using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class ModelManagementTasksTests
{
    [Fact]
    public async Task Sync_pulls_only_recommended_missing_models_sequentially()
    {
        var client = new FakeClient
        {
            Status = new LocalModelsStatusOutput(
                ["qwen3.5:9b"],
                [],
                ["translategemma:12b"],
                [],
                "1")
        };
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.SyncRecommendedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["translategemma:12b"], client.Pulled);
        Assert.Equal(["translategemma:12b"], result.InstalledModels);
        Assert.Equal("1", result.CatalogVersion);
        Assert.Equal(1, client.MaxConcurrentPulls);
    }

    /// <summary>
    /// A pull the broker refused is about that model, so the run goes on to the rest.
    ///
    /// Model pulls are minutes each, so a run that stops on the first refusal leaves the machine
    /// without models it could have had. A `BrokerJobFailedException` means the job ran and the
    /// broker wrote down why - a mis-tagged catalogue entry, a manifest that is not there - and
    /// none of that says anything about the next model.
    /// </summary>
    [Fact]
    public async Task A_model_the_broker_refused_does_not_stop_the_rest()
    {
        var client = Missing("first", "second", "third");
        client.PullFailures["second"] = () =>
            new BrokerJobFailedException(Guid.NewGuid(), "InvalidDataException", "no such tag");
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.SyncRecommendedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalModelSyncOutcome.Partial, result.Outcome);
        Assert.Equal(["first", "third"], result.InstalledModels);
        Assert.Equal("second", Assert.Single(result.FailedModels).Model);
        Assert.Contains("no such tag", Assert.Single(result.FailedModels).Reason, StringComparison.Ordinal);
        Assert.Empty(result.NotAttemptedModels);

        // Every model was tried: this is the assertion that says the run went on.
        Assert.Equal(["first", "second", "third"], client.Pulled);
    }

    /// <summary>
    /// A backend that is not there is about the machine, so the run stops and says what it never
    /// tried.
    ///
    /// Not a preference: `BrokerClient` waits thirty minutes for a job before it gives up, so
    /// carrying on through an unreachable Ollama would spend half an hour per remaining model
    /// inside one call.
    /// </summary>
    [Fact]
    public async Task An_unreachable_backend_stops_the_run_and_the_rest_are_named()
    {
        var client = Missing("first", "second", "third");
        client.PullFailures["second"] = () =>
            new BrokerBackendUnreachableException("http://127.0.0.1:11434");
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.SyncRecommendedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalModelSyncOutcome.Partial, result.Outcome);
        Assert.Equal(["first"], result.InstalledModels);
        Assert.Equal("second", Assert.Single(result.FailedModels).Model);
        Assert.Equal(["third"], result.NotAttemptedModels);

        // Two attempts, not three: the third was never asked for.
        Assert.Equal(["first", "second"], client.Pulled);
    }

    [Fact]
    public async Task A_run_that_installed_nothing_says_so_rather_than_partly()
    {
        var client = Missing("first", "second");
        foreach (var model in new[] { "first", "second" })
        {
            client.PullFailures[model] = () =>
                new BrokerJobFailedException(Guid.NewGuid(), "InvalidDataException", "no such tag");
        }

        var result = await new ModelManagementTasks(client)
            .SyncRecommendedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalModelSyncOutcome.Failed, result.Outcome);
        Assert.Empty(result.InstalledModels);
        Assert.Equal(2, result.FailedModels.Count);
    }

    [Fact]
    public async Task A_run_with_nothing_to_do_is_complete()
    {
        var result = await new ModelManagementTasks(Missing())
            .SyncRecommendedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalModelSyncOutcome.Complete, result.Outcome);
        Assert.Empty(result.InstalledModels);
        Assert.Empty(result.FailedModels);
        Assert.Empty(result.NotAttemptedModels);
    }

    /// <summary>
    /// The invariant the whole shape exists for: whatever happened, every recommended model is
    /// accounted for exactly once. A caller reading the result can act on it without asking again.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("refused")]
    [InlineData("unreachable")]
    public async Task Every_recommended_model_is_accounted_for_exactly_once(string failure)
    {
        var client = Missing("first", "second", "third");
        if (failure == "refused")
        {
            client.PullFailures["second"] = () =>
                new BrokerJobFailedException(Guid.NewGuid(), "InvalidDataException", "no such tag");
        }
        else if (failure == "unreachable")
        {
            client.PullFailures["second"] = () =>
                new BrokerBackendUnreachableException("http://127.0.0.1:11434");
        }

        var result = await new ModelManagementTasks(client)
            .SyncRecommendedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["first", "second", "third"],
            result.InstalledModels
                .Concat(result.FailedModels.Select(failed => failed.Model))
                .Concat(result.NotAttemptedModels)
                .Order(StringComparer.Ordinal)
                .ToArray()
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The host's own cancellation is not a partial result. `BrokerJobCancelledException` derives
    /// from `OperationCanceledException`, so a filter that did not test the token would let a job
    /// the queue cancelled escape as though the caller had asked to stop.
    /// </summary>
    [Fact]
    public async Task Cancellation_the_caller_asked_for_is_not_a_result()
    {
        using var cancelled = new CancellationTokenSource();
        var client = Missing("first", "second");
        client.PullFailures["first"] = () =>
        {
            cancelled.Cancel();
            return new OperationCanceledException(cancelled.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ModelManagementTasks(client).SyncRecommendedAsync(cancelled.Token));
    }

    private static FakeClient Missing(params string[] models) =>
        new()
        {
            Status = new LocalModelsStatusOutput([], [], models, [], "1"),
        };

    [Fact]
    public async Task Feedback_and_report_are_forwarded_as_typed_operations()
    {
        var client = new FakeClient();
        var tasks = new ModelManagementTasks(client);

        await tasks.ApplyFeedbackAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            ExperimentOwnerAction.ContinueExperiment,
            TestContext.Current.CancellationToken);
        await tasks.GetExperimentReportAsync(
            LocalTaskProfile.PlainTranslation,
            "translategemma:12b",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (
                LocalTaskProfile.PlainTranslation,
                "translategemma:12b",
                ExperimentOwnerAction.ContinueExperiment),
            client.Feedback);
        Assert.Equal(
            (LocalTaskProfile.PlainTranslation, "translategemma:12b"),
            client.Report);
    }

    [Fact]
    public async Task Preflight_is_forwarded_without_task_content()
    {
        var client = new FakeClient();
        var tasks = new ModelManagementTasks(client);

        var result = await tasks.PreflightAsync(
            "translategemma:12b",
            2048,
            "signed-7",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ("translategemma:12b", 2048, "signed-7"),
            client.Preflight);
        Assert.True(result.FullyResident);
        Assert.Equal(result.SizeBytes, result.SizeVramBytes);
    }

    private sealed class FakeClient : ILocalModelClient
    {
        private int _activePulls;

        public LocalModelsStatusOutput Status { get; init; } =
            new([], [], [], [], "1");

        public List<string> Pulled { get; } = [];

        public int MaxConcurrentPulls { get; private set; }

        public (
            LocalTaskProfile Profile,
            string Model,
            ExperimentOwnerAction Action)? Feedback { get; private set; }

        public (LocalTaskProfile Profile, string Model)? Report { get; private set; }

        public (string Model, int ContextTokens, string CatalogVersion)? Preflight { get; private set; }

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(Status));

        /// <summary>
        /// What a pull does instead of succeeding, per model. A sync that keeps going after one
        /// model and stops after another cannot be tested by a fake that only ever succeeds.
        /// </summary>
        public Dictionary<string, Func<Exception>> PullFailures { get; } =
            new(StringComparer.Ordinal);

        public async Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activePulls);
            MaxConcurrentPulls = Math.Max(MaxConcurrentPulls, active);
            await Task.Yield();
            Pulled.Add(model);
            Interlocked.Decrement(ref _activePulls);
            if (PullFailures.TryGetValue(model, out var failure))
            {
                throw failure();
            }

            return Result(new ModelMaintenanceJobOutput("success"));
        }

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            Preflight = (model, contextTokens, catalogVersion);
            return Task.FromResult(Result(
                new LocalModelPreflightOutput(
                    model,
                    contextTokens,
                    catalogVersion,
                    100,
                    100,
                    true,
                    DateTimeOffset.UtcNow)));
        }

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default)
        {
            Report = (profile, model);
            return Task.FromResult(Result(
                new LocalExperimentReportOutput(
                    profile,
                    model,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    0,
                    0)));
        }

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default)
        {
            Feedback = (profile, model, action);
            return Task.FromResult(Result(
                new LocalModelFeedbackOutput(profile, model, action)));
        }

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

        private static LocalJobResult<T> Result<T>(T value) =>
            new(
                value,
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "control",
                    "n/a",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    0,
                    null,
                    null,
                    null));
    }
}
