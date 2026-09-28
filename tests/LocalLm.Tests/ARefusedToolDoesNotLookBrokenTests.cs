using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;

namespace LocalLm.Tests;

/// <summary>
/// A tool that refuses is not a tool that is broken.
///
/// The four content tools have said so since #209: their failures come back as readable text
/// rather than as a protocol error, "which keeps a missing file or a stopped Ollama from looking
/// like a broken tool". The five model-management tools were one-line
/// `Serialize(await tasks.Something(...))` with no failure handling at all, so anything thrown
/// reached the MCP host as `An error occurred invoking '...'`.
///
/// Found by calling `local_model_feedback` on a pair with three completed tasks. The broker
/// refused correctly — feedback is not offered before ten, so nobody judges a model on a first
/// impression — and wrote the reason down in full in the job's failure record. The caller got
/// nine words that named no cause. The sentence existed and never arrived.
///
/// These tests assert what the caller must be able to see, not how it is worded: the call
/// returns rather than throwing, and the reason travels with it.
/// </summary>
public sealed class ARefusedToolDoesNotLookBrokenTests
{
    private const string Refusal =
        "Feedback for 'TechnicalTranslation/translategemma:12b' is available only after 10 " +
        "completed experiment tasks and the report gate has paused the pair.";

    [Fact]
    public async Task A_refusal_reaches_the_caller_as_its_own_sentence()
    {
        var answer = await LocalLmTools.LocalModelFeedback(
            new ModelManagementTasks(new RefusingClient(Refusal)),
            "TechnicalTranslation",
            "translategemma:12b",
            "ContinueExperiment",
            TestContext.Current.CancellationToken);

        Assert.Contains(Refusal, answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a fault rather than a refusal: a broker that is not there is a sentence too,
    /// and the tool that could not reach it is not itself the thing that broke.
    /// </summary>
    [Fact]
    public async Task A_broker_that_is_not_there_is_reported_rather_than_thrown()
    {
        var answer = await LocalLmTools.LocalModelsStatus(
            new ModelManagementTasks(new RefusingClient("The Local AI broker is not running.")),
            TestContext.Current.CancellationToken);

        Assert.Contains("broker is not running", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation is the one thing that must still travel as itself: the host asked, and
    /// rendering that as an answer invites a retry nobody is waiting for.
    /// </summary>
    [Fact]
    public async Task Cancellation_the_host_asked_for_is_still_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalLmTools.LocalModelsStatus(
                new ModelManagementTasks(new CancellingClient()),
                cancelled.Token));
    }

    private sealed class RefusingClient(string reason) : NotAClient
    {
        public override Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new BrokerJobFailedException(Guid.NewGuid(), "InvalidOperationException", reason);

        public override Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new BrokerJobFailedException(Guid.NewGuid(), "InvalidOperationException", reason);
    }

    private sealed class CancellingClient : NotAClient
    {
        public override Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("should not be reached");
        }
    }

    /// <summary>
    /// Everything a test here does not call. Overriding one member is the whole point of each
    /// fake above; the rest exist so the interface is satisfied and would be a defect to reach.
    /// </summary>
    private abstract class NotAClient : ILocalModelClient
    {
        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
