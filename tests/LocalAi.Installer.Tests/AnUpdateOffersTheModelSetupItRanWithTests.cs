using System.Text;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Updating an installation set up with "skip model setup" offered to set up all seven models
/// (Windows Sandbox run 20260927-040916-update-from-rc19, check U04-models-line). An update or
/// repair now offers the mode the installation ran with; one from before that was recorded is
/// judged by what Ollama already holds.
/// </summary>
public sealed class AnUpdateOffersTheModelSetupItRanWithTests
{
    private const string SkippedReview = "Models: skipped, nothing will be downloaded";

    private const string OfflineReview =
        " — no network connection, so only models already on this computer can be set up";

    [Fact]
    public async Task An_installation_set_up_without_models_is_offered_skip_again()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, ModelProvisioningSelection.None);

        var wizard = await Update(temp, offline: false);

        Assert.True(wizard.Models.IsSkip);
        Assert.Contains(SkippedReview, wizard.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installation_set_up_with_one_model_is_offered_that_model()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        var model = new ModelsPageViewModel().CatalogModels[0];
        var recorded = new ModelProvisioningSelection(ModelProvisioningMode.Exact, model.Tag, model.ContextTokens[1]);
        Record(temp, recorded);

        var wizard = await Update(temp, offline: false);

        Assert.True(wizard.Models.IsChooseExact);
        Assert.Equal(recorded, wizard.Models.BuildProvisioningSelection());
        Assert.Contains(
            $"Models: {model.Tag} with a {model.ContextTokens[1]} token context",
            wizard.ReviewText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Offline the page starts on Skip only until the person chooses, and the recorded mode is
    /// their earlier choice.
    /// </summary>
    [Fact]
    public async Task A_recorded_mode_is_the_persons_choice_so_being_offline_does_not_replace_it()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, new ModelProvisioningSelection(ModelProvisioningMode.Automatic));

        var wizard = await Update(temp, offline: true);

        Assert.True(wizard.Models.IsAutomatic);
        Assert.Contains(OfflineReview, wizard.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>What is offered is still a choice: the person changes it, and theirs is what the run keeps.</summary>
    [Fact]
    public async Task A_mode_the_person_picks_over_the_recorded_one_is_kept_and_recorded()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, ModelProvisioningSelection.None);
        var wizard = await Update(temp, offline: false);

        wizard.RevealSettings();
        wizard.Models.IsAutomatic = true;
        wizard.Models.Offline = ModelsOffline.NoNetworkConnection;
        wizard.Models.Offline = ModelsOffline.None;
        wizard.ApplyPackageResult(Installed(temp));
        wizard.RecordModelSetup(new StringBuilder());

        Assert.True(wizard.Models.IsAutomatic);
        Assert.Equal(new ModelProvisioningSelection(ModelProvisioningMode.Automatic), ModelSetupRecord.Read(Runtime(temp)));
    }

    /// <summary>
    /// The mode of the installation that is there: a run whose package did not install leaves the
    /// record of the one before it.
    /// </summary>
    [Fact]
    public async Task A_run_records_its_mode_once_its_package_is_installed()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, new ModelProvisioningSelection(ModelProvisioningMode.Automatic));
        var wizard = await Update(temp, offline: false);
        wizard.Models.IsSkip = true;

        wizard.RecordModelSetup(new StringBuilder());
        Assert.Equal(new ModelProvisioningSelection(ModelProvisioningMode.Automatic), ModelSetupRecord.Read(Runtime(temp)));

        wizard.ApplyPackageResult(Installed(temp));
        wizard.RecordModelSetup(new StringBuilder());
        Assert.Equal(ModelProvisioningSelection.None, ModelSetupRecord.Read(Runtime(temp)));
    }

    [Fact]
    public void Set_up_without_models_the_page_stays_on_skip_whatever_the_network_does()
    {
        var page = new ModelsPageViewModel();

        page.StartOnSkip();
        Assert.True(page.IsSkip);

        page.Offline = ModelsOffline.NoNetworkConnection;
        page.Offline = ModelsOffline.None;
        Assert.True(page.IsSkip);
    }

    [Fact]
    public async Task Without_a_record_an_Ollama_holding_none_of_the_models_is_offered_skip()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Directory.CreateDirectory(Runtime(temp));
        var store = Store(temp, "llama3/latest");

        var wizard = await Update(temp, offline: false, store);

        Assert.True(wizard.Models.IsSkip);
        Assert.Contains(SkippedReview, wizard.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store holding a catalogue model says models were set up, not which mode did it, so the
    /// default stands — Skip while offline, as for any installation nobody has chosen for.
    /// </summary>
    [Theory]
    [InlineData(false, ModelSelectionMode.Automatic)]
    [InlineData(true, ModelSelectionMode.Skip)]
    public async Task Without_a_record_an_Ollama_holding_one_of_the_models_leaves_the_default(
        bool offline,
        ModelSelectionMode expected)
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Directory.CreateDirectory(Runtime(temp));
        var tag = new ModelsPageViewModel().CatalogModels[0].Tag.Replace(':', '/');
        var store = Store(temp, tag);

        var wizard = await Update(temp, offline, store);

        Assert.Equal(expected, wizard.Models.Mode);
    }

    [Fact]
    public async Task Without_a_record_or_a_readable_store_the_default_stands()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Directory.CreateDirectory(Runtime(temp));

        var wizard = await Update(temp, offline: false, store: null);

        Assert.True(wizard.Models.IsAutomatic);
    }

    /// <summary>A model the catalogue no longer carries cannot be offered, so the record is set aside.</summary>
    [Fact]
    public async Task A_recorded_model_the_catalogue_dropped_is_judged_like_no_record()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, new ModelProvisioningSelection(ModelProvisioningMode.Exact, "retired-model:1b", 2048));
        var store = Store(temp);

        var wizard = await Update(temp, offline: false, store);

        Assert.True(wizard.Models.IsSkip);
    }

    /// <summary>
    /// Offering something else in its place without a word is the silent change of #256, so the
    /// review names the choice it could not offer, between the models line and the memory rule.
    /// </summary>
    [Fact]
    public async Task A_recorded_model_this_installer_does_not_offer_is_named_under_the_models_line()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        Record(temp, new ModelProvisioningSelection(ModelProvisioningMode.Exact, "retired-model:1b", 2048));

        var wizard = await Update(temp, offline: false, Store(temp));

        Assert.Equal(
            new[]
            {
                SkippedReview,
                "The last installation chose retired-model:1b with a 2048 token context; this installer does not offer it",
                wizard.Residency.ReviewText,
            },
            ModelsGroup(wizard));

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "В прошлой установке была выбрана модель retired-model:1b с контекстом 2048 токенов; этот установщик такую не предлагает",
            ModelsGroup(wizard)[1]);
    }

    /// <summary>The models line already says what the run downloads; where the mode came from is no news.</summary>
    [Theory]
    [InlineData(ModelProvisioningMode.None)]
    [InlineData(ModelProvisioningMode.Automatic)]
    [InlineData(ModelProvisioningMode.Exact)]
    public async Task A_mode_carried_over_from_the_last_installation_has_no_note(ModelProvisioningMode mode)
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-offer");
        var model = new ModelsPageViewModel().CatalogModels[0];
        Record(temp, mode == ModelProvisioningMode.Exact
            ? new ModelProvisioningSelection(mode, model.Tag, model.ContextTokens[0])
            : new ModelProvisioningSelection(mode));

        var wizard = await Update(temp, offline: false);

        Assert.Equal(new[] { wizard.Models.ReviewText, wizard.Residency.ReviewText }, ModelsGroup(wizard));
    }

    private static IReadOnlyList<string> ModelsGroup(InstallerWizardViewModel wizard) =>
        Assert.Single(wizard.ReviewGroups, group => group.Title == PageLabels.ReviewModels).Lines;

    private static string Runtime(TemporaryDirectory temp) => temp.Combine("runtime");

    private static void Record(TemporaryDirectory temp, ModelProvisioningSelection selection)
    {
        Directory.CreateDirectory(Runtime(temp));
        ModelSetupRecord.Apply(Runtime(temp), selection);
    }

    /// <summary>Ollama's default store under a profile of the test's own, holding a manifest per name/tag given.</summary>
    private static OllamaModelStore Store(TemporaryDirectory temp, params string[] models)
    {
        var directory = temp.Combine("profile", ".ollama", "models");
        Directory.CreateDirectory(Path.Combine(directory, "manifests"));
        foreach (var model in models)
        {
            var file = Path.Combine([directory, "manifests", "registry.ollama.ai", "library", .. model.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");
        }

        return new OllamaModelStore(directory, IsDefault: true);
    }

    private static ReleaseInstallResult Installed(TemporaryDirectory temp) =>
        new(LocalAiPackageInstallStatus.Installed, "0.2.1-test", null, temp.Path, null);

    private static async Task<InstallerWizardViewModel> Update(
        TemporaryDirectory temp,
        bool offline,
        OllamaModelStore? store = null)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            new OfflineSizesComeFromTheReleaseTests.ReleaseFeed(
                OfflineSizesComeFromTheReleaseTests.Release(OfflineSizesComeFromTheReleaseTests.CatalogueTags())),
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported(
                network: new NetworkSnapshot(
                    offline ? ObservationState.Unavailable : ObservationState.Available,
                    null))))
        {
            ModelSizeSource = new OfflineSizesComeFromTheReleaseTests.FixedRegistry(null, answers: () => !offline),
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = Runtime(temp),
            ScratchRoot = temp.Combine("scratch"),
            LocateOllamaStore = _ => store,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }
}
