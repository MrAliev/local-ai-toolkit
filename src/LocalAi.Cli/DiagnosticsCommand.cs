using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

internal static class DiagnosticsCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable,
        CancellationToken cancellationToken, string? runtimeRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int Refuse(string code, string message, int exit)
        {
            if (exit == 2) UsageCall.Current?.Refused(code); else UsageCall.Current?.Failed(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal("diagnostics", code, message) : code + ": " + message);
            return exit;
        }
        if (args.Length != 0 && args is not ["--out", var _])
            return Refuse("invalid_arguments", LocalAiCommandSyntax.Diagnostics, 2);
        string? destination = args.Length == 2 ? args[1] : null;
        if (destination is not null && string.IsNullOrWhiteSpace(destination))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.Diagnostics, 2);
        DiagnosticSnapshot snapshot;
        try { snapshot = await DiagnosticBundle.CaptureAsync(runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Refuse("diagnostic_read_failed", CliText.Catalogue.Get("DiagnosticsInputFailed"), 74); }
        if (destination is not null)
        {
            try { await DiagnosticBundle.WriteAsync(snapshot, destination, cancellationToken); }
            catch (DiagnosticOutputExistsException) { return Refuse("output_exists", CliText.Catalogue.Get("DiagnosticsOutputExists"), 73); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return Refuse("diagnostic_write_failed", CliText.Catalogue.Get("DiagnosticsOutputFailed"), 73); }
        }
        var data = new { saved = destination is not null, manifest = snapshot.Manifest, contents = snapshot.Contents };
        if (!machineReadable)
        {
            streams.Out.WriteLine(CliText.Catalogue.Get("DiagnosticsHelp"));
            streams.Out.WriteLine(CliText.Catalogue.Get("DiagnosticsNotice"));
            streams.Out.WriteLine(CliText.Catalogue.Get("DiagnosticsPrivacyNotice"));
            if (destination is not null) streams.Out.WriteLine(CliText.Catalogue.Format("DiagnosticsSaved", Path.GetFullPath(destination)));
        }
        streams.Out.WriteLine(machineReadable ? MachineOutput.Answer("diagnostics", data) : JsonSerializer.Serialize(data, DiagnosticBundle.Json));
        return 0;
    }
}
