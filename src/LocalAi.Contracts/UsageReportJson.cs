using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>The versioned usage boundary; does not change any existing JSON serializer.</summary>
public static class UsageReportJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Write(UsageReport report)
    {
        Validate(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static UsageReport Read(string json)
    {
        var report = JsonSerializer.Deserialize<UsageReport>(json, Options);
        Validate(report);
        return report!;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(LocalAiJson.Strict)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    private static void Validate(UsageReport? report)
    {
        Require(report is not null, "report_required");
        Require(report!.SchemaVersion == UsageReport.CurrentSchemaVersion, "unsupported_usage_schema");
        Named(report.MethodologyVersion, "methodology_required");
        var identity = report.Identity;
        Require(identity is not null, "identity_required");
        Defined(identity!.Surface);
        Named(identity.Component, "component_required");
        Named(identity.Operation, "operation_required");
        Require(identity.CallId != Guid.Empty && identity.WorkflowId != Guid.Empty, "invalid_call_identity");
        Ids(identity.JobIds);
        Ids(identity.AttemptIds);

        var outcome = report.Outcome;
        Require(outcome is not null, "outcome_required");
        Defined(outcome!.Technical); Defined(outcome.Validation); Defined(outcome.Delivery);
        if (outcome.Technical != UsageTechnicalOutcome.Succeeded) Named(outcome.TechnicalReason, "technical_reason_required");
        if (outcome.Validation != UsageValidation.Passed) Named(outcome.ValidationReason, "validation_reason_required");
        if (outcome.Delivery != UsageDelivery.Delivered) Named(outcome.DeliveryReason, "delivery_reason_required");

        var models = report.Models;
        Require(models is not null && models.Actual is not null, "models_required");
        Defined(models!.State); Defined(models.Cache);
        Value(models.Calls, false);
        if (models.Cache is UsageCache.Unknown or UsageCache.NotApplicable) Named(models.CacheReason, "cache_reason_required");
        long calls = 0;
        foreach (var actual in models.Actual!)
        {
            Require(actual is not null, "model_required");
            Named(actual!.Model, "actual_model_required");
            Require(actual.Calls > 0, "actual_model_calls_required");
            Defined(actual.Offload);
            if (actual.Offload == UsageOffload.Unknown) Named(actual.OffloadReason, "offload_reason_required");
            try { calls = checked(calls + actual.Calls); }
            catch (OverflowException) { throw new JsonException("model_calls_overflow"); }
        }
        switch (models.State)
        {
            case UsageModelState.Unknown:
                Require(models.Calls.Status == UsageValueStatus.Unknown && models.Actual.Count == 0, "unknown_model_calls"); break;
            case UsageModelState.NotCalled:
                Require(models.Calls == UsageValue.Measured(0) && models.Actual.Count == 0, "unexpected_model_calls"); break;
            case UsageModelState.Called:
                Require(models.Calls.Status == UsageValueStatus.Measured && calls > 0 && models.Calls.Value == calls, "inconsistent_model_calls"); break;
        }

        Map(report.Timings, _ => false);
        Map(report.Volumes, _ => false);
        if (models.State == UsageModelState.NotCalled)
        {
            Require(report.Volumes[UsageVolume.BackendPromptTokens].Value is null or 0
                && report.Volumes[UsageVolume.BackendEvalTokens].Value is null or 0, "backend_tokens_without_model_call");
        }
        Map(report.CloudTokens, key => key is UsageCloudTokens.InputSaved or UsageCloudTokens.OutputSaved);
        Defined(report.ComparisonKind);
        Require(report.Comparison is not null, "comparison_required");
        if (report.ComparisonKind == UsageComparisonKind.NotApplicable)
            Require(report.CloudTokens.Values.All(value => value.Status == UsageValueStatus.NotApplicable), "inapplicable_comparison");
        if (report.CloudTokens.Values.Any(Known))
        {
            Named(report.Comparison!.ScenarioId, "scenario_required");
            Named(report.Comparison.TokenizerProfile, "cloud_tokenizer_profile_required");
        }
        Difference(report.CloudTokens, UsageCloudTokens.InputWithoutDelegation, UsageCloudTokens.InputWithDelegation, UsageCloudTokens.InputSaved);
        Difference(report.CloudTokens, UsageCloudTokens.OutputWithoutDelegation, UsageCloudTokens.OutputWithDelegation, UsageCloudTokens.OutputSaved);
    }

    private static void Difference(IReadOnlyDictionary<UsageCloudTokens, UsageValue> values, UsageCloudTokens beforeKey, UsageCloudTokens afterKey, UsageCloudTokens savedKey)
    {
        var before = values[beforeKey]; var after = values[afterKey]; var saved = values[savedKey];
        if (!Known(saved)) return;
        Require(Known(before) && Known(after), "comparison_sides_required");
        Require(saved.Value == before.Value - after.Value, "inconsistent_token_difference");
        if (before.Status == UsageValueStatus.Estimated || after.Status == UsageValueStatus.Estimated)
            Require(saved.Status == UsageValueStatus.Estimated, "estimated_difference_required");
    }

    private static bool Known(UsageValue value) => value.Status is UsageValueStatus.Measured or UsageValueStatus.Estimated;

    private static void Map<T>(IReadOnlyDictionary<T, UsageValue>? values, Func<T, bool> signed) where T : struct, Enum
    {
        var required = Enum.GetValues<T>();
        Require(values is not null && values.Count == required.Length && required.All(values.ContainsKey), "incomplete_" + typeof(T).Name);
        foreach (var (key, value) in values!) Value(value, signed(key));
    }

    private static void Value(UsageValue? value, bool signed)
    {
        Require(value is not null, "value_required");
        Defined(value!.Status);
        if (!Known(value))
        {
            Require(value.Value is null && value.Method is null && value.Range is null && value.RangeReason is null, "unknown_value_has_measurement");
            Named(value.Reason, "value_reason_required");
            return;
        }
        Require(value.Value.HasValue && (signed || value.Value >= 0) && value.Reason is null, "invalid_known_value");
        if (value.Status == UsageValueStatus.Measured)
        {
            Require(value.Range is null && value.RangeReason is null && value.Method is null, "measured_value_has_estimate");
            return;
        }
        Named(value.Method, "estimation_method_required");
        if (value.Range is { } range)
        {
            Require(range.Lower <= value.Value && value.Value <= range.Upper && (signed || range.Lower >= 0) && value.RangeReason is null, "invalid_estimate_range");
            Named(range.Basis, "range_basis_required");
        }
        else Named(value.RangeReason, "range_reason_required");
    }

    private static void Ids(IReadOnlyList<Guid>? values) =>
        Require(values is not null && values.All(value => value != Guid.Empty) && values.Distinct().Count() == values.Count, "invalid_linked_ids");
    private static void Defined<T>(T value) where T : struct, Enum => Require(Enum.IsDefined(value), "unknown_" + typeof(T).Name);
    private static void Named(string? value, string reason) => Require(!string.IsNullOrWhiteSpace(value), reason);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new JsonException(reason);
    }
}
