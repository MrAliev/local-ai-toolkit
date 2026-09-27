using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// With networking turned off an adapter can still be up, so the system check said "connected to
/// a network" and the models page offered downloads without a word (Windows Sandbox run
/// 20260927-213146-clean-install-folder on RC30). Offline for models is now the Ollama registry
/// not answering; the adapters only say why.
/// </summary>
public sealed class OfflineMeansTheRegistryDoesNotAnswerTests
{
    private const string AdaptersUnreadable = "The adapters could not be listed.";

    private const string RegistryReviewEnglish =
        " — the Ollama registry does not answer, so only models already on this computer can be set up";

    private const string RegistryReviewRussian =
        " — реестр Ollama не отвечает, поэтому настроить можно только модели, которые уже есть на " +
        "этом компьютере";

    public static TheoryData<ObservationState, bool, CheckStatus, string, string> NetworkRows => new()
    {
        {
            ObservationState.Available, true, CheckStatus.Ok,
            "online: the Ollama registry answers",
            "есть доступ в интернет: реестр Ollama отвечает"
        },
        {
            ObservationState.Unavailable, true, CheckStatus.Ok,
            "online: the Ollama registry answers",
            "есть доступ в интернет: реестр Ollama отвечает"
        },
        {
            ObservationState.Failed, true, CheckStatus.Ok,
            "online: the Ollama registry answers",
            "есть доступ в интернет: реестр Ollama отвечает"
        },
        {
            ObservationState.Available, false, CheckStatus.Warning,
            "connected to a network, but the Ollama registry does not answer — models can come only " +
            "from this computer",
            "подключение к сети есть, но реестр Ollama не отвечает — модели можно взять только с " +
            "этого компьютера"
        },
        {
            ObservationState.Unavailable, false, CheckStatus.Warning,
            "no network connection",
            "подключения к сети нет"
        },
        {
            ObservationState.Failed, false, CheckStatus.Warning,
            "the Ollama registry does not answer, and the network adapters could not be read: " +
            AdaptersUnreadable,
            "реестр Ollama не отвечает, а сетевые адаптеры прочитать не удалось: " + AdaptersUnreadable
        },
    };

    [Theory]
    [MemberData(nameof(NetworkRows))]
    public async Task The_network_row_says_first_whether_the_registry_answers(
        ObservationState adapters,
        bool registryAnswers,
        CheckStatus mark,
        string english,
        string russian)
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-registry");
        var wizard = await Initialized(temp, adapters, registryAnswers);

        var row = Assert.Single(wizard.Diagnose.Checks, check => check.Name == "Network");
        Assert.Equal(mark, row.Status);
        Assert.Equal(english, row.Detail);

        InstallerCulture.In(InstallerLanguage.Russian, () =>
        {
            wizard.Diagnose.Relabel();
            return 0;
        });
        Assert.Equal(russian, row.Detail);
    }

    [Theory]
    [InlineData(ObservationState.Available)]
    [InlineData(ObservationState.Failed)]
    public async Task Without_the_registry_the_page_starts_on_skip_and_says_the_registry_does_not_answer(
        ObservationState adapters)
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-registry");
        var wizard = await Initialized(temp, adapters, registryAnswers: false);

        Assert.True(wizard.Models.IsOffline);
        Assert.True(wizard.Models.IsSkip);
        Assert.True(wizard.Models.HasNetworkNote);
        Assert.Equal(
            "The Ollama registry does not answer: models that are not on this computer yet cannot be " +
            "downloaded now, so they cannot be set up.",
            wizard.Models.NetworkNote);
        Assert.Equal(
            "Реестр Ollama не отвечает: модели, которых ещё нет на этом компьютере, сейчас скачать не " +
            "получится, и настроить их не удастся.",
            InstallerCulture.In(InstallerLanguage.Russian, () => wizard.Models.NetworkNote));
    }

    [Theory]
    [InlineData(ModelSelectionMode.Automatic)]
    [InlineData(ModelSelectionMode.ChooseExact)]
    public async Task Without_the_registry_the_review_says_only_models_already_here_can_be_set_up(
        ModelSelectionMode chosen)
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-registry");
        var wizard = await Initialized(temp, ObservationState.Available, registryAnswers: false);

        Choose(wizard.Models, chosen);

        Assert.EndsWith(RegistryReviewEnglish, wizard.Models.ReviewText, StringComparison.Ordinal);
        Assert.Contains(RegistryReviewEnglish, wizard.ReviewText, StringComparison.Ordinal);
        Assert.EndsWith(
            RegistryReviewRussian,
            InstallerCulture.In(InstallerLanguage.Russian, () => wizard.Models.ReviewText),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_answers_is_online_whatever_the_adapters_say()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-registry");
        var wizard = await Initialized(temp, ObservationState.Unavailable, registryAnswers: true);

        Assert.False(wizard.Models.IsOffline);
        Assert.True(wizard.Models.IsAutomatic);
        Assert.False(wizard.Models.HasNetworkNote);
        Assert.DoesNotContain("only models already on this computer", wizard.Models.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>The mode the installation recorded is the person's choice (#781); no offline default replaces it.</summary>
    [Fact]
    public async Task A_recorded_mode_still_stands_when_the_registry_does_not_answer()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-registry");
        Directory.CreateDirectory(temp.Combine("runtime"));
        ModelSetupRecord.Apply(
            temp.Combine("runtime"),
            new ModelProvisioningSelection(ModelProvisioningMode.Automatic));

        var wizard = await Initialized(
            temp,
            ObservationState.Available,
            registryAnswers: false,
            StartChoice.UpdateOrRepair);

        Assert.True(wizard.Models.IsAutomatic);
        Assert.EndsWith(RegistryReviewEnglish, wizard.Models.ReviewText, StringComparison.Ordinal);
    }

    private static void Choose(ModelsPageViewModel page, ModelSelectionMode mode)
    {
        if (mode == ModelSelectionMode.ChooseExact)
        {
            page.IsChooseExact = true;
        }
        else
        {
            page.IsAutomatic = true;
        }
    }

    private static async Task<InstallerWizardViewModel> Initialized(
        TemporaryDirectory temp,
        ObservationState adapters,
        bool registryAnswers,
        StartChoice start = StartChoice.Install)
    {
        var wizard = new InstallerWizardViewModel(
            start,
            () => null,
            new OfflineSizesComeFromTheReleaseTests.ReleaseFeed(
                OfflineSizesComeFromTheReleaseTests.Release(OfflineSizesComeFromTheReleaseTests.CatalogueTags())),
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported(
                network: new NetworkSnapshot(
                    adapters,
                    adapters == ObservationState.Failed ? AdaptersUnreadable : null))))
        {
            ModelSizeSource = new OfflineSizesComeFromTheReleaseTests.FixedRegistry(
                null,
                answers: () => registryAnswers),
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = temp.Combine("runtime"),
            ScratchRoot = temp.Combine("scratch"),
            LocateOllamaStore = _ => null,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }
}
