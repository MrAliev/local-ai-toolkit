using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A click on Cancel is a message to the window's thread and is handled only when that thread is
/// free. The removal used to run on it, so a click made after the first path waited until the
/// last one was gone and the run reported itself complete.
/// </summary>
public sealed class CancelStopsTheRemovalTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task A_cancel_clicked_after_the_first_path_stops_before_the_next_and_the_report_says_which()
    {
        var token = TestContext.Current.CancellationToken;
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly), token, installationFollows: true);
        var first = plan.Paths[0].Path;
        var untouched = plan.Paths.Skip(1).Select(entry => entry.Path).ToArray();
        Assert.NotEmpty(untouched);

        await OnTheWindowThread(async dispatcher =>
        {
            var wizard = await Create(token);
            var finished = await wizard.RunAsync(token, new Observe(step =>
            {
                if (!step.Text.EndsWith(first, StringComparison.Ordinal)) return;
                var click = dispatcher.InvokeAsync(() => wizard.CancelCommand.Execute(null));
                // The window's own thread cannot wait for a message it has yet to handle; a
                // removal running there carries on, as it did in front of the person clicking.
                if (!dispatcher.CheckAccess()) click.Task.Wait(token);
            }));

            Assert.All(untouched, path => Assert.True(
                Directory.Exists(path) || File.Exists(path),
                path + " was removed after Cancel was clicked."));
            Assert.False(finished);
            Assert.False(Directory.Exists(first) || File.Exists(first));
            Assert.StartsWith("Removal stopped.", wizard.Summary, StringComparison.Ordinal);
            Assert.Contains("Removed " + first, wizard.Report, StringComparison.Ordinal);
            Assert.All(untouched, path =>
                Assert.Contains("Not started: " + path, wizard.Report, StringComparison.Ordinal));

            var journal = InstallerRunJournal.Load(
                Directory.GetFiles(wizard.LogDirectory, "journal-*.json").Single()).Snapshot;
            Assert.Equal(InstallerRunOutcome.Cancelled, journal.Outcome);
            var removals = journal.Steps.Where(step => step.Kind == InstallerRunEffectKind.RuntimeRemoval).ToArray();
            var removal = Assert.Single(removals);
            Assert.Equal(UninstallJournalDescriptions.Path(first), removal.Description);
            Assert.Equal(InstallerRunStepStatus.Completed, removal.Status);
        });
    }

    [Fact]
    public async Task No_step_of_the_removal_runs_on_the_window_thread()
    {
        var token = TestContext.Current.CancellationToken;
        var onTheWindowThread = new List<string>();

        await OnTheWindowThread(async dispatcher =>
        {
            var wizard = await Create(token);
            Assert.True(await wizard.RunAsync(token, new Observe(step =>
            {
                if (dispatcher.CheckAccess()) lock (onTheWindowThread) onTheWindowThread.Add(step.Text);
            })));
        });

        // The last report comes just before the PATH step, which leaves the calling thread itself.
        Assert.Empty(onTheWindowThread);
    }

    private async Task<UninstallWizardViewModel> Create(CancellationToken token)
    {
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true,
            machine.Layout, machine.Home, new Stopped(), machine.HooksPathReader)
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(token);
        Assert.True(await wizard.MoveNextAsync(token));
        wizard.IsConfirmed = true;
        return wizard;
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the window would: on a Dispatcher, whose context every
    /// await in the wizard comes back to.
    /// </summary>
    private static Task OnTheWindowThread(Func<Dispatcher, Task> body) => WpfTestThread.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        dispatcher.InvokeAsync(async () =>
        {
            try { await body(dispatcher); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

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
