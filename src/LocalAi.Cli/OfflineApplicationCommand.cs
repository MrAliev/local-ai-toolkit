using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

internal static class OfflineApplicationCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken token,
        ReleaseManifestVerifier? verifier = null, IProcessRunner? runner = null, ModelRoutingCatalogDocument? catalog = null)
    {
        token.ThrowIfCancellationRequested();
        var models = args.FirstOrDefault() == "import-models";
        var command = "offline " + (models ? "import-models" : "install-dependency");
        var syntax = models ? LocalAiCommandSyntax.OfflineImportModels : LocalAiCommandSyntax.OfflineInstallDependency;
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal(command, code, message) : code + ": " + message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length == 0 || args[0] is not ("import-models" or "install-dependency"))
            return Refuse("invalid_arguments", syntax, 2);
        for (var i = 1; i < args.Length; i += 2)
            if (!(args[i] is "--bundle" or "--manifest-sha256" or "--mode" || args[i] == (models ? "--out" : "--component")) ||
                i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", syntax, 2);
        var mode = options.GetValueOrDefault("--mode", "plan");
        if (!options.ContainsKey("--bundle") || !options.ContainsKey("--manifest-sha256") ||
            !options.ContainsKey(models ? "--out" : "--component") || mode is not ("plan" or "apply"))
            return Refuse("invalid_arguments", syntax, 2);
        var refusalCode = models ? "offline_models_refused" : "offline_action_refused";
        var refusalText = CliText.Catalogue.Get(models ? "OfflineModelsRefused" : "OfflineApplyRefused");
        if (!HardwarePlanCommand.IsLocalPath(options["--bundle"]) || models && !HardwarePlanCommand.IsLocalPath(options["--out"]))
            return Refuse(refusalCode, refusalText, 65);
        if (models && Path.Exists(options["--out"]))
            return Refuse("output_exists", CliText.Catalogue.Get("OfflineOutputExists"), 73);
        object result;
        try
        {
            using var ownedVerifier = verifier is null ? ReleaseTrustAnchor.CreateManifestVerifier() : null;
            var application = new OfflineBundleApplication(verifier ?? ownedVerifier!, runner ?? new SystemProcessRunner());
            if (models)
                result = await application.ImportModelsAsync(options["--bundle"], options["--manifest-sha256"], options["--out"],
                    catalog ?? new ModelRoutingCatalogStore().Read(), mode == "apply", token);
            else if (mode == "apply")
                result = await application.ApplyDependencyAsync(options["--bundle"], options["--manifest-sha256"], options["--component"], token);
            else
                result = await application.PlanDependencyAsync(options["--bundle"], options["--manifest-sha256"], options["--component"], token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or System.ComponentModel.Win32Exception)
        { return Refuse(refusalCode, refusalText, 65); }
        var applied = result as OfflineDependencyResult;
        var exit = applied?.State switch { "cancelled" => 130, "timed-out" => 75, "failed" or "termination-unconfirmed" => 70, _ => 0 };
        if (exit == 130) UsageCall.Current?.Cancelled();
        else if (exit != 0) UsageCall.Current?.Failed("offline_install_failed");
        var notice = CliText.Catalogue.Get(models ? "OfflineModelsNotice" : "OfflineApplyNotice");
        var data = JsonSerializer.SerializeToElement(new { mode, result, notice }, OfflineEnvironmentBundle.Json);
        if (machineReadable)
            streams.Out.WriteLine(exit == 0 ? MachineOutput.Answer(command, data)
                : MachineEnvelope.Refusal(command, "offline_install_failed", CliText.Catalogue.Get("OfflineApplyFailed"), data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get(models ? "OfflineModelsHelp" : "OfflineApplyHelp"));
            if (models) streams.Out.WriteLine(mode == "plan" ? CliText.Catalogue.Get("OfflineModelsPlan")
                : CliText.Catalogue.Format("OfflineModelsImported", ((OfflineModelsPlan)result).Destination));
            else if (applied is null) streams.Out.WriteLine(CliText.Catalogue.Format("OfflineApplyPlan", options["--component"]));
            else if (applied.State == "termination-unconfirmed") streams.Out.WriteLine(CliText.Catalogue.Get("OfflineApplyUnconfirmed"));
            else if (applied.ExitCode is not null) streams.Out.WriteLine(CliText.Catalogue.Format("OfflineApplyResult", applied.Plan.Component, applied.ExitCode));
            else streams.Out.WriteLine(CliText.Catalogue.Get("OfflineApplyFailed"));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "offline-application"));
        }
        return exit;
    }
}
