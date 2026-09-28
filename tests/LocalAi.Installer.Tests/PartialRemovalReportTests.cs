using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

public sealed class PartialRemovalReportTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_after_a_real_deletion_retains_the_result_and_unstarted_work(bool fail)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var plan = await machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly), stop.Token,
            installationFollows: true);
        var first = plan.Paths[0].Path;
        var later = plan.Paths[1].Path;
        var wizard = await Create();

        Assert.False(await wizard.RunAsync(stop.Token, new Observe(step =>
        {
            if (step.Text.EndsWith(first, StringComparison.Ordinal) && !Directory.Exists(first) && !File.Exists(first))
            {
                if (fail) throw new InvalidOperationException("controlled failure after deletion");
                stop.Cancel();
            }
        })));

        Assert.False(Directory.Exists(first) || File.Exists(first));
        Assert.True(Directory.Exists(later) || File.Exists(later));
        Assert.Contains("Removed " + first, wizard.Report, StringComparison.Ordinal);
        Assert.Contains("Not started: " + later, wizard.Report, StringComparison.Ordinal);
        Assert.Contains("Kept:", wizard.Report, StringComparison.Ordinal);
        Assert.False(wizard.CanContinueToInstall);
        Assert.True(wizard.HasReportPath);
        var saved = File.ReadAllText(wizard.ReportPath!);
        Assert.Contains("Removed " + first, saved, StringComparison.Ordinal);
        Assert.Contains(wizard.Results, row => row.Title == first && row.Mark == RunMark.Done);
        var path = wizard.ReportPath!;
        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);
        Assert.Contains("Удалено " + first, wizard.Report, StringComparison.Ordinal);
        Assert.Equal(saved, File.ReadAllText(path));
        wizard.DetachPresentation();
        Directory.Delete(machine.Runtime, recursive: true);
        Assert.Equal(saved, File.ReadAllText(path));
    }

    [Fact]
    public async Task An_unfinished_journal_intent_stays_unknown_even_when_the_target_is_gone()
    {
        var plan = await machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly),
            TestContext.Current.CancellationToken, installationFollows: true);
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        var completed = plan.Paths[0].Path;
        var unknown = plan.Paths[1].Path;
        var first = journal.BeginStep(InstallerRunEffectKind.RuntimeRemoval, UninstallJournalDescriptions.Path(completed));
        Remove(completed);
        journal.CompleteStep(first, "Removed.", false);
        journal.BeginStep(InstallerRunEffectKind.RuntimeRemoval, UninstallJournalDescriptions.Path(unknown));
        Remove(unknown);

        var report = UninstallReport.Capture(plan, journal.Snapshot, machine.Layout);

        Assert.Contains(report, entry => entry.Target == completed && entry.State == RemovalReportState.Completed);
        Assert.Contains(report, entry => entry.Target == unknown && entry.State == RemovalReportState.Unknown);
        Assert.Contains(report, entry => entry.Target == plan.Paths[2].Path && entry.State == RemovalReportState.NotStarted);
    }

    [Fact]
    public async Task A_save_failure_preserves_results_and_allows_saving_elsewhere()
    {
        var blocked = Path.Combine(machine.Root, "not-a-directory");
        File.WriteAllText(blocked, "keep this file");
        var wizard = await Create();
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken, new Observe(step =>
        {
            if (step.Percent >= 90) wizard.LogDirectory = blocked;
        })));

        Assert.False(wizard.HasReportPath);
        Assert.True(wizard.HasReportSaveError);
        Assert.Contains("Copy the details", wizard.ReportSaveError, StringComparison.Ordinal);
        Assert.Contains("Removed ", wizard.ReportText, StringComparison.Ordinal);
        Assert.Equal("keep this file", File.ReadAllText(blocked));
        var destination = Path.Combine(machine.Root, "saved-elsewhere.txt");
        Assert.True(wizard.SaveReport(destination));
        Assert.False(wizard.HasReportSaveError);
        Assert.Equal(wizard.ReportText, File.ReadAllText(destination));
        Assert.True(wizard.CanContinueToInstall);
    }

    [Fact]
    public async Task A_report_directory_inside_the_runtime_is_rejected_before_deletion()
    {
        var wizard = await Create();
        wizard.LogDirectory = Path.Combine(machine.Runtime, "report");
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(machine.LauncherPath));
        Assert.False(wizard.HasReportPath);
        Assert.True(wizard.HasReportSaveError);
        Assert.DoesNotContain(wizard.Results, row => row.Mark == RunMark.Done);
    }

    /// <summary>#813: the refusal read "…runtime LocalAi.. Скопируйте…" in the RC32 sandbox.</summary>
    [Theory]
    [InlineData(InstallerLanguage.English,
        "Could not save the report: Choose a report folder outside the LocalAi runtime being removed. Copy the details below.")]
    [InlineData(InstallerLanguage.Russian,
        "Не удалось сохранить отчёт: Выберите папку отчётов вне удаляемого рантайма LocalAi. Скопируйте сведения ниже.")]
    public async Task The_refused_report_folder_reads_as_one_clean_sentence(InstallerLanguage language, string expected)
    {
        InstallerCulture.Current = language;
        var wizard = await Create();
        wizard.LogDirectory = Path.Combine(machine.Runtime, "report");

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, wizard.ReportSaveError);
    }

    private static void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private async Task<UninstallWizardViewModel> Create()
    {
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true,
            machine.Layout, machine.Home, new Stopped(), machine.HooksPathReader)
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        return wizard;
    }

    private sealed class Observe(Action<UninstallProgress> observe) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value) => observe(value);
    }

    private sealed class Stopped : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }
}
