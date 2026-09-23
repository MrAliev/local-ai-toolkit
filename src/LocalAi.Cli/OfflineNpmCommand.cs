using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

internal static class OfflineNpmCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken token,
        ReleaseManifestVerifier? verifier = null, IProcessRunner? runner = null)
    {
        token.ThrowIfCancellationRequested();
        const string command = "offline install-npm";
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal(command, code, message) : code + ": " + message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--bundle" or "--manifest-sha256" or "--component" or "--node" or "--npm-cli" or "--out" or "--mode") ||
                i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflineInstallNpm, 2);
        var mode = options.GetValueOrDefault("--mode", "plan");
        if (new[] { "--bundle", "--manifest-sha256", "--component", "--node", "--npm-cli", "--out" }.Any(key => !options.ContainsKey(key)) || mode is not ("plan" or "apply"))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflineInstallNpm, 2);
        var refusal = CliText.Catalogue.Get("OfflineNpmRefused");
        if (new[] { "--bundle", "--node", "--npm-cli", "--out" }.Any(key => !HardwarePlanCommand.IsLocalPath(options[key])))
            return Refuse("offline_npm_refused", refusal, 65);
        if (Path.Exists(options["--out"])) return Refuse("output_exists", CliText.Catalogue.Get("OfflineNpmOutputExists"), 73);
        object result;
        try
        {
            using var ownedVerifier = verifier is null ? ReleaseTrustAnchor.CreateManifestVerifier() : null;
            var application = new OfflineBundleApplication(verifier ?? ownedVerifier!, runner ?? new SystemProcessRunner());
            result = mode == "apply"
                ? await application.ApplyNpmAsync(options["--bundle"], options["--manifest-sha256"], options["--component"], options["--node"], options["--npm-cli"], options["--out"], token)
                : await application.PlanNpmAsync(options["--bundle"], options["--manifest-sha256"], options["--component"], options["--node"], options["--npm-cli"], options["--out"], token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or JsonException or InvalidOperationException or ReleaseVerificationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return Refuse("offline_npm_refused", refusal, 65); }
        var applied = result as OfflineNpmResult;
        var exit = applied?.State switch { null or "applied" => 0, "cancelled" => 130, "timed-out" => 75, _ => 70 };
        if (exit == 130) UsageCall.Current?.Cancelled(); else if (exit != 0) UsageCall.Current?.Failed("offline_npm_failed");
        var data = JsonSerializer.SerializeToElement(new { mode, result, notice = CliText.Catalogue.Get("OfflineNpmNotice") }, OfflineEnvironmentBundle.Json);
        if (machineReadable) streams.Out.WriteLine(exit == 0 ? MachineOutput.Answer(command, data)
            : MachineEnvelope.Refusal(command, "offline_npm_failed", CliText.Catalogue.Get("OfflineNpmFailed"), data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get("OfflineNpmHelp"));
            streams.Out.WriteLine(applied is null ? CliText.Catalogue.Get("OfflineNpmPlan") : applied.State == "termination-unconfirmed"
                ? CliText.Catalogue.Get("OfflineNpmUnconfirmed") : exit == 0
                ? CliText.Catalogue.Format("OfflineNpmApplied", applied.Plan.Component, applied.Plan.Destination) : CliText.Catalogue.Get("OfflineNpmFailed"));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "offline-npm"));
        }
        return exit;
    }
}
