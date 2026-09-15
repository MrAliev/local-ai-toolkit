using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class ModelRunProgressTests
{
    [Fact]
    public void Reports_are_delivered_on_the_captured_context_in_order()
    {
        var context = new QueuedContext();
        var received = new List<ModelProvisioningPhase>();
        using var progress = new ModelRunProgress(step => received.Add(step.Phase), context);

        progress.Report(Step(ModelProvisioningPhase.Planned));
        progress.Report(Step(ModelProvisioningPhase.Accepted));
        Assert.Empty(received);
        context.Deliver();

        Assert.Equal([ModelProvisioningPhase.Planned, ModelProvisioningPhase.Accepted], received);
    }

    [Fact]
    public void Closing_delivers_already_reported_verdicts_before_the_batch_is_summarised()
    {
        var context = new QueuedContext();
        var wizard = new InstallerWizardViewModel();
        using var progress = new ModelRunProgress(wizard.ApplyModelStep, context);
        progress.Report(Step(ModelProvisioningPhase.Planned));
        progress.Report(Step(ModelProvisioningPhase.Accepted));

        progress.Dispose();

        Assert.Equal(ModelRunState.Ready, Assert.Single(wizard.ModelRuns).State);
        Assert.Equal(RunMark.Done, RunResults.Models(wizard.ModelRuns).Mark);
    }

    [Fact]
    public void Posted_callbacks_and_reports_from_a_closed_run_cannot_change_the_next_run()
    {
        var context = new QueuedContext();
        var received = new List<string>();
        using var oldRun = new ModelRunProgress(step => received.Add(step.Model), context);
        oldRun.Report(Step(ModelProvisioningPhase.Planned));
        oldRun.Dispose();
        received.Clear();
        using var nextRun = new ModelRunProgress(step => received.Add(step.Model), context);

        nextRun.Report(Step(ModelProvisioningPhase.Planned) with { Model = "next-model" });
        oldRun.Report(Step(ModelProvisioningPhase.Checking));
        context.Deliver();

        Assert.Equal(["next-model"], received);
    }

    [Fact]
    public void Closing_and_then_draining_posted_callbacks_does_not_repeat_log_entries()
    {
        var context = new QueuedContext();
        var received = new List<ModelProvisioningPhase>();
        using var progress = new ModelRunProgress(step => received.Add(step.Phase), context);
        progress.Report(Step(ModelProvisioningPhase.Planned));
        progress.Report(Step(ModelProvisioningPhase.Accepted));

        progress.Dispose();
        context.Deliver();
        progress.Dispose();

        Assert.Equal([ModelProvisioningPhase.Planned, ModelProvisioningPhase.Accepted], received);
    }

    private static ModelProvisioningProgress Step(ModelProvisioningPhase phase) =>
        new("previous-model", 2048, phase, 0, 1);

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            callbacks.Enqueue(() => callback(state));

        public void Deliver()
        {
            while (callbacks.TryDequeue(out var callback))
            {
                callback();
            }
        }
    }
}
