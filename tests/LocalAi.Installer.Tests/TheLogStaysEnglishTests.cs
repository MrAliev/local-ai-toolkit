using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The run log is written beside an English journal and is English, and the window speaks
/// whatever was chosen on the start screen. Model steps were worded inside the installer core,
/// which picks by the process language, so a Russian window put «gpt-oss:20b: проверяется,
/// помещается ли в видеопамять» under "No prerequisites were selected."
///
/// One record of facts, two readers, two wordings — the same shape
/// <see cref="AgentChoiceMapping.EnglishTitle"/> already uses for the same reason.
/// </summary>
public sealed class TheLogStaysEnglishTests : IDisposable
{
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose() => InstallerCulture.Current = original;

    [Fact]
    public void A_model_step_reaches_the_log_in_english_whatever_the_window_speaks()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        // Through the same call the wizard makes, not only through the wording underneath it:
        // one of the two is picked at the point the line is appended, and that is the choice
        // this is about.
        Assert.Equal(
            "model-a: downloading",
            ModelStepWording.LogLine(Step(ModelProvisioningPhase.Downloading)));
    }

    [Fact]
    public void The_same_step_reaches_the_window_in_the_window_s_language()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        RussianText.AssertRussian(
            ModelStepWording.ForTheRail(Step(ModelProvisioningPhase.Checking)),
            "model step");
    }

    /// <summary>
    /// Invariant, as the console prints it: a window and a terminal beside it disagreeing about
    /// the size of one download is worse than a decimal point in the wrong half of Europe.
    /// </summary>
    [Fact]
    public void A_download_is_measured_the_same_way_in_both()
    {
        var step = Step(
            ModelProvisioningPhase.Downloading,
            completedBytes: 5_046_586_573,
            totalBytes: 13_743_895_347);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Contains("4.7", ModelStepWording.ForTheRail(step), StringComparison.Ordinal);
        Assert.Contains("12.8", ModelStepWording.ForTheRail(step), StringComparison.Ordinal);
        Assert.Contains("4.7", ModelStepWording.ForTheLog(step), StringComparison.Ordinal);
    }

    /// <summary>
    /// A status token from Ollama is an identifier, and identifiers are quoted as they arrive in
    /// both languages.
    /// </summary>
    [Fact]
    public void A_status_token_is_never_translated()
    {
        var step = Step(ModelProvisioningPhase.Other, status: "pulling manifest");

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Contains(
            "pulling manifest",
            ModelStepWording.ForTheRail(step),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The log does not scroll. A model download reports a line per progress update, and a
    /// hundred byte counts per model would push everything worth reading out of sight.
    /// </summary>
    [Fact]
    public void A_byte_count_moves_the_rail_and_stays_out_of_the_log()
    {
        var counting = Step(
            ModelProvisioningPhase.Downloading,
            completedBytes: 1,
            totalBytes: 2);

        Assert.NotEmpty(ModelStepWording.ForTheRail(counting));
        Assert.Null(ModelStepWording.LogLine(counting));
        Assert.NotNull(ModelStepWording.LogLine(Step(ModelProvisioningPhase.Downloading)));
        Assert.NotNull(ModelStepWording.LogLine(Step(ModelProvisioningPhase.Checking)));
    }

    /// <summary>
    /// A verdict is left to the batch result, which carries the same news with its code and the
    /// smaller context sizes that would have fitted. Logged from here as well, every model would
    /// be reported twice and the shorter report would come first.
    /// </summary>
    [Theory]
    [InlineData(ModelProvisioningPhase.Accepted)]
    [InlineData(ModelProvisioningPhase.Rejected)]
    [InlineData(ModelProvisioningPhase.TrialLoadFailed)]
    [InlineData(ModelProvisioningPhase.Planned)]
    public void A_verdict_is_not_the_log_s_to_write(ModelProvisioningPhase phase) =>
        Assert.Null(ModelStepWording.LogLine(Step(phase)));

    /// <summary>
    /// The tally moved into the log when the finish page grew rows, and it went on picking the
    /// window's language: the log a Russian machine wrote ended in six Russian lines under
    /// "Finalising." — the very mixing the model steps had just been fixed for.
    /// </summary>
    [Fact]
    public void The_tally_is_english_whatever_the_window_speaks()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.DoesNotMatch("[а-яА-ЯёЁ]", RunResults.Tally(2, 1, 0, 1));
    }

    /// <summary>
    /// And it says only what the log does not already have. The package was reported twice —
    /// once in English where it was installed, once in Russian here.
    /// </summary>
    [Fact]
    public void The_tally_does_not_repeat_the_package_line()
    {
        var tally = RunResults.Tally(2, 1, 0, 1);

        Assert.DoesNotContain("package", tally, StringComparison.OrdinalIgnoreCase);
        foreach (var figure in new[] { "2", "1", "0" })
        {
            Assert.Contains(figure, tally, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The log said «listed as "Local AI Toolkit" 0.2.1-RC16» on a machine where that entry is
    /// not in the registry. The line was written from what the installer had just tried to write,
    /// not from what is there — so it could only ever agree with itself.
    /// </summary>
    [Fact]
    public void The_apps_and_features_line_reports_what_was_read_back()
    {
        var line = RunResults.AppsAndFeatures(
            new UninstallEntry("Local AI Toolkit", "0.2.1-RC16", "MrAliev", "run", "here", 0),
            @"C:\LocalAi\bin\uninstall\LocalAi.Installer.exe");

        Assert.Contains("Local AI Toolkit", line, StringComparison.Ordinal);
        Assert.Contains("0.2.1-RC16", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_is_not_there_when_read_back_says_so()
    {
        var line = RunResults.AppsAndFeatures(
            null,
            @"C:\LocalAi\bin\uninstall\LocalAi.Installer.exe");

        Assert.DoesNotContain("listed as", line, StringComparison.Ordinal);
        Assert.Contains("not there", line, StringComparison.OrdinalIgnoreCase);
        // Still installed and still removable, which is the part somebody needs to know.
        Assert.Contains("Remove", line, StringComparison.Ordinal);
    }

    private static ModelProvisioningProgress Step(
        ModelProvisioningPhase phase,
        long completedBytes = 0,
        long totalBytes = 0,
        string? status = null) =>
        new("model-a", 2048, phase, 0, 2, completedBytes, totalBytes, status);
}
