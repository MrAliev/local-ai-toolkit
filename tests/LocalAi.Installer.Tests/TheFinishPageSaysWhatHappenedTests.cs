using System.Text;
using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The finish page's words against what the run recorded: a cancelled check is not a refusal,
/// a client left alone for want of a package was skipped rather than cut short, a package
/// installer's error is not a layout refusal unless the layout refused, and a choice the run
/// could not store is an action that did not complete.
/// </summary>
public sealed class TheFinishPageSaysWhatHappenedTests
{
    private const string NoModelPassed = "No selected model passed validation.";

    /// <summary>
    /// A cancelled run's model rows are cancelled or not reached; none of them was refused, so
    /// advice about a refusal answers a question the run never asked.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_does_not_report_its_models_as_refused()
    {
        var wizard = await RunEndedByTheCheck(cancelled: true);

        Assert.True(wizard.IsCanceled);
        Assert.DoesNotContain(NoModelPassed, wizard.Finish.NextSteps!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run checks models against the stored rule and stores this run's choice afterwards, so
    /// "the memory policy has not been relaxed" was a claim nothing checked.
    /// </summary>
    [Fact]
    public async Task A_run_whose_models_all_failed_says_where_to_look()
    {
        var wizard = await RunEndedByTheCheck(cancelled: false);

        Assert.Contains(
            NoModelPassed + " If a model was refused for lack of memory, choose a smaller model " +
            "or context on the next run; if its check failed, read its details above before " +
            "retrying.",
            wizard.Finish.NextSteps!,
            StringComparison.Ordinal);
        Assert.DoesNotContain("relaxed", wizard.Finish.NextSteps!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a package the clients are left alone on purpose. "The run stopped before client
    /// setup" described a run that did not stop, and "a launcher that is not there" and
    /// "LocalAi was not installed" named reasons nothing checked: on a repair both launcher and
    /// installation are there. What decides it is that this run did not install the package.
    /// </summary>
    [Fact]
    public async Task Clients_left_alone_without_a_package_are_reported_as_skipped()
    {
        using var logs = new TemporaryDirectory("LocalAi-finish-skipped");
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
            RuntimeRoot = logs.Combine("no-runtime-root"),
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        wizard.Agents.SetChoice("claude", AgentChoice.McpAndInstructions);
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.False(wizard.IsCanceled);
        var rows = wizard.Finish.Results
            .Where(row => row.Title is "Claude Code" or "Codex" or "Claude Code hooks")
            .ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal("skipped: this run did not install the LocalAi package", row.Detail);
            Assert.Equal(RunMark.None, row.Mark);
        });
        Assert.Contains(
            "Client applications: skipped, because this run did not install the LocalAi package.",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.DoesNotContain("not there", wizard.Finish.Progress, StringComparison.Ordinal);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "пропущено: этот запуск не установил пакет LocalAi",
            RunResults.Client(new ClientRunOutcome("Codex", ClientRunState.Skipped)).Detail);
    }

    /// <summary>
    /// The package installer wraps any I/O or access error in the same exception type its layout
    /// check throws, so the type alone does not make it a refusal.
    /// </summary>
    [Theory]
    [InlineData("LocalAi package installation failed.", PackageOutcomeKind.Failed)]
    [InlineData("An installed file failed identity verification.", PackageOutcomeKind.Failed)]
    [InlineData(
        "The LocalAi installation layout is unsafe (check: ValidateAcl).",
        PackageOutcomeKind.Refused)]
    [InlineData("The installation layout is unsafe.", PackageOutcomeKind.Refused)]
    public void Only_a_layout_refusal_is_reported_as_one(string message, PackageOutcomeKind expected)
    {
        Assert.Equal(expected, PackageOutcome.FromInstallationFailure(message).Kind);
    }

    [Fact]
    public void A_failed_package_says_it_stopped_with_an_error()
    {
        Assert.Equal(
            "not installed: the installation stopped with an error. Technical details: " +
            "LocalAi package installation failed.",
            RunResults.PackageDetail(
                new PackageOutcome(PackageOutcomeKind.Failed, Reason: "LocalAi package installation failed."),
                installed: false));
    }

    /// <summary>
    /// For a root without versions the advice in the log is to delete it, so the row sends the
    /// reader there instead of telling them not to delete anything.
    /// </summary>
    [Fact]
    public void A_refused_package_points_at_the_advice_in_the_log()
    {
        Assert.Equal(
            "not installed: the installation directory failed safety checks. The log below says " +
            "what to do with it. Technical details: The installation layout is unsafe.",
            RunResults.PackageDetail(
                new PackageOutcome(PackageOutcomeKind.Refused, Reason: "The installation layout is unsafe."),
                installed: false));
    }

    /// <summary>
    /// The update check was ticked on the confirm page. When it could not be stored the run is
    /// still an installation, but not one where every selected action completed.
    /// </summary>
    [Fact]
    public async Task A_setting_that_could_not_be_stored_is_an_action_that_did_not_complete()
    {
        using var root = new TemporaryDirectory("LocalAi-finish-partial");
        // A directory where the file goes, so the write is refused.
        Directory.CreateDirectory(Path.Combine(root.Path, "settings", "update-check.json"));
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Path,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.Review.EnableUpdateCheck = true;
        wizard.SetReviewConfirmed(true);
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("All selected actions completed.", wizard.StepDescription);

        wizard.ApplyUpdateCheckPolicy(new StringBuilder());

        Assert.Equal("Update complete", wizard.StepTitle);
        Assert.Equal(
            "Some actions did not complete. The log below shows what happened.",
            wizard.StepDescription);
        Assert.True(wizard.OpensTheLog);
    }

    /// <summary>
    /// The memory rule the run could not store failed the run, and only the log said so: the
    /// finish page listed every other thing the run touched and not this one. The row names the
    /// rule in the words the review offered it, in the language on screen when it is read.
    /// </summary>
    [Fact]
    public async Task A_memory_rule_that_could_not_be_stored_has_its_own_row()
    {
        using var root = new TemporaryDirectory("LocalAi-finish-policy");
        var runtime = root.Combine("runtime");
        // A directory where the file goes, so the write is refused.
        Directory.CreateDirectory(Path.Combine(runtime, "settings", BrokerPolicy.FileName));
        var wizard = RunOver(
            runtime,
            root.Combine("logs"),
            answers => answers.Residency.Policy = ModelResidencyPolicy.AllowPartialOffload);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        var row = Assert.Single(wizard.Finish.Results, row => row.Title == "Video memory");
        Assert.Equal(RunMark.Failed, row.Mark);
        Assert.StartsWith(
            "the setting “part of the model in system memory” was not saved. Technical details: ",
            row.Detail,
            StringComparison.Ordinal);

        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);

        var russian = Assert.Single(wizard.Finish.Results, row => row.Title == "Видеопамять");
        Assert.Equal(RunMark.Failed, russian.Mark);
        Assert.StartsWith(
            "правило «часть модели в системной памяти» не сохранено. Технические сведения: ",
            russian.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A repair that keeps both settings has nothing to store, so a read-only settings file is
    /// no reason for anything to fail, and the journal holds no step for a write that never
    /// happened.
    /// </summary>
    [Fact]
    public async Task Settings_the_run_keeps_are_not_written_again()
    {
        using var root = new TemporaryDirectory("LocalAi-finish-kept");
        var runtime = root.Combine("runtime");
        Directory.CreateDirectory(runtime);
        var policy = new ModelResidencyPolicyStore(runtime);
        policy.Write(policy.Read() with { ModelResidency = ModelResidencyPolicy.RequireFullVram });
        new UpdateCheckPolicyStore(runtime).Write(UpdateCheckPolicy.Default);
        var settings = Directory.EnumerateFiles(Path.Combine(runtime, "settings")).ToArray();
        foreach (var file in settings)
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        try
        {
            var logs = root.Combine("logs");
            var wizard = RunOver(runtime, logs);
            wizard.Residency.Policy = ModelResidencyPolicy.RequireFullVram;
            wizard.Review.EnableUpdateCheck = false;

            await wizard.RunAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Could not store", wizard.Finish.Progress, StringComparison.Ordinal);
            Assert.DoesNotContain(wizard.Finish.Results, row => row.Title == "Video memory");
            var journal = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "journal-*.json")));
            Assert.DoesNotContain("Model residency policy", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("Update check policy", journal, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in settings)
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    /// <summary>
    /// A package-less run over a runtime root the test owns, its answers given before the box is
    /// ticked: an answer changed after the tick takes the tick back.
    /// </summary>
    private static InstallerWizardViewModel RunOver(
        string runtime,
        string logs,
        Action<InstallerWizardViewModel>? answers = null)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = logs,
            RuntimeRoot = runtime,
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        answers?.Invoke(wizard);
        wizard.SetReviewConfirmed(true);
        return wizard;
    }

    private static async Task<InstallerWizardViewModel> RunEndedByTheCheck(bool cancelled)
    {
        using var logs = new TemporaryDirectory("LocalAi-finish-models");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        InstallerWizardViewModel? wizard = null;
        wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair, detectEnvironment: token =>
        {
            wizard!.ApplyModelStep(new("model-a", 2048, ModelProvisioningPhase.Planned, 0, 1));
            wizard.ApplyModelStep(new("model-a", 2048, ModelProvisioningPhase.Checking, 0, 1));
            if (cancelled)
            {
                stop.Cancel();
                return Task.FromCanceled<EnvironmentDiagnosis>(token);
            }

            return Task.FromException<EnvironmentDiagnosis>(new InvalidOperationException("diagnostic failure"));
        })
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(stop.Token));
        Assert.True(wizard.Finish.HasNextSteps);
        return wizard;
    }
}
