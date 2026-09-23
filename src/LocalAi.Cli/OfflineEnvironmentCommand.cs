using System.Text.Json;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Cli;

internal static class OfflineEnvironmentCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (args.FirstOrDefault() is "install-dependency" or "import-models")
            return await OfflineApplicationCommand.ExecuteAsync(args, streams, machineReadable, token);
        var building = args.FirstOrDefault() == "bundle";
        var command = "offline " + (building ? "bundle" : "verify");
        var syntax = building ? LocalAiCommandSyntax.OfflineBundle : LocalAiCommandSyntax.OfflineVerify;
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal(command, code, message) : code + ": " + message);
            return exit;
        }
        if (args.Length == 0 || args[0] is not ("bundle" or "verify"))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.OfflineBundle + "\n" + LocalAiCommandSyntax.OfflineVerify, 2);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (!(building ? args[i] is "--source" or "--out" : args[i] is "--bundle" or "--manifest-sha256") ||
                i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", syntax, 2);
        if (options.Count != 2) return Refuse("invalid_arguments", syntax, 2);
        var input = options[building ? "--source" : "--bundle"];
        if (!HardwarePlanCommand.IsLocalPath(input) || building && !HardwarePlanCommand.IsLocalPath(options["--out"]))
            return Refuse(building ? "offline_source_invalid" : "offline_verification_failed",
                CliText.Catalogue.Get(building ? "OfflineInvalidSource" : "OfflineVerificationFailed"), 65);
        if (building && Path.Exists(options["--out"]))
            return Refuse("output_exists", CliText.Catalogue.Get("OfflineOutputExists"), 73);
        OfflineBundleResult result;
        try
        {
            using var verifier = ReleaseTrustAnchor.CreateManifestVerifier();
            var bundle = new OfflineEnvironmentBundle(verifier);
            result = building
                ? await bundle.BuildAsync(input, options["--out"], new ModelRoutingCatalogStore().Read(), token)
                : await bundle.VerifyAsync(input, options["--manifest-sha256"], token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or OverflowException)
        { return Refuse(building ? "offline_source_invalid" : "offline_verification_failed",
            CliText.Catalogue.Get(building ? "OfflineInvalidSource" : "OfflineVerificationFailed"), 65); }
        var data = JsonSerializer.SerializeToElement(new { result.ManifestSha256, result.TotalBundleBytes, result.Manifest,
            notice = CliText.Catalogue.Get("OfflineNotice") }, OfflineEnvironmentBundle.Json);
        if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer(command, data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get(building ? "OfflineHelpBundle" : "OfflineHelpVerify"));
            if (building) streams.Out.WriteLine(CliText.Catalogue.Format("OfflineSaved", Path.GetFullPath(options["--out"]), result.ManifestSha256));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "offline-bundle"));
        }
        return 0;
    }
}
