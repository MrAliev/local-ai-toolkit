using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A clean install from a folder on RC30 reviewed «Модели: пропущены, ничего не будет скачано»
/// and finished on «Локальные модели — не выполнялось», as though the run had stopped before the
/// models. A skip says it was one; "not attempted" is left for models chosen and not reached.
/// </summary>
public sealed class SkippedModelsSaySoOnTheFinishPageTests
{
    [Fact]
    public void Skipped_models_say_nothing_was_downloaded()
    {
        var row = RunResults.Models([], skipped: true);

        Assert.Equal(RunMark.None, row.Mark);
        Assert.Equal("skipped, nothing was downloaded", row.Detail);
    }

    [Fact]
    public void Skipped_models_say_so_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal("пропущены, ничего не скачано", RunResults.Models([], skipped: true).Detail);
    }

    [Fact]
    public async Task A_run_whose_models_were_skipped_says_nothing_was_downloaded()
    {
        using var logs = new TemporaryDirectory("LocalAi-models-skipped");

        var row = await ModelsRowAfterARun(logs, ModelSelectionMode.Skip);

        Assert.Equal(RunMark.None, row.Mark);
        Assert.Equal("skipped, nothing was downloaded", row.Detail);
    }

    [Fact]
    public async Task Models_chosen_and_not_reached_are_still_not_attempted()
    {
        using var logs = new TemporaryDirectory("LocalAi-models-not-reached");

        var row = await ModelsRowAfterARun(logs, ModelSelectionMode.Automatic);

        Assert.Equal(RunMark.None, row.Mark);
        Assert.Equal("not attempted", row.Detail);
    }

    /// <summary>A real run, stopped by the check it makes before the package.</summary>
    private static async Task<RunResultRow> ModelsRowAfterARun(
        TemporaryDirectory logs,
        ModelSelectionMode mode)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ => Task.FromException<EnvironmentDiagnosis>(
                new InvalidOperationException("the test stops the run here")))
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
            RuntimeRoot = logs.Combine("no-runtime-root"),
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.Models.Mode = mode;
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard.Finish.Results.Single(row => row.Title == PageLabels.LocalModels);
    }
}
