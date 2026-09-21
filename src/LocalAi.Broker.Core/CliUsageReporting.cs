using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker;

public static class CliUsageReporting
{
    public static async Task<int> RunAsync(string component, string[] arguments, string runtimeRoot,
        TextWriter output, TextWriter error, Func<TextWriter, TextWriter, Task<int>> execute, Guid? callId = null)
    {
        var operation = CliHelp.IsRequest(component, arguments) ? "help" : UsageReportingRegistry.All.Where(entry => entry.Surface == UsageSurface.Cli && entry.Component == component)
            .OrderByDescending(entry => entry.Operation.Length)
            .FirstOrDefault(entry => arguments.Length >= entry.Operation.Split(' ').Length
                && entry.Operation.Split(' ').SequenceEqual(arguments.Take(entry.Operation.Split(' ').Length), StringComparer.OrdinalIgnoreCase))
            ?.Operation ?? "unknown_command";
        using var call = new UsageCall(UsageSurface.Cli, component, operation, callId);
        var contextSetting = Environment.GetEnvironmentVariable("LOCALAI_SOURCE_CONTEXT");
        if (contextSetting is not null)
            call.Comparison.SourceContext(Enum.TryParse<CloudContextPresence>(contextSetting, true, out var presence)
                && Enum.IsDefined(presence) ? presence : CloudContextPresence.Unknown);
        using var stdout = new CountingWriter(output);
        using var stderr = new CountingWriter(error);
        var store = new UsageReportStore(runtimeRoot);
        var legacyModelProtocol = component == "localai" && arguments.FirstOrDefault() == "model";
        int exit;
        try
        {
            exit = await execute(stdout, stderr);
            call.ExitCode(exit);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException) call.Cancelled();
            else if (exception is ArgumentException) call.Refused("invalid_input");
            else call.Failed("command_failed");
            if (stdout.Failed || stderr.Failed) call.DeliveryFailed("output_not_written");
            await UsageEvidence.CollectAsync(call);
            var failed = CloudTokenMethodology.Apply(call.Complete(), null, null, null);
            try { await store.WriteAsync(failed, CancellationToken.None); }
            catch (Exception persistence) when (persistence is IOException or UnauthorizedAccessException)
            { exception.Data["localai/usagePersistence"] = persistence.GetType().Name; }
            throw;
        }
        await UsageEvidence.CollectAsync(call);
        var scenario = call.Comparison.Scenario(JsonSerializer.Serialize(arguments));
        var initial = call.Complete();
        var beforeNotice = stdout.Captured is { } capturedOut && stderr.Captured is { } capturedError
            ? capturedOut + capturedError : null;
        var report = beforeNotice is null
            ? RuntimeCloudComparison.MarkConditional(CloudTokenMethodology.Apply(initial, scenario, null, call.Comparison.Profile))
            : UsagePayload.Finalize(initial,
                value => beforeNotice + (legacyModelProtocol ? string.Empty : UsageNotice.Render(value) + stderr.NewLine),
                value => value, scenario, call.Comparison.Profile).Report;
        try
        {
            // The installer's version-one model protocol treats extra stderr as corruption.
            // Its canonical report remains available in the usage store by call ID.
            if (!legacyModelProtocol)
                stderr.WriteLine(UsageNotice.Render(report));
            stdout.Flush(); stderr.Flush();
            var volumes = report.Volumes.ToDictionary();
            volumes[UsageVolume.ResponseBytes] = UsageValue.Measured(checked(stdout.Bytes + stderr.Bytes));
            volumes[UsageVolume.ResponseCharacters] = UsageValue.Measured(checked(stdout.Characters + stderr.Characters));
            report = report with { Volumes = volumes };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report = report with { Outcome = report.Outcome with { Delivery = UsageDelivery.Failed, DeliveryReason = "output_not_written" } };
            report = RuntimeCloudComparison.MarkConditional(CloudTokenMethodology.Apply(report, scenario, null, call.Comparison.Profile));
            try { await store.WriteAsync(report, CancellationToken.None); }
            catch (Exception persistence) when (persistence is IOException or UnauthorizedAccessException)
            { exception.Data["localai/usagePersistence"] = persistence.GetType().Name; }
            throw;
        }
        try { await store.WriteAsync(report, CancellationToken.None); }
        catch (Exception persistence) when (persistence is IOException or UnauthorizedAccessException)
        {
            if (!legacyModelProtocol)
                stderr.WriteLine(UsageNotice.SaveFailed(call.CallId, persistence.GetType().Name));
        }
        return exit;
    }

    // Forward without persisting source/output contents or replacing process-global console state.
    private sealed class CountingWriter : TextWriter
    {
        private readonly TextWriter inner;
        private readonly Encoder encoder = Encoding.UTF8.GetEncoder();
        private readonly object gate = new();
        // Bounded, transient capture for tokenization. Never written to the usage store.
        private StringBuilder? captured = new();
        private const int MaximumCaptureCharacters = 8 * 1024 * 1024;
        private long bytes;
        public long Characters { get; private set; }
        public bool Failed { get; private set; }
        public string? Captured { get { lock (gate) return captured?.ToString(); } }
        public override Encoding Encoding => inner.Encoding;
        public CountingWriter(TextWriter inner) : base(inner.FormatProvider) { this.inner = inner; NewLine = inner.NewLine; }
        public long Bytes { get { lock (gate) { Count([], flush: true); return bytes; } } }
        public override void Write(char value) { lock (gate) { try { inner.Write(value); Count([value]); } catch { Failed = true; throw; } } }
        public override void Write(string? value) { lock (gate) { try { inner.Write(value); Count(value.AsSpan()); } catch { Failed = true; throw; } } }
        public override void Write(ReadOnlySpan<char> value) { lock (gate) { try { inner.Write(value); Count(value); } catch { Failed = true; throw; } } }
        public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));
        public override void Flush() { lock (gate) { try { inner.Flush(); } catch { Failed = true; throw; } } }
        private void Count(ReadOnlySpan<char> value, bool flush = false)
        {
            if (captured is not null)
            {
                if (value.Length > MaximumCaptureCharacters - captured.Length) captured = null;
                else captured.Append(value);
            }
            Characters = checked(Characters + value.Length);
            Span<byte> buffer = stackalloc byte[4096];
            bool complete;
            do
            {
                encoder.Convert(value, buffer, flush, out var consumed, out var written, out complete);
                bytes = checked(bytes + written);
                value = value[consumed..];
            } while (!complete);
        }
    }
}
