using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A client configuration that Cancel interrupts puts back every file it had replaced before it
/// lets the cancellation through, yet its step stayed Running in a journal with an outcome and
/// the report said "Результат неизвестен" (RC31 sandbox run 20260928-161545-removal-edge,
/// cancel-clients, where the files were byte-for-byte as before).
/// </summary>
public sealed class ACancelledClientStepSaysNothingChangedTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task The_step_is_closed_as_undone_and_its_files_are_as_they_were()
    {
        var (plan, journal, before) = await CancelInsideTheFirstClient();
        using (journal)
        {
            var step = Assert.Single(journal.Snapshot.Steps, step => step.Kind == InstallerRunEffectKind.AgentConfiguration);
            Assert.Equal(InstallerRunStepStatus.Undone, step.Status);
            Assert.Equal("Cancelled before it finished; its files are as they were before it started.", step.Detail);
            Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
        }
    }

    [Theory]
    [InlineData(InstallerLanguage.English, "Cancelled, unchanged: {0} — LocalAi still connected",
        "Kept: {0} — the copy used to restore {1}")]
    [InlineData(InstallerLanguage.Russian, "Отменено, без изменений: {0} — LocalAi по-прежнему подключён",
        "Оставлено: {0} — копия, из которой восстановлен файл {1}")]
    public async Task The_report_says_it_was_cancelled_and_nothing_changed(
        InstallerLanguage language, string stepLine, string backupLine)
    {
        var (plan, journal, _) = await CancelInsideTheFirstClient();
        using (journal)
        {
            InstallerCulture.Current = language;
            var configuration = plan.AgentConfigurations.First(item => item.HasChanges);
            var lines = UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
                .Select(RemovalReportPresentation.Line)
                .ToArray();

            Assert.Contains(
                string.Format(stepLine, string.Join(", ", configuration.Files.Select(file => file.Path))),
                lines);
            var replaced = configuration.Files.First(file => File.Exists(file.BackupPath));
            Assert.Contains(string.Format(backupLine, replaced.BackupPath, replaced.Path), lines);
            Assert.DoesNotContain(lines, line => line.StartsWith("Outcome unknown", StringComparison.Ordinal) ||
                line.StartsWith("Результат неизвестен", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Cancels as the first client's first file has been swapped and read back: the apply's next
    /// file then sees the cancellation and puts the first one back.
    /// </summary>
    private async Task<(UninstallPlan Plan, InstallerRunJournal Journal, Dictionary<string, byte[]> Before)> CancelInsideTheFirstClient()
    {
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            TestContext.Current.CancellationToken);
        var first = plan.AgentConfigurations.First(item => item.HasChanges);
        Assert.True(first.Files.Count(file => !file.Deletes) > 1, "the first client needs a second file to see the cancellation");
        var before = first.Files.Where(file => File.Exists(file.Path))
            .ToDictionary(file => file.Path, file => File.ReadAllBytes(file.Path));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UninstallRunner(machine.Layout, new Exits(), readBackOverride: path =>
                {
                    stop.Cancel();
                    return File.ReadAllBytes(path);
                })
                .ApplyAsync(plan, journal, stop.Token));
        return (plan, journal, before);
    }

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }
}
