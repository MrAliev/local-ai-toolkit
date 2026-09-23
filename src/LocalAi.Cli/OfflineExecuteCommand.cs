using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

internal static class OfflineExecuteCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken token,
        ReleaseManifestVerifier? verifier = null, IProcessRunner? runner = null, ModelRoutingCatalogDocument? catalog = null,
        InstallationLayout? layout = null, IUserPathStore? userPath = null)
    {
        token.ThrowIfCancellationRequested();
        const string command = "offline execute";
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal(command, code, message) : code + ": " + message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--bundle" or "--manifest-sha256" or "--request" or "--out" or "--mode") ||
                i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflineExecute, 2);
        var mode = options.GetValueOrDefault("--mode", "plan");
        if (!options.ContainsKey("--bundle") || !options.ContainsKey("--manifest-sha256") || !options.ContainsKey("--request") ||
            !options.ContainsKey("--out") || mode is not ("plan" or "apply"))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflineExecute, 2);
        var refusal = CliText.Catalogue.Get("OfflineExecuteRefused");
        if (!HardwarePlanCommand.IsLocalPath(options["--bundle"]) || !HardwarePlanCommand.IsLocalPath(options["--request"]) ||
            !HardwarePlanCommand.IsLocalPath(options["--out"])) return Refuse("offline_execution_refused", refusal, 65);
        if (Path.Exists(options["--out"])) return Refuse("output_exists", CliText.Catalogue.Get("OfflineExecuteOutputExists"), 73);
        object result;
        try
        {
            await using var input = new FileStream(options["--request"], FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException();
            var request = await JsonSerializer.DeserializeAsync<OfflineExecutionRequest>(input, OfflineEnvironmentBundle.Json, token)
                ?? throw new InvalidDataException();
            if (request.ModelsOutput is not null && !HardwarePlanCommand.IsLocalPath(request.ModelsOutput)) throw new InvalidDataException();
            if (request.Npm?.Any(selection => selection is null ||
                new[] { selection.Node, selection.NpmCli, selection.Output }.Any(path => string.IsNullOrWhiteSpace(path) || !HardwarePlanCommand.IsLocalPath(path))) == true)
                throw new InvalidDataException();
            using var ownedVerifier = verifier is null ? ReleaseTrustAnchor.CreateManifestVerifier() : null;
            var application = new OfflineBundleApplication(verifier ?? ownedVerifier!, runner ?? new SystemProcessRunner(), userPath);
            catalog ??= new ModelRoutingCatalogStore().Read(); layout ??= InstallationLayout.CreateDefault();
            result = mode == "apply"
                ? await application.ExecuteAsync(options["--bundle"], options["--manifest-sha256"], request, options["--out"], catalog, layout, token)
                : await application.PlanExecutionAsync(options["--bundle"], options["--manifest-sha256"], request, options["--out"], catalog, layout, token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or JsonException or ReleaseVerificationException or LocalAiPackageInstallationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return Refuse("offline_execution_refused", refusal, 65); }
        var applied = result as OfflineExecutionResult;
        var exit = applied?.State switch
        { null or "applied" => 0, "cancelled" => 130, "timed-out" or "reboot-required" => 75, "recording-failed" => 74, _ => 70 };
        if (exit == 130) UsageCall.Current?.Cancelled();
        else if (exit != 0) UsageCall.Current?.Failed("offline_execution_failed");
        var data = JsonSerializer.SerializeToElement(new { mode, result, notice = CliText.Catalogue.Get("OfflineExecuteNotice"),
            dependencyNotice = CliText.Catalogue.Get("OfflineApplyNotice"), modelNotice = CliText.Catalogue.Get(
                (applied?.Plan ?? result as OfflineExecutionPlan)?.ModelSetup is null ? "OfflineModelsNotice" : "OfflineExecuteModelsNotice"),
            npmNotice = CliText.Catalogue.Get("OfflineNpmNotice"), pathNotice = CliText.Catalogue.Get("OfflineExecutePathNotice"),
            uninstallNotice = CliText.Catalogue.Get("OfflineExecuteUninstallNotice"),
            preflightNotice = CliText.Catalogue.Get("OfflineExecutePreflightNotice"),
            diskNotice = CliText.Catalogue.Get("OfflinePlanDiskCaveat") }, OfflineEnvironmentBundle.Json);
        if (machineReadable)
            streams.Out.WriteLine(exit == 0 ? MachineOutput.Answer(command, data)
                : MachineEnvelope.Refusal(command, "offline_execution_failed", CliText.Catalogue.Get("OfflineExecuteFailed"), data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get("OfflineExecuteHelp"));
            streams.Out.WriteLine(applied is null ? CliText.Catalogue.Get("OfflineExecutePlan") : exit == 0
                ? CliText.Catalogue.Get("OfflineExecuteApplied") : CliText.Catalogue.Format("OfflineExecuteStopped", applied.State));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "offline-execution"));
        }
        return exit;
    }
}
