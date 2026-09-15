using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class UsageCallTests
{
    [Fact]
    public void Cancellation_of_a_joined_waiter_keeps_the_receipt_already_observed_for_that_job()
    {
        using var call = NewCall();
        var id = Guid.NewGuid();
        var receipt = new LocalUsageReceipt(id, "locallm", "chat", "actual", TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null);
        call.RegisterJobEvidence("runtime", id, receipt);
        call.RegisterJobEvidence("runtime", id);
        Assert.Same(receipt, Assert.Single(call.TakeJobEvidence()).Receipt);
    }

    [Fact]
    public void Invalid_input_before_inference_has_one_identity_and_no_invented_model()
    {
        using var call = NewCall();
        call.Refused("invalid_input");
        var report = call.Complete();
        Assert.Equal(call.CallId, report.Identity.CallId);
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(UsageDelivery.NotAttempted, report.Outcome.Delivery);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.Equal(UsageValue.Measured(0), report.Models.Calls);
        Assert.Equal(UsageReportJson.Write(report), UsageReportJson.Write(call.Complete()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undelivered_translation_retains_partial_metrics_without_claiming_savings(bool cancelled)
    {
        using var call = NewCall();
        var job = Guid.NewGuid(); var attempt = Guid.NewGuid(); var workflow = Guid.NewGuid();
        call.LinkJob(job, workflow);
        call.ModelCalled("qwen3.5:9b", UsageOffload.Partial, inputTokens: 12, outputTokens: 7, attemptId: attempt);
        call.Validated(true);
        if (cancelled) call.Cancelled(); else call.DeliveryFailed("output_not_written");
        var report = call.Complete();
        Assert.Equal(cancelled ? UsageTechnicalOutcome.Cancelled : UsageTechnicalOutcome.Succeeded, report.Outcome.Technical);
        Assert.Equal(cancelled ? UsageDelivery.NotAttempted : UsageDelivery.Failed, report.Outcome.Delivery);
        Assert.Equal(UsageValidation.Passed, report.Outcome.Validation);
        Assert.Equal([job], report.Identity.JobIds);
        Assert.Equal([attempt], report.Identity.AttemptIds);
        Assert.Equal(workflow, report.Identity.WorkflowId);
        Assert.Equal(UsageOffload.Partial, Assert.Single(report.Models.Actual).Offload);
        Assert.Equal(UsageValue.Measured(12), report.Volumes[UsageVolume.BackendPromptTokens]);
        Assert.Equal(UsageValue.Measured(7), report.Volumes[UsageVolume.BackendEvalTokens]);
        Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.OutputSaved].Status);
        _ = UsageReportJson.Write(report);
    }

    [Fact]
    public void A_cache_hit_delivers_a_result_without_reusing_the_cached_models_identity()
    {
        using var call = NewCall();
        call.CacheHit();
        var report = call.Complete();
        Assert.Equal(UsageCache.Hit, report.Models.Cache);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.Empty(report.Models.Actual);
        Assert.Equal(UsageDelivery.Delivered, report.Outcome.Delivery);
    }

    [Fact]
    public void Joining_or_cancelling_an_unobserved_job_does_not_claim_zero_inference()
    {
        using var call = NewCall();
        call.LinkJob(Guid.NewGuid());
        call.Cancelled();
        Assert.Equal(UsageModelState.Unknown, call.Complete().Models.State);
    }

    [Fact]
    public void Refusal_after_inference_preserves_the_model_and_failed_validation()
    {
        using var call = NewCall();
        call.ModelCalled("qwen3.5:9b", UsageOffload.Cpu);
        call.Validated(false);
        call.Refused("result_rejected");
        var report = call.Complete();
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(UsageValidation.Failed, report.Outcome.Validation);
        Assert.Equal(UsageModelState.Called, report.Models.State);
        Assert.Equal(UsageValueStatus.Unknown, report.Volumes[UsageVolume.BackendEvalTokens].Status);
    }

    [Fact]
    public async Task Concurrent_calls_do_not_share_outcomes_or_metrics()
    {
        using var outer = NewCall();
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(async number =>
        {
            using var call = NewCall();
            await Task.Yield();
            Assert.Same(call, UsageCall.Current);
            if (number % 2 == 0) call.Refused("invalid_input");
            return call.Complete();
        }));
        Assert.Same(outer, UsageCall.Current);
        Assert.Equal(8, values.Select(value => value.Identity.CallId).Distinct().Count());
        Assert.Equal(4, values.Count(value => value.Outcome.Technical == UsageTechnicalOutcome.Refused));
        Assert.Equal(4, values.Count(value => value.Outcome.Technical == UsageTechnicalOutcome.Succeeded));
    }

    private static UsageCall NewCall() => new(UsageSurface.Mcp, "locallm", "translate_local");
}
