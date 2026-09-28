using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A stopped or failed removal put the journal path at the end of its summary and again as the
/// report's last line, and a refused one said "Nothing was removed." twice running (RC31 sandbox
/// run 20260928-145527-remove-cancel-partial). The journal has a line of its own now.
/// </summary>
public sealed class TheRemovalReportNamesEachThingOnceTests : IDisposable
{
    public TheRemovalReportNamesEachThingOnceTests() => InstallerCulture.Current = InstallerLanguage.English;

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removal_that_did_not_finish_names_its_journal_once(bool fail)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var plan = await machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly), stop.Token,
            installationFollows: true);
        var first = plan.Paths[0].Path;
        var wizard = await Create(new Stopped());

        Assert.False(await wizard.RunAsync(stop.Token, new Observe(step =>
        {
            if (step.Text.EndsWith(first, StringComparison.Ordinal) && !Directory.Exists(first) && !File.Exists(first))
            {
                if (fail) throw new InvalidOperationException("controlled failure after deletion.");
                stop.Cancel();
            }
        })));

        var journal = Path.GetFileName(Directory.GetFiles(wizard.LogDirectory, "journal-*.json").Single());
        Assert.DoesNotContain(journal, wizard.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("..", wizard.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Journal: ", wizard.JournalLine, StringComparison.Ordinal);
        Assert.EndsWith(journal, wizard.JournalLine, StringComparison.Ordinal);
        Assert.Equal(1, Mentions(wizard.ReportText, journal));
        Assert.EndsWith(Environment.NewLine + wizard.JournalLine, wizard.ReportText, StringComparison.Ordinal);
        Assert.Equal(1, Mentions(File.ReadAllText(wizard.ReportPath!), journal));
    }

    [Fact]
    public async Task A_finished_removal_names_its_journal_once()
    {
        var wizard = await Create(new Stopped());

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        var journal = Path.GetFileName(Directory.GetFiles(wizard.LogDirectory, "journal-*.json").Single());
        Assert.EndsWith(journal, wizard.JournalLine, StringComparison.Ordinal);
        Assert.Equal(1, Mentions(wizard.ReportText, journal));
        Assert.EndsWith(Environment.NewLine + wizard.JournalLine, wizard.ReportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_removal_says_nothing_was_removed_once()
    {
        var wizard = await Create(new Refusing());

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Nothing was removed.", wizard.Summary, StringComparison.Ordinal);
        Assert.Equal(1, Mentions(wizard.ReportText, "Nothing was removed."));
        Assert.StartsWith("Failed: stopping LocalAi's running tools and broker", wizard.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_run_names_no_journal()
    {
        var wizard = await Create(new Stopped());

        Assert.Equal(string.Empty, wizard.JournalLine);
    }

    private static int Mentions(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private async Task<UninstallWizardViewModel> Create(IProcessRunner runner)
    {
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true,
            machine.Layout, machine.Home, runner, machine.HooksPathReader)
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

    private sealed class Refusing : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(1, "", "broker_still_running", false, false));
    }
}
