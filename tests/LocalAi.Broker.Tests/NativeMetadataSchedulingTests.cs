using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class NativeMetadataSchedulingTests
{
    private readonly ManualTimeProvider _time =
        new(new DateTimeOffset(2026, 9, 21, 16, 0, 0, TimeSpan.Zero));
    private readonly ScheduleMetadataResolver _resolver =
        new(ModelRoutingCatalog.LoadEmbedded(), new DurationEstimator());

    [Theory]
    [InlineData(NativeOllamaOperation.Tags, null)]
    [InlineData(NativeOllamaOperation.Tags, "resident")]
    [InlineData(NativeOllamaOperation.Processes, null)]
    [InlineData(NativeOllamaOperation.Processes, "resident")]
    [InlineData(NativeOllamaOperation.Show, null)]
    [InlineData(NativeOllamaOperation.Show, "resident")]
    public void Reading_metadata_does_not_wait_for_a_model_switch(
        NativeOllamaOperation operation, string? resident)
    {
        var candidate = Native(operation);

        var decision = new ModelAwareScheduler(_time).Decide([candidate], resident);

        Assert.Equal(candidate.JobId, decision.JobId);
        Assert.Null(decision.WaitUntilUtc);
    }

    [Theory]
    [InlineData(NativeOllamaOperation.Chat)]
    [InlineData(NativeOllamaOperation.Embed)]
    [InlineData(NativeOllamaOperation.Generate)]
    public void Actual_native_inference_still_waits_for_the_other_model(
        NativeOllamaOperation operation)
    {
        var decision = new ModelAwareScheduler(_time).Decide([Native(operation)], "resident");

        Assert.Null(decision.JobId);
        Assert.Equal(_time.GetUtcNow().AddSeconds(2), decision.WaitUntilUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("resident")]
    public void Listing_installed_models_does_not_wait_for_a_model_switch(string? resident)
    {
        var request = LocalJobRequestFactory.CreateListModels(
            "list-models", LocalJobPriority.Interactive, createdAtUtc: _time.GetUtcNow());
        var candidate = _resolver.Resolve(new QueuedJobCandidate(request, 1, request.CreatedAtUtc));

        var decision = new ModelAwareScheduler(_time).Decide([candidate], resident);

        Assert.Equal(request.JobId, decision.JobId);
        Assert.Null(decision.WaitUntilUtc);
    }

    [Fact]
    public void Metadata_does_not_bypass_dependency_readiness()
    {
        var candidate = Native(NativeOllamaOperation.Tags) with { IsDependencyReady = false };

        var decision = new ModelAwareScheduler(_time).Decide([candidate], "resident");

        Assert.Null(decision.JobId);
    }

    [Fact]
    public void Metadata_cannot_overtake_higher_priority_inference()
    {
        var metadata = Native(NativeOllamaOperation.Tags, LocalJobPriority.Background);
        var inference = Native(NativeOllamaOperation.Embed, LocalJobPriority.Interactive);

        var decision = new ModelAwareScheduler(_time).Decide([metadata, inference], "other");

        Assert.Equal(inference.JobId, decision.JobId);
    }

    [Fact]
    public void A_starved_metadata_request_is_not_hidden_by_resident_model_affinity()
    {
        var metadata = Native(NativeOllamaOperation.Tags) with
        {
            CreatedAtUtc = _time.GetUtcNow().AddMinutes(-15)
        };
        var inference = Native(NativeOllamaOperation.Embed);

        var decision = new ModelAwareScheduler(_time).Decide([metadata, inference], "other");

        Assert.Equal(metadata.JobId, decision.JobId);
        Assert.Null(decision.WaitUntilUtc);
    }

    private ScheduledJobCandidate Native(
        NativeOllamaOperation operation,
        LocalJobPriority priority = LocalJobPriority.Interactive)
    {
        var request = LocalJobRequestFactory.CreateNativeOllama(
            Guid.NewGuid().ToString(), priority, operation,
            operation is NativeOllamaOperation.Tags or NativeOllamaOperation.Processes
                ? null
                : JsonSerializer.SerializeToElement(new { model = "other" }),
            createdAtUtc: _time.GetUtcNow());
        return _resolver.Resolve(new QueuedJobCandidate(request, 1, request.CreatedAtUtc));
    }
}
