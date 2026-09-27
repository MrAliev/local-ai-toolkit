using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The binaries on the review page and the whole finish report read top to bottom in the order
/// the removal runs: the other paths, the PATH entry, the Apps &amp; features entry, and last the
/// path holding the copy that entry runs. An entry the run held back after a failure is reported
/// as kept, and why, rather than as not started.
/// </summary>
public sealed class TheRemovalReadsInTheOrderItRunsTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public TheRemovalReadsInTheOrderItRunsTests()
    {
        Directory.CreateDirectory(Copy);
        File.WriteAllText(Path.Combine(Copy, UninstallRegistration.UninstallerFileName), "installer");
    }

    public void Dispose() => machine.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Copy => machine.Layout.UninstallerDirectory;

    private string Installer => machine.Layout.InstallerDirectory;

    private string Jobs => Path.Combine(machine.Runtime, "jobs");

    [Fact]
    public async Task The_binaries_on_the_review_page_are_listed_in_the_order_the_run_takes_them()
    {
        var plan = await Plan();

        var group = ReviewGroup.ForRemoval(plan)[0].Lines
            .SkipWhile(line => line != RemovalMatrix.Title(RemovalItem.Binaries) + ":")
            .Skip(1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                "  remove " + machine.Layout.BinRoot + "\\",
                "  take the launcher off this account's PATH",
                "  remove the Apps & features entry, then the uninstaller copy it runs",
                "  remove " + Installer + "\\",
            ],
            group);
    }

    [Fact]
    public async Task The_report_names_the_path_holding_the_copy_after_the_entry()
    {
        var plan = await Plan();
        using var journal = Journal();
        Assert.True((await Runner().ApplyAsync(plan, journal, Token)).Succeeded);

        var lines = Lines(plan, journal);

        var path = Array.IndexOf(lines, "Removed from PATH: " + machine.Layout.LauncherDirectory);
        Assert.Equal("Registration processed: " + Copy, lines[path + 1]);
        Assert.Equal("Removed " + Installer, lines[path + 2]);
        Assert.All(
            plan.Paths.Where(entry => !entry.HoldsUninstaller),
            entry => Assert.InRange(Array.IndexOf(lines, "Removed " + entry.Path), 0, path - 1));
    }

    /// <summary>
    /// Stopped at the PATH step, what did not start comes last and together, and nothing held
    /// the entry back: it is not started rather than kept.
    /// </summary>
    [Fact]
    public async Task A_removal_stopped_at_the_path_step_lists_what_it_did_not_start_last()
    {
        var plan = await Plan();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var journal = Journal();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(stop.Cancel).ApplyAsync(plan, journal, stop.Token));

        var lines = Lines(plan, journal);
        var path = Array.IndexOf(lines, "Removed from PATH: " + machine.Layout.LauncherDirectory);
        Assert.Equal("Not started: " + Copy, lines[path + 1]);
        Assert.Equal("Not started: " + Installer, lines[path + 2]);
    }

    [Theory]
    [InlineData(
        InstallerLanguage.English,
        "Kept",
        "an earlier step failed, so the Apps & features entry stays and the removal can be " +
        "started again from there",
        "holds the uninstaller the Apps & features entry runs")]
    [InlineData(
        InstallerLanguage.Russian,
        "Оставлено",
        "предыдущий шаг не удался, поэтому запись в «Приложениях и возможностях» остаётся и " +
        "удаление можно запустить оттуда снова",
        "здесь деинсталлятор, который запускает запись в «Приложениях и возможностях»")]
    public async Task An_entry_held_back_after_a_failure_is_reported_as_kept_and_why(
        InstallerLanguage language, string kept, string entryDetail, string holderDetail)
    {
        var plan = await Plan();
        using var journal = Journal();
        using (new FileStream(Path.Combine(Jobs, "job.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False((await Runner().ApplyAsync(plan, journal, Token)).Succeeded);
        }

        InstallerCulture.Current = language;

        var lines = Lines(plan, journal);
        var path = Array.FindIndex(
            lines,
            line => line.EndsWith(machine.Layout.LauncherDirectory, StringComparison.Ordinal));
        Assert.Equal(kept + ": " + Copy + " — " + entryDetail, lines[path + 1]);
        Assert.Equal(kept + ": " + Installer + " — " + holderDetail, lines[path + 2]);
        Assert.InRange(
            Array.FindIndex(lines, line => line.Contains(Jobs + " — ", StringComparison.Ordinal)),
            0,
            path - 1);
        var rows = UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
            .Select(RemovalReportPresentation.Row)
            .ToArray();
        Assert.Contains(new RunResultRow(RunMark.None, Copy, kept + " — " + entryDetail), rows);
        Assert.Contains(new RunResultRow(RunMark.None, Installer, kept + " — " + holderDetail), rows);
    }

    /// <summary>
    /// A full removal whose PATH step and entry are both planned, over an installation whose
    /// copy is parked in installer\uninstall. The registry key is one nothing writes.
    /// </summary>
    private async Task<UninstallPlan> Plan()
    {
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            Token,
            readUserPath: () => machine.Layout.LauncherDirectory) with
        {
            RemovesAppsAndFeaturesEntry = true,
        };
        Assert.True(plan.RemovesLauncherFromPath);
        Assert.True(plan.Paths.Single(entry => entry.Path == Installer).HoldsUninstaller);
        return plan;
    }

    private InstallerRunJournal Journal() =>
        InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));

    private UninstallRunner Runner(Action? onPathWrite = null) =>
        new(
            machine.Layout,
            new Exits(),
            registrySubKey: RemovalFixture.UnusedRegistrySubKey,
            userPath: new UserPath(machine.Layout.LauncherDirectory, onPathWrite));

    private string[] Lines(UninstallPlan plan, InstallerRunJournal journal) =>
        UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
            .Select(RemovalReportPresentation.Line)
            .ToArray();

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }

    private sealed class UserPath(string value, Action? onWrite) : IUserPathStore
    {
        private string? value = value;

        public string? Read() => value;

        public void Write(string written)
        {
            value = written;
            onWrite?.Invoke();
        }
    }
}
