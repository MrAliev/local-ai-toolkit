using System.Xml.Linq;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Offline, "Choose automatically" ends in a failed models step on a computer that has no models
/// yet, and neither the page nor the review said what the missing network costs.
/// </summary>
public sealed class OfflineModelChoiceTests
{
    private const string OfflineReviewEnglish =
        " — no network connection, so only models already on this computer can be set up";

    private const string OfflineReviewRussian =
        " — подключения к сети нет, поэтому настроить можно только модели, которые уже есть на " +
        "этом компьютере";

    [Fact]
    public async Task Offline_the_page_starts_on_skip()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-choice");
        var wizard = await Initialized(temp, _ => true);

        Assert.True(wizard.Models.IsSkip);
        Assert.Equal(ModelProvisioningMode.None, wizard.Models.BuildProvisioningSelection().Mode);
        Assert.Equal("Models: skipped, nothing will be downloaded", wizard.Models.ReviewText);
    }

    [Fact]
    public async Task With_a_network_the_page_starts_on_automatic_and_the_review_adds_nothing()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-choice");
        var wizard = await Initialized(temp, _ => false);

        Assert.True(wizard.Models.IsAutomatic);
        Assert.DoesNotContain("network", wizard.Models.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Until_the_person_chooses_the_mode_follows_the_network()
    {
        var page = new ModelsPageViewModel { Offline = ModelsOffline.NoNetworkConnection };
        Assert.True(page.IsSkip);

        page.Offline = ModelsOffline.None;

        Assert.True(page.IsAutomatic);
    }

    /// <summary>The run checks the system again, and so will a page that offers "Check again".</summary>
    [Theory]
    [InlineData(ModelSelectionMode.Automatic)]
    [InlineData(ModelSelectionMode.ChooseExact)]
    [InlineData(ModelSelectionMode.Skip)]
    public void A_mode_the_person_chose_stays_whatever_the_network_does(ModelSelectionMode chosen)
    {
        var page = new ModelsPageViewModel { Offline = ModelsOffline.NoNetworkConnection };
        Choose(page, chosen);

        page.Offline = ModelsOffline.None;
        Assert.Equal(chosen, page.Mode);
        page.Offline = ModelsOffline.NoNetworkConnection;
        Assert.Equal(chosen, page.Mode);
    }

    /// <summary>
    /// The review named the mode the run was started on. A check during the run that finds the
    /// network back must not turn a skipped models step into downloads nobody was shown.
    /// </summary>
    [Fact]
    public async Task A_check_during_the_run_keeps_the_mode_the_review_named()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-choice");
        var wizard = await Initialized(temp, check => check == 1);
        Assert.True(wizard.Models.IsSkip);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);
        Assert.True(wizard.CanRun);

        await wizard.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(wizard.Models.IsOffline);
        Assert.True(wizard.Models.IsSkip);
    }

    [Theory]
    [InlineData(ModelSelectionMode.Automatic)]
    [InlineData(ModelSelectionMode.ChooseExact)]
    public async Task Offline_the_review_says_only_models_already_here_can_be_set_up(ModelSelectionMode chosen)
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-choice");
        var wizard = await Initialized(temp, _ => true);

        Choose(wizard.Models, chosen);

        Assert.EndsWith(OfflineReviewEnglish, wizard.Models.ReviewText, StringComparison.Ordinal);
        Assert.Contains(OfflineReviewEnglish, wizard.ReviewText, StringComparison.Ordinal);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.EndsWith(OfflineReviewRussian, wizard.Models.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>The note stands above the choice and explains it, so no choice hides it.</summary>
    [Theory]
    [InlineData(ModelSelectionMode.Automatic)]
    [InlineData(ModelSelectionMode.ChooseExact)]
    [InlineData(ModelSelectionMode.Skip)]
    public async Task Offline_the_note_shows_whichever_mode_is_chosen(ModelSelectionMode chosen)
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-choice");
        var wizard = await Initialized(temp, _ => true);

        Choose(wizard.Models, chosen);

        Assert.True(wizard.Models.HasNetworkNote);
    }

    /// <summary>
    /// A bare line with no wrapping cut the Russian note at 150 %, and it read as muted text
    /// rather than as the warning the video memory rule already gets on the same page.
    /// </summary>
    [Fact]
    public void The_note_is_the_pages_warning_box_between_the_rule_line_and_the_models()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var markup = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"));
        var note = markup.Descendants(presentation + "TextBlock")
            .Single(block => block.Attribute("Text")?.Value == "{Binding Models.NetworkNote}");
        var residencyBox = markup.Descendants(presentation + "Border")
            .Single(border => border.Attribute("Visibility")?.Value.Contains(
                "Residency.HasWarning", StringComparison.Ordinal) == true);

        Assert.Equal("Wrap", note.Attribute("TextWrapping")?.Value);
        var box = note.Parent!;
        Assert.Equal(presentation + "Border", box.Name);
        foreach (var attribute in new[] { "Background", "BorderBrush", "BorderThickness", "CornerRadius", "Padding" })
        {
            Assert.Equal(residencyBox.Attribute(attribute)?.Value, box.Attribute(attribute)?.Value);
        }

        Assert.Equal("0,0,0,8", box.Attribute("Margin")?.Value);
        Assert.Contains("Models.HasNetworkNote", box.Attribute("Visibility")?.Value, StringComparison.Ordinal);
        Assert.Equal("{Binding Models.RuleSummary}", box.ElementsBeforeSelf().Last().Attribute("Text")?.Value);
        Assert.Equal(presentation + "GroupBox", box.ElementsAfterSelf().First().Name);
    }

    private static void Choose(ModelsPageViewModel page, ModelSelectionMode mode)
    {
        switch (mode)
        {
            case ModelSelectionMode.Automatic:
                page.IsAutomatic = true;
                break;
            case ModelSelectionMode.ChooseExact:
                page.IsChooseExact = true;
                break;
            default:
                page.IsSkip = true;
                break;
        }
    }

    /// <summary><paramref name="offline"/> answers for each check of the system, counted from 1.</summary>
    private static async Task<InstallerWizardViewModel> Initialized(
        TemporaryDirectory temp,
        Func<int, bool> offline)
    {
        var checks = 0;
        var online = false;
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            new OfflineSizesComeFromTheReleaseTests.ReleaseFeed(
                OfflineSizesComeFromTheReleaseTests.Release(OfflineSizesComeFromTheReleaseTests.CatalogueTags())),
            detectEnvironment: _ =>
            {
                online = !offline(++checks);
                return Task.FromResult(HermeticDiagnosis.Supported(
                    network: new NetworkSnapshot(
                        online ? ObservationState.Available : ObservationState.Unavailable,
                        null)));
            })
        {
            ModelSizeSource = new OfflineSizesComeFromTheReleaseTests.FixedRegistry(null, answers: () => online),
            EnableDependencyActions = true,
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = temp.Combine("no-runtime-root"),
            ScratchRoot = temp.Combine("scratch"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    private static void WalkToTheReview(InstallerWizardViewModel wizard)
    {
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }
    }
}
