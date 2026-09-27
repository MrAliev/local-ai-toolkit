using System.ComponentModel;
using System.Text.Json;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Offline and without WinGet, Git and Ollama failed in milliseconds and the log said only
/// "Git: failed.", the journal "The install command did not succeed." The reason was worded and
/// then handed to a property nothing shows, and the run overwrote it with the log anyway.
/// </summary>
public sealed class APrerequisiteThatFailedSaysWhyTests
{
    private const string ScipTypeScriptLink = "https://www.npmjs.com/package/@sourcegraph/scip-typescript";

    /// <summary>
    /// WinGet was there when Git and Ollama were ticked and gone when the run started. The run
    /// does what the review showed: it tries them, and each row says why it did not install.
    /// </summary>
    [Fact]
    public async Task Without_winget_the_log_the_journal_and_the_rows_say_why_git_was_not_installed()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-cause");
        var logs = root.Combine("logs");
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(
                git: Missing("git"),
                winGet: Missing("winget")),
            root,
            page =>
            {
                page.SetConsent("Git", true);
                page.SetConsent("Ollama", true);
            });

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause =
            "WinGet is unavailable; install Git manually from https://git-scm.com/install/windows.";
        Assert.Contains(cause, wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.Contains(
            cause,
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "install-*.log"))),
            StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite Git (Git.Git)"));
        Assert.Equal(
            [
                (RunMark.Failed, "Prerequisites", "installed 0 of 2, failed 2"),
                (RunMark.Failed, "Git",
                    "not installed: WinGet was not found. Install it yourself: https://git-scm.com/install/windows"),
                (RunMark.Failed, "Ollama",
                    "not installed: WinGet was not found. Install it yourself: https://ollama.com/download/windows"),
            ],
            PrerequisiteRows(wizard));
    }

    [Fact]
    public async Task Without_npm_the_log_and_the_journal_say_why_an_indexer_was_not_installed()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-npm");
        var logs = root.Combine("logs");
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(npm: Missing("npm")),
            root,
            page =>
            {
                FoundTheRequiredOnes(page);
                page.SetConsent("ScipTypeScript", true);
            });

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause = "npm is unavailable; install Node.js 20 before SCIP TypeScript.";
        Assert.Contains(cause, wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite SCIP TypeScript (@sourcegraph/scip-typescript)"));
        Assert.Contains(
            (RunMark.Failed, "SCIP TypeScript",
                "not installed: npm was not found. With Node.js 20 installed, run this installer again"),
            PrerequisiteRows(wizard));
    }

    /// <summary>
    /// npm that cannot be started installed nothing, and nothing after it depends on it. The run
    /// used to end there, leaving the package uninstalled for a reason unrelated to it.
    /// </summary>
    [Fact]
    public async Task A_command_that_could_not_be_started_moves_on_to_the_next_one()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-start");
        var logs = root.Combine("logs");
        var runner = new ScriptedRunner(arguments => arguments.Contains("@sourcegraph/scip-typescript@0.4.0")
            ? throw new Win32Exception(2, "The system cannot find the file specified.")
            : new ProcessResult(1, string.Empty, string.Empty, false, false));
        var wizard = ConfirmedInstall(HermeticDiagnosis.Supported(), root, TickBothIndexers, runner);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause =
            "SCIP TypeScript was not installed: npm could not be started: The system cannot find the file specified.";
        Assert.Contains(
            "SCIP TypeScript: failed." + Environment.NewLine + cause,
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite SCIP TypeScript (@sourcegraph/scip-typescript)"));
        Assert.Contains(runner.Started, arguments => arguments.Contains("@sourcegraph/scip-python@0.6.6"));
        Assert.Contains(
            "LocalAi package: no verified release was selected",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        var rows = PrerequisiteRows(wizard);
        Assert.Contains(
            (RunMark.Failed, "SCIP TypeScript",
                "not installed: npm could not be started (The system cannot find the file specified.). " +
                "Install it yourself: " + ScipTypeScriptLink),
            rows);
        Assert.Contains(
            (RunMark.Failed, "SCIP Python",
                "not installed: npm ended with exit code 1. Install it yourself: " +
                "https://www.npmjs.com/package/@sourcegraph/scip-python"),
            rows);
    }

    /// <summary>
    /// A process that could not be stopped may still be running, so the run stops. Its component
    /// was attempted and its outcome is unknown, which is what its row says.
    /// </summary>
    [Fact]
    public async Task A_command_that_could_not_be_stopped_still_ends_the_run()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-stuck");
        var runner = new ScriptedRunner(_ => throw new ProcessTerminationException(
            42, ProcessTerminationCause.Timeout, "Process tree 42 did not confirm exit."));
        var wizard = ConfirmedInstall(HermeticDiagnosis.Supported(), root, TickBothIndexers, runner);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Single(runner.Started);
        Assert.Contains(
            "SCIP TypeScript: failed." + Environment.NewLine +
            "SCIP TypeScript: npm could not be stopped; the installation may still be running.",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Contains("Failed: Process tree 42 did not confirm exit.", wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAi package:", wizard.Finish.Progress, StringComparison.Ordinal);
        var rows = PrerequisiteRows(wizard);
        Assert.Contains(
            (RunMark.Warned, "SCIP TypeScript",
                "outcome unknown: npm could not be stopped, so the installation may still be running. Wait " +
                "for it to end before running this installer again"),
            rows);
        Assert.Contains((RunMark.None, "SCIP Python", "not attempted"), rows);
    }

    /// <summary>
    /// npm installed SCIP Python and the check after it could not be started. The run goes on, and
    /// the log names the check rather than giving the exception's message alone.
    /// </summary>
    [Fact]
    public async Task A_check_after_installing_that_could_not_be_started_says_which_check()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-check");
        var logs = root.Combine("logs");
        var runner = new ScriptedRunner(arguments => arguments.Contains("root")
            ? throw new Win32Exception(2, "The system cannot find the file specified.")
            : new ProcessResult(0, string.Empty, string.Empty, false, false));
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(),
            root,
            page =>
            {
                FoundTheRequiredOnes(page);
                page.SetConsent("ScipPython", true);
            },
            runner);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause =
            "SCIP Python was installed, but the check after installing failed: The system cannot find the file specified.";
        Assert.Contains(
            "SCIP Python: failed." + Environment.NewLine + cause,
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite SCIP Python (@sourcegraph/scip-python)"));
        Assert.Contains(
            "LocalAi package: no verified release was selected",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Contains(
            (RunMark.Failed, "SCIP Python",
                "installed by npm, but the check after installing failed. The log below says what went wrong"),
            PrerequisiteRows(wizard));
    }

    [Fact]
    public async Task A_command_stopped_at_its_time_limit_says_so()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-timeout");
        var logs = root.Combine("logs");
        var runner = new ScriptedRunner(_ => new ProcessResult(null, string.Empty, string.Empty, true, false));
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(),
            root,
            page =>
            {
                FoundTheRequiredOnes(page);
                page.SetConsent("ScipTypeScript", true);
            },
            runner);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause = "SCIP TypeScript installation did not finish within 10 minutes and was stopped.";
        Assert.Contains(cause, wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite SCIP TypeScript (@sourcegraph/scip-typescript)"));
        Assert.Contains(
            (RunMark.Failed, "SCIP TypeScript",
                "not installed: npm did not finish within 10 min and was stopped. Install it yourself: " +
                ScipTypeScriptLink),
            PrerequisiteRows(wizard));
    }

    [Fact]
    public async Task A_command_cut_short_by_cancelling_says_the_run_was_cancelled()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-cancel");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new ScriptedRunner(_ =>
        {
            stop.Cancel();
            return new ProcessResult(null, string.Empty, string.Empty, false, true);
        });
        var wizard = ConfirmedInstall(HermeticDiagnosis.Supported(), root, TickBothIndexers, runner);

        Assert.False(await wizard.RunAsync(stop.Token));

        Assert.True(wizard.IsCanceled);
        Assert.Contains(
            "SCIP TypeScript was not installed: the run was cancelled.",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        // Cancelled is for the one that was running; the run never reached the next one.
        Assert.Single(runner.Started);
        var rows = PrerequisiteRows(wizard);
        Assert.Contains((RunMark.Failed, "SCIP TypeScript", "not installed: the run was cancelled"), rows);
        Assert.Contains((RunMark.None, "SCIP Python", "not attempted"), rows);
    }

    [Fact]
    public async Task Winget_that_answered_leaves_a_row_per_component()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-winget");
        var runner = new ScriptedRunner(arguments => new ProcessResult(
            arguments.Contains("Ollama.Ollama") ? 0 : arguments.Contains("GitHub.cli") ? 5 : 0,
            string.Empty,
            string.Empty,
            false,
            false));
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(),
            root,
            page =>
            {
                page.SetConsent("Git", true);
                page.SetConsent("Ollama", true);
                page.SetConsent("GitHubCli", true);
            },
            runner,
            new TrustEverything());

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        // Found by the check, so Git was a reinstall.
        Assert.Equal(
            [
                (RunMark.Failed, "Prerequisites", "installed 2 of 3, failed 1"),
                (RunMark.Done, "Git", "reinstalled"),
                (RunMark.Done, "Ollama", "reinstalled"),
                (RunMark.Failed, "GitHub CLI",
                    "not installed: winget ended with exit code 5. Install it yourself: https://cli.github.com/"),
            ],
            PrerequisiteRows(wizard));
    }

    [Fact]
    public async Task Winget_that_failed_the_safety_check_points_at_the_log()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-refused");
        var runner = new ScriptedRunner(_ => new ProcessResult(0, string.Empty, string.Empty, false, false));
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(git: Missing("git")),
            root,
            page =>
            {
                page.SetConsent("Git", true);
                page.SetInstalled("Ollama", true);
            },
            runner,
            new TrustNothing());

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Empty(runner.Started);
        Assert.Contains(
            "Git was not installed. The winget on this machine is not signed by Microsoft Corporation. " +
            "It will not be run. Reinstall App Installer from the Microsoft Store. You can install it " +
            "yourself from https://git-scm.com/install/windows.",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Contains(
            (RunMark.Failed, "Git",
                "not installed: winget on this computer failed the installer's safety check. The log below " +
                "says why and what to do; you can also install it yourself: https://git-scm.com/install/windows"),
            PrerequisiteRows(wizard));
    }

    private static void TickBothIndexers(DependenciesPageViewModel page)
    {
        FoundTheRequiredOnes(page);
        page.SetConsent("ScipTypeScript", true);
        page.SetConsent("ScipPython", true);
    }

    /// <summary>What the check says of Git and Ollama when it finds them, so Next does not wait on them.</summary>
    private static void FoundTheRequiredOnes(DependenciesPageViewModel page)
    {
        page.SetInstalled("Git", true);
        page.SetInstalled("Ollama", true);
    }

    private static DependencySnapshot Missing(string name) =>
        new(name, DependencyState.NotFound, null, null, null);

    /// <summary>The prerequisite rows the finish page shows: the count, then one per component.</summary>
    private static (RunMark, string, string)[] PrerequisiteRows(InstallerWizardViewModel wizard) =>
        [.. wizard.Finish.Results
            .SkipWhile(row => row.Title != PageLabels.ResultPrerequisites)
            .TakeWhile(row => row.Title != PageLabels.LocalModels)
            .Select(row => (row.Mark, row.Title, row.Detail))];

    /// <summary>An installation walked page by page, with nothing outside the test's directory.</summary>
    private static InstallerWizardViewModel ConfirmedInstall(
        EnvironmentDiagnosis diagnosis,
        TemporaryDirectory root,
        Action<DependenciesPageViewModel> choose,
        IProcessRunner? runner = null,
        IWinGetExecutableTrust? trust = null)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            detectEnvironment: _ => Task.FromResult(diagnosis))
        {
            EnableDependencyActions = true,
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Combine("no-runtime-root"),
            ScratchRoot = root.Combine("scratch"),
            ProcessRunner = runner ?? new ScriptedRunner(_ => throw new InvalidOperationException("Nothing runs here.")),
            WinGetTrust = trust ?? new TrustNothing(),
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        Assert.Equal(InstallerPage.Dependencies, wizard.CurrentPage);
        choose(wizard.Dependencies);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            if (wizard.CurrentPage == InstallerPage.Models)
            {
                wizard.Models.Mode = ModelSelectionMode.Skip;
            }

            Assert.True(wizard.MoveNext());
        }

        wizard.SetReviewConfirmed(true);
        return wizard;
    }

    private static string? StepDetail(string logs, string description)
    {
        using var journal = JsonDocument.Parse(
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "journal-*.json"))));
        var step = Assert.Single(
            journal.RootElement.GetProperty("Steps").EnumerateArray(),
            candidate => candidate.GetProperty("Description").GetString() == description);
        return step.GetProperty("Detail").GetString();
    }

    /// <summary>Answers each command from the script and remembers the arguments it was given.</summary>
    private sealed class ScriptedRunner(Func<IReadOnlyList<string>, ProcessResult> answer) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Started { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Started.Add(arguments);
            return Task.FromResult(answer(arguments));
        }
    }

    private sealed class TrustEverything : IWinGetExecutableTrust
    {
        private static readonly TrustedExecutable Winget =
            new(@"C:\absent\winget.exe", "winget", "Microsoft Corporation");

        public ExecutableTrustResult Resolve(string snapshotPath) => new(ExecutableTrustStatus.Trusted, Winget);

        public ExecutableTrustResult Revalidate(TrustedExecutable executable) =>
            new(ExecutableTrustStatus.Trusted, Winget);
    }

    private sealed class TrustNothing : IWinGetExecutableTrust
    {
        public ExecutableTrustResult Resolve(string snapshotPath) => new(ExecutableTrustStatus.UntrustedPublisher, null);

        public ExecutableTrustResult Revalidate(TrustedExecutable executable) =>
            new(ExecutableTrustStatus.UntrustedPublisher, null);
    }
}
