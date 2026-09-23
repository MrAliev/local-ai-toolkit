using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Cli;

internal sealed record HardwareDependency(string Id, string Executable, string Presence, string Verification);
internal sealed record HardwareObservation(GpuSnapshot Gpu, PhysicalMemorySnapshot Memory, DiskSnapshot Disk, HardwareDependency[] Dependencies);

internal static class HardwarePlanCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable,
        CancellationToken cancellationToken, string? runtimeRoot = null, ReleaseManifestVerifier? verifier = null,
        Func<string, CancellationToken, Task<HardwareObservation>>? observe = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int Refuse(string code, string message, int exit)
        {
            UsageCall.Current?.Refused(code);
            (machineReadable ? streams.Out : streams.Error).WriteLine(machineReadable
                ? MachineOutput.Refusal("hardware plan", code, message) : code + ": " + message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (args[i] is not ("--release" or "--disk") || i + 1 == args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", LocalAiCommandSyntax.HardwarePlan, 2);
        if (!options.TryGetValue("--release", out var releaseFolder))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.HardwarePlan, 2);

        ReleaseManifest release;
        try
        {
            if (!IsLocalPath(releaseFolder)) throw new InvalidDataException();
            var manifest = await ReadBoundedAsync(Path.Combine(releaseFolder, "release-manifest.json"), 1024 * 1024, cancellationToken);
            var signature = await ReadBoundedAsync(Path.Combine(releaseFolder, "release-manifest.sig"), 64, cancellationToken);
            using var ownedVerifier = verifier is null ? ReleaseTrustAnchor.CreateManifestVerifier() : null;
            release = (verifier ?? ownedVerifier!).Verify(manifest, signature);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidDataException or ReleaseVerificationException)
        { return Refuse("release_manifest_rejected", CliText.Catalogue.Get("HardwarePlanReleaseRejected"), 65); }

        var diskPath = options.GetValueOrDefault("--disk", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!IsLocalPath(diskPath) || !Directory.Exists(diskPath))
            return Refuse("invalid_disk_path", CliText.Catalogue.Get("HardwarePlanDiskPathInvalid"), 2);
        runtimeRoot ??= ModelResidencyPolicyStore.DefaultRuntimeRoot;
        ModelRoutingCatalogSnapshot catalog;
        try { catalog = new ModelRoutingCatalogStore(runtimeRoot).ReadWithSource(); }
        catch (InvalidDataException)
        { return Refuse("catalog_rejected", CliText.Catalogue.Get("HardwarePlanCatalogRejected"), 65); }
        var policy = new ModelResidencyPolicyStore(runtimeRoot).ReadWithSource();
        var hardware = await (observe ?? ObserveAsync)(diskPath, cancellationToken);
        var plan = HardwarePlanBuilder.Build(release, catalog.Document, hardware.Gpu, hardware.Memory, hardware.Disk,
            policy.Policy.ModelResidency, cancellationToken);
        var data = JsonSerializer.SerializeToElement(new
        {
            plan,
            catalogSource = catalog.SourcePath is null ? "embedded" : "configured",
            policySource = new { fileFound = policy.FileFound, fileUsed = policy.FileUsed },
            dependencies = hardware.Dependencies,
            notice = CliText.Catalogue.Get("HardwarePlanNotice"),
            diskNotice = CliText.Catalogue.Get("HardwarePlanDiskIncomplete"),
            dependencyNotice = CliText.Catalogue.Get("HardwarePlanDependenciesNotice"),
        }, Json);
        if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer("hardware plan", data));
        else
        {
            streams.Out.WriteLine(CliText.Catalogue.Get("HardwarePlanHelp"));
            streams.Out.WriteLine(CliText.Catalogue.Format("HardwarePlanSummary", plan.Models.Length));
            streams.Out.WriteLine(UntrustedContent.Wrap(data.GetRawText(), "hardware-plan"));
        }
        return 0;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }

    private static async Task<HardwareObservation> ObserveAsync(string diskPath, CancellationToken token) => new(
        await new WindowsGpuProbe(new DxgiNativeGpuAdapterEnumerator()).ProbeAsync(token), PhysicalMemoryProbe.Observe(),
        new SystemDiskProbe().Observe(diskPath), FindDependencies(Environment.GetEnvironmentVariable("PATH")));

    internal static HardwareDependency[] FindDependencies(string? searchPath)
    {
        // Presence only: executing a discovered binary could trigger downloads or services.
        var directories = (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => directory.Trim().Trim('"')).Where(directory => Path.IsPathFullyQualified(directory) && IsLocalPath(directory)).ToArray();
        return DependencyCatalog.Supported.Select(dependency =>
        {
            var names = Path.HasExtension(dependency.ExecutableName) ? [dependency.ExecutableName]
                : new[] { dependency.ExecutableName, dependency.ExecutableName + ".exe", dependency.ExecutableName + ".cmd", dependency.ExecutableName + ".bat" };
            var found = directories.Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
            return new HardwareDependency(dependency.ActionId, dependency.ExecutableName, found ? "present" : "not-found", "unverified");
        }).ToArray();
    }

    internal static bool IsLocalPath(string path)
    {
        try
        {
            // Reject UNC/device namespaces before any existence check can contact a share.
            if (path.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var full = Path.GetFullPath(path);
            return !OperatingSystem.IsWindows() || new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
