using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LocalAi.Mcp;

public sealed class UsageReporting(string runtimeRoot)
{
    private static readonly JsonSerializerOptions VisibleJson = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string ModelVisibleRequest(CallToolRequestParams request) =>
        JsonSerializer.Serialize(new { name = request.Name, arguments = request.Arguments }, VisibleJson);

    public string RuntimeRoot { get; } = runtimeRoot;
    public async ValueTask<CallToolResult> InvokeAsync(string component, string operation, Guid callId,
        Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken cancellationToken,
        string? requestJson = null, CloudContextPresence sourceContext = CloudContextPresence.NotPresent)
    {
        using var call = new UsageCall(UsageSurface.Mcp, component, operation, callId);
        call.Comparison.SourceContext(sourceContext);
        var store = new UsageReportStore(RuntimeRoot);
        _ = new RuntimeAcl().TryCreateRoot(RuntimeRoot);
        CallToolResult result;
        try { result = await next(cancellationToken); }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                call.Cancelled();
                await UsageEvidence.CollectAsync(call);
                var cancelled = CloudTokenMethodology.Apply(call.Complete(), null, null, null);
                try { await store.WriteAsync(cancelled, CancellationToken.None); }
                catch (Exception persistence) when (persistence is IOException or UnauthorizedAccessException)
                { exception.Data["localai/usagePersistence"] = persistence.GetType().Name; }
                throw;
            }
            var invalidInput = exception is ArgumentException or JsonException;
            if (invalidInput) call.Refused("invalid_input");
            else call.Failed("tool_failed");
            result = new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                {
                    Text = UsageNotice.Catalogue.Get(invalidInput ? "UsageInvalidToolArguments" : "UsageToolCallFailed"),
                }],
            };
        }
        if (result.IsError == true) call.ExitCode(70);
        await UsageEvidence.CollectAsync(call);
        var policy = UsageReportingRegistry.Find(UsageSurface.Mcp, component, operation).Policy;
        // Keep content and provenance nonces fixed throughout the bounded size calculation.
        var content = result.Content.ToArray();
        var metadata = result.Meta?.DeepClone().AsObject() ?? new JsonObject();
        var scenario = call.Comparison.Scenario(requestJson);
        UsagePayload<CallToolResult> Render(string? persistenceError = null) => UsagePayload.Finalize(call.Complete(), report =>
        {
            var meta = metadata.DeepClone().AsObject();
            meta["localai/usage"] = JsonNode.Parse(UsageReportJson.Write(report));
            var notice = UsageNotice.Render(report);
            if (persistenceError is not null)
            {
                meta["localai/usagePersistence"] = persistenceError;
                notice += "\n" + UsageNotice.SaveFailed(call.CallId, persistenceError);
            }
            if (policy.Response == UsageResponseShape.Json) meta["localai/usageNotice"] = notice;
            return new CallToolResult
            {
                Content = policy.Response == UsageResponseShape.Json
                    ? [.. content, new TextContentBlock { Text = notice }]
                    : [new TextContentBlock { Text = notice }, .. content],
                StructuredContent = result.StructuredContent, IsError = result.IsError, Meta = meta,
            };
        }, payload => JsonSerializer.Serialize(payload, McpJsonUtilities.DefaultOptions),
            scenario, call.Comparison.Profile, ModelVisibleResponse);
        var final = Render();
        try { await store.WriteAsync(final.Report, CancellationToken.None); }
        catch (Exception persistence) when (persistence is IOException or UnauthorizedAccessException)
        { final = Render(persistence.GetType().Name); }
        return final.Value;
    }

    private static string? ModelVisibleResponse(CallToolResult result)
    {
        // _meta belongs to the client, not the model. Count decoded text (including our
        // notice) and structured data, never the escaped transport envelope. Client-specific
        // framing/deduplication is unknown: this is a named comparison, not account billing.
        // Do not pretend that images/audio/resources cost their base64/JSON length in tokens.
        if (result.Content.Any(block => block is not TextContentBlock)) return null;
        var parts = result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToList();
        if (result.StructuredContent is { } structured)
            parts.Add(JsonSerializer.Serialize(structured, VisibleJson));
        return string.Join("\n", parts);
    }
}

public static class UsageReportingExtensions
{
    public static IMcpServerBuilder WithUsageReporting(this IMcpServerBuilder builder, string component, string runtimeRoot) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
        {
            var supplied = context.Params?.Meta?["localai/callId"];
            var id = supplied is JsonValue value && value.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var parsed) && parsed != Guid.Empty ? parsed : Guid.NewGuid();
            var presenceNode = context.Params?.Meta?["localai/sourceContext"];
            var presenceText = presenceNode is JsonValue presenceValue && presenceValue.TryGetValue<string>(out var sourcePresence)
                ? sourcePresence : null;
            var presence = Enum.TryParse<CloudContextPresence>(presenceText, true, out var parsedPresence)
                && Enum.IsDefined(parsedPresence) ? parsedPresence : presenceNode is null
                    ? CloudContextPresence.NotPresent : CloudContextPresence.Unknown;
            return new UsageReporting(runtimeRoot).InvokeAsync(component, context.Params!.Name, id,
                token => next(context, token), cancellationToken,
                UsageReporting.ModelVisibleRequest(context.Params), presence);
        }));
}
