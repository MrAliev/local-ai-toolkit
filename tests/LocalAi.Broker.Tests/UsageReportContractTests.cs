using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class UsageReportContractTests
{
    private static UsageReport NewReport(string tool = "ask_local") =>
        UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", tool, Guid.NewGuid());

    [Fact]
    public void An_unobserved_call_contains_unknowns_instead_of_invented_zeroes_or_models()
    {
        var report = UsageReportJson.Read(UsageReportJson.Write(NewReport()));
        Assert.All(report.Timings.Values.Concat(report.Volumes.Values).Concat(report.CloudTokens.Values), value =>
        {
            Assert.Equal(UsageValueStatus.Unknown, value.Status);
            Assert.Null(value.Value);
            Assert.False(string.IsNullOrWhiteSpace(value.Reason));
        });
        Assert.Equal(UsageModelState.Unknown, report.Models.State);
        Assert.Empty(report.Models.Actual);
        Assert.Equal(UsageDelivery.Unknown, report.Outcome.Delivery);
        Assert.NotEqual(Guid.Empty, report.Identity.CallId);
    }

    [Theory]
    [InlineData("index_unload")]
    [InlineData("lsp_close_document")]
    public void Administration_has_an_explicit_inapplicable_comparison(string tool)
    {
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "codesearch", tool, Guid.NewGuid());
        Assert.All(report.CloudTokens.Values, value => Assert.Equal(UsageValueStatus.NotApplicable, value.Status));
        Assert.All(report.Volumes.Values, value => Assert.Equal(UsageValueStatus.Unknown, value.Status));
    }

    [Theory]
    [InlineData(UsageTechnicalOutcome.Succeeded)]
    [InlineData(UsageTechnicalOutcome.Failed)]
    [InlineData(UsageTechnicalOutcome.Refused)]
    [InlineData(UsageTechnicalOutcome.Cancelled)]
    [InlineData(UsageTechnicalOutcome.Unknown)]
    public void Every_outcome_can_preserve_unknown_measurements(UsageTechnicalOutcome outcome)
    {
        var report = NewReport() with
        {
            Outcome = new(outcome, UsageValidation.Unknown, UsageDelivery.Unknown, "not_recorded", "not_recorded", "not_recorded"),
        };
        Assert.Equal(outcome, UsageReportJson.Read(UsageReportJson.Write(report)).Outcome.Technical);
    }

    [Fact]
    public void Negative_cloud_savings_and_backend_tokens_stay_in_separate_fields()
    {
        var report = NewReport();
        var tokens = report.CloudTokens.ToDictionary();
        tokens[UsageCloudTokens.InputWithoutDelegation] = UsageValue.Measured(100);
        tokens[UsageCloudTokens.InputWithDelegation] = UsageValue.Measured(125);
        tokens[UsageCloudTokens.InputSaved] = UsageValue.Measured(-25);
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.BackendPromptTokens] = UsageValue.Measured(700);
        report = report with { CloudTokens = tokens, Volumes = volumes, Comparison = new("same_answer", "cloud-tokenizer-v1", null) };
        var restored = UsageReportJson.Read(UsageReportJson.Write(report));
        Assert.Equal(-25, restored.CloudTokens[UsageCloudTokens.InputSaved].Value);
        Assert.Equal(700, restored.Volumes[UsageVolume.BackendPromptTokens].Value);
        Assert.Equal(UsageValueStatus.Unknown, restored.CloudTokens[UsageCloudTokens.OutputSaved].Status);
    }

    [Fact]
    public void Failed_delivery_does_not_erase_successful_execution_or_workflow_links()
    {
        var report = NewReport();
        var job = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        report = report with
        {
            Identity = report.Identity with { WorkflowId = Guid.NewGuid(), JobIds = [job], AttemptIds = [attempt] },
            Outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.Passed, UsageDelivery.Failed, null, null, "write_failed"),
        };
        var restored = UsageReportJson.Read(UsageReportJson.Write(report));
        Assert.Equal(report.Identity.WorkflowId, restored.Identity.WorkflowId);
        Assert.Equal([job], restored.Identity.JobIds);
        Assert.Equal([attempt], restored.Identity.AttemptIds);
        Assert.Equal(UsageTechnicalOutcome.Succeeded, restored.Outcome.Technical);
        Assert.Equal(UsageDelivery.Failed, restored.Outcome.Delivery);
    }

    [Fact]
    public void Parallel_execution_durations_do_not_replace_elapsed_wall_time()
    {
        var report = NewReport();
        var times = report.Timings.ToDictionary();
        times[UsageTiming.ElapsedMilliseconds] = UsageValue.Measured(100);
        times[UsageTiming.ExecutionMilliseconds] = UsageValue.Measured(180);
        var restored = UsageReportJson.Read(UsageReportJson.Write(report with { Timings = times }));
        Assert.Equal(100, restored.Timings[UsageTiming.ElapsedMilliseconds].Value);
        Assert.Equal(UsageValueStatus.Unknown, restored.Timings[UsageTiming.QueueMilliseconds].Status);
    }

    [Fact]
    public void An_estimate_carries_its_method_and_range_applicability()
    {
        var report = NewReport();
        var tokens = report.CloudTokens.ToDictionary();
        tokens[UsageCloudTokens.InputWithoutDelegation] = UsageValue.Estimated(20, "characters-v1", new(10, 30, "calibrated-corpus-v1"));
        var restored = UsageReportJson.Read(UsageReportJson.Write(report with
        {
            CloudTokens = tokens, Comparison = new("read_sources", "heuristic-v1", null),
        }));
        var value = restored.CloudTokens[UsageCloudTokens.InputWithoutDelegation];
        Assert.Equal(UsageValueStatus.Estimated, value.Status);
        Assert.Equal(10, value.Range!.Lower);
        Assert.Equal("characters-v1", value.Method);
    }

    [Fact]
    public void No_model_call_is_explicit_and_does_not_invent_a_model_name()
    {
        var report = NewReport() with { Models = UsageModels.NoCall() };
        var restored = UsageReportJson.Read(UsageReportJson.Write(report));
        Assert.Equal(UsageModelState.NotCalled, restored.Models.State);
        Assert.Equal(UsageValue.Measured(0), restored.Models.Calls);
        Assert.Empty(restored.Models.Actual);
        Assert.Throws<JsonException>(() => UsageReportJson.Write(report with
        {
            Models = report.Models with { Actual = [new("invented", 1, UsageOffload.None, null)] },
        }));
    }

    [Fact]
    public void Every_registered_operation_can_create_a_valid_report_without_observations()
    {
        foreach (var entry in UsageReportingRegistry.All)
        {
            var report = UsageReportingRegistry.CreateReport(entry.Surface, entry.Component, entry.Operation, Guid.NewGuid());
            Assert.Equal(entry.Operation, UsageReportJson.Read(UsageReportJson.Write(report)).Identity.Operation);
        }
    }

    [Fact]
    public void Actual_models_cache_and_offload_are_preserved_without_guessing()
    {
        var report = NewReport() with
        {
            Models = new(UsageModelState.Called, UsageValue.Measured(3),
                [new("small-model", 2, UsageOffload.None, null), new("fallback-model", 1, UsageOffload.Unknown, "backend_not_reported")],
                UsageCache.Miss, null),
        };
        var restored = UsageReportJson.Read(UsageReportJson.Write(report));
        Assert.Equal(report.Models.Actual, restored.Models.Actual);
        Assert.Equal(UsageCache.Miss, restored.Models.Cache);
        Assert.Equal(3, restored.Models.Calls.Value);
    }

    [Fact]
    public void A_call_without_inference_cannot_claim_observed_backend_tokens()
    {
        var report = NewReport();
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.BackendPromptTokens] = UsageValue.Measured(42);
        Assert.Throws<JsonException>(() => UsageReportJson.Write(report with { Models = UsageModels.NoCall(), Volumes = volumes }));
    }

    [Theory]
    [InlineData("unknown_zero")]
    [InlineData("unknown_reason")]
    [InlineData("measured_null")]
    [InlineData("negative_volume")]
    [InlineData("estimated_method")]
    [InlineData("estimated_range")]
    [InlineData("missing_volume")]
    [InlineData("schema")]
    [InlineData("outcome")]
    [InlineData("numeric_enum")]
    [InlineData("call_id")]
    [InlineData("failure_reason")]
    [InlineData("model_count")]
    [InlineData("unjustified_savings")]
    public void Invalid_reports_are_rejected_at_the_json_boundary(string defect)
    {
        var node = JsonNode.Parse(UsageReportJson.Write(NewReport()))!.AsObject();
        var volume = node["volumes"]!["input_bytes"]!;
        switch (defect)
        {
            case "unknown_zero": volume["value"] = 0; break;
            case "unknown_reason": volume["reason"] = null; break;
            case "measured_null": volume["status"] = "measured"; break;
            case "negative_volume": volume["status"] = "measured"; volume["value"] = -1; volume["reason"] = null; break;
            case "estimated_method": volume["status"] = "estimated"; volume["value"] = 1; volume["reason"] = null; break;
            case "estimated_range": volume["status"] = "estimated"; volume["value"] = 1; volume["reason"] = null; volume["method"] = "test-v1"; break;
            case "missing_volume": node["volumes"]!.AsObject().Remove("input_bytes"); break;
            case "schema": node["schemaVersion"] = 999; break;
            case "outcome": node["outcome"]!["technical"] = "excellent"; break;
            case "numeric_enum": volume["status"] = 0; break;
            case "call_id": node["identity"]!["callId"] = Guid.Empty.ToString(); break;
            case "failure_reason": node["outcome"]!["technical"] = "failed"; node["outcome"]!["technicalReason"] = null; break;
            case "model_count": node["models"]!["state"] = "called"; break;
            case "unjustified_savings": var saved = node["cloudTokens"]!["input_saved"]!; saved["status"] = "measured"; saved["value"] = 0; saved["reason"] = null; break;
        }
        Assert.Throws<JsonException>(() => UsageReportJson.Read(node.ToJsonString()));
    }

    [Fact]
    public void The_new_contract_does_not_change_existing_envelopes_or_tool_policy_json()
    {
        Assert.Equal("{\"schema\":1,\"command\":\"ask\",\"ok\":true,\"data\":{\"answer\":\"ok\"}}",
            MachineEnvelope.Answer("ask", new { answer = "ok" }));
        Assert.Equal("{\"Name\":\"ask_local\",\"Approval\":0}", JsonSerializer.Serialize(McpToolNames.LocalLmPolicies[0]));
    }
}
