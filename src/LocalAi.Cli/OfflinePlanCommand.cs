using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

internal static class OfflinePlanCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken token,
        ReleaseManifestVerifier? verifier = null, ModelRoutingCatalogDocument? catalog = null, Func<string, DiskSnapshot>? observe = null)
    {
        token.ThrowIfCancellationRequested();
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal("offline plan", code, message) : code + ": " + message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--bundle" or "--manifest-sha256" or "--disk") || i + 1 == args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflinePlan, 2);
        if (options.Count != 3) return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflinePlan, 2);
        if (!HardwarePlanCommand.IsLocalPath(options["--disk"]) || !Directory.Exists(options["--disk"]))
            return Refuse("invalid_disk_path", CliText.Catalogue.Get("OfflinePlanDiskPathInvalid"), 2);
        OfflineEnvironmentPlan plan;
        try
        {
            if (!HardwarePlanCommand.IsLocalPath(options["--bundle"])) throw new InvalidDataException();
            using var ownedVerifier = verifier is null ? ReleaseTrustAnchor.CreateManifestVerifier() : null;
            var application = new OfflineBundleApplication(verifier ?? ownedVerifier!, new PlanningProcessRunner());
            plan = await application.PlanEnvironmentAsync(options["--bundle"], options["--manifest-sha256"],
                catalog ?? new ModelRoutingCatalogStore().Read(), (observe ?? OfflineBundleApplication.ObservePlanDisk)(options["--disk"]), token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or ReleaseVerificationException)
        { return Refuse("offline_plan_refused", CliText.Catalogue.Get("OfflinePlanRefused"), 65); }
        var data = JsonSerializer.SerializeToElement(new { plan, notice = CliText.Catalogue.Get("OfflinePlanNotice"),
            diskNotice = CliText.Catalogue.Get("OfflinePlanDiskCaveat") }, OfflineEnvironmentBundle.Json);
        if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer("offline plan", data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get("OfflinePlanHelp"));
            streams.Out.WriteLine(CliText.Catalogue.Format("OfflinePlanSummary", plan.Unresolved.Length));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "offline-plan"));
        }
        return 0;
    }

    private sealed class PlanningProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Offline planning cannot execute a process.");
    }
}
