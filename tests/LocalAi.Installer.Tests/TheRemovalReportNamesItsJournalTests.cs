using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The saved removal report names its journal, and a journal that could not record how the run
/// ended, in the reader's language (#614).
/// </summary>
public sealed class TheRemovalReportNamesItsJournalTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    private string LogDirectory => Path.Combine(machine.Root, "reports");

    [Theory]
    [InlineData(InstallerLanguage.English, "Journal: ")]
    [InlineData(InstallerLanguage.Russian, "Журнал: ")]
    public async Task The_saved_report_names_the_journal(InstallerLanguage language, string label)
    {
        var wizard = await Confirmed(language);

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(label + Journal(), File.ReadAllLines(wizard.ReportPath!)[^1]);
    }

    [Theory]
    [InlineData(InstallerLanguage.English, "The journal could not record how this run ended: ")]
    [InlineData(InstallerLanguage.Russian, "Не удалось записать в журнал, чем закончился этот запуск: ")]
    public async Task A_journal_that_could_not_record_the_end_is_said_so(InstallerLanguage language, string label)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wizard = await Confirmed(language);
        FileStream? held = null;
        try
        {
            // The runner checks for cancellation right after reporting 60, and writes no journal
            // step in between, so the one write the held file refuses is the outcome.
            Assert.False(await wizard.RunAsync(stop.Token, new Observe(step =>
            {
                if (step.Percent == 60 && held is null)
                {
                    held = new FileStream(Journal(), FileMode.Open, FileAccess.Read, FileShare.Read);
                    stop.Cancel();
                }
            })));
        }
        finally
        {
            held?.Dispose();
        }

        var line = Assert.Single(
            File.ReadAllLines(wizard.ReportPath!),
            item => item.StartsWith(label, StringComparison.Ordinal));
        Assert.True(line.Length > label.Length);
    }

    private string Journal() => Assert.Single(Directory.GetFiles(LogDirectory, "journal-*.json"));

    private async Task<UninstallWizardViewModel> Confirmed(InstallerLanguage language)
    {
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true,
            machine.Layout, machine.Home, new Exits(), machine.HooksPathReader)
        {
            LogDirectory = LogDirectory,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        wizard.Presentation.ChooseLanguage(language);
        return wizard;
    }

    private sealed class Observe(Action<UninstallProgress> observe) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value) => observe(value);
    }

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }
}
