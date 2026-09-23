using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineDependencyAction(string Component, string Kind, string Installer, string[] Arguments);
public sealed record OfflineActionDocument(int SchemaVersion, OfflineDependencyAction[] Actions);
public sealed record OfflineDependencyPlan(string Component, string? DeclaredVersion, string Kind, string Installer,
    string[] Arguments, string ManifestSha256, string InstallerSha256, string[] Files);
public sealed record OfflineDependencyResult(OfflineDependencyPlan Plan, string State, int? ExitCode, bool RebootRequired, string? RetainedDirectory);
public sealed record OfflineModelsPlan(string[] Models, string[] Files, long Bytes, string Destination, string ResidencyProof);

public sealed partial class OfflineBundleApplication(ReleaseManifestVerifier verifier, IProcessRunner runner, IUserPathStore? userPath = null)
{
    private readonly IUserPathStore pathStore = userPath ?? new WindowsUserPathStore();
    private const string ActionsFile = "offline-actions.json";

    public async Task<OfflineDependencyPlan> PlanDependencyAsync(string bundle, string pin, string component, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        return await DependencyPlanAsync(bundle, snapshot, component, token);
    }

    public async Task<OfflineDependencyResult> ApplyDependencyAsync(string bundle, string pin, string component, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        var plan = await DependencyPlanAsync(bundle, snapshot, component, token);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var stage = new WindowsStagingRootFactory().CreateExclusive(Path.Combine(Path.GetTempPath(), "localai-offline-" + Guid.NewGuid().ToString("N")));
        var retained = new List<IRetainedStagingFile>();
        var preserve = false;
        try
        {
            var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            foreach (var path in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                var leaf = Path.GetFileName(path);
                await CopyVerifiedAsync(bundle, path, Path.Combine(stage.CanonicalPath, leaf), files[path], token);
                var file = stage.RetainFile(leaf); retained.Add(file);
                if (file.Metadata.Length != files[path].Bytes || !file.Metadata.Sha256.Equals(files[path].Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Invalid();
            }
            stage.ValidateExactLayout(plan.Files.Select(Path.GetFileName)!);
            foreach (var file in retained) file.Revalidate();
            var installer = Path.Combine(stage.CanonicalPath, Path.GetFileName(plan.Installer));
            var executable = plan.Kind == "msi" ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : installer;
            string[] arguments = plan.Kind == "msi" ? ["/i", installer, "/qn", "/norestart", .. plan.Arguments] : plan.Arguments;
            token.ThrowIfCancellationRequested();
            ProcessResult result;
            try { result = await runner.RunAsync(executable, arguments, TimeSpan.FromMinutes(20), token); }
            catch (ProcessTerminationException)
            {
                // A child that could not be stopped may still need its adjacent installer files.
                preserve = true;
                return new(plan, "termination-unconfirmed", null, false, stage.CanonicalPath);
            }
            var state = result.Cancelled ? "cancelled" : result.TimedOut ? "timed-out" : result.ExitCode is 0 or 3010 or 1641 ? "completed" : "failed";
            return new(plan, state, result.ExitCode, result.ExitCode is 3010 or 1641, null);
        }
        finally
        {
            foreach (var file in retained) file.Dispose();
            if (!preserve) stage.Cleanup();
        }
    }

    public async Task<OfflineModelsPlan> ImportModelsAsync(string bundle, string pin, string destination,
        ModelRoutingCatalogDocument catalog, bool apply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("Model destination already exists.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent)) throw Invalid();
        OfflineEnvironmentBundle.EnsureNoLinks(parent);
        var sourcePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle)) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        var materials = await ReadModelArtifactsAsync(bundle, snapshot, catalog, token);
        var plan = new OfflineModelsPlan(materials.Models, materials.Files.Select(path => path[7..]).ToArray(), materials.Bytes,
            destination, "preflight-required");
        if (!apply) return plan;
        var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var staging = Path.Combine(parent, ".localai-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var path in materials.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = OfflineEnvironmentBundle.Resolve(staging, path[7..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyVerifiedAsync(bundle, path, target, files[path], token);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return plan;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static async Task<(string[] Models, string[] Files, long Bytes)> ReadModelArtifactsAsync(string bundle,
        OfflineBundleResult snapshot, ModelRoutingCatalogDocument catalog, CancellationToken token)
    {
        ValidateComponents(snapshot.Manifest);
        var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var selected = snapshot.Manifest.Components.Where(component => component.Kind == "model" && component.Availability == "included").ToArray();
        if (selected.Length == 0) throw Invalid();
        var imported = new HashSet<string>(StringComparer.Ordinal);
        var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in selected)
        {
            if (!catalog.Models.Any(model => model.Tag == component.Id && model.Lifecycle != LocalModelLifecycle.Disabled && model.InstallPolicy != LocalModelInstallPolicy.Never))
                throw Invalid();
            var manifestPath = ModelManifestPath(component.Id);
            if (!manifests.Add(manifestPath) || !component.Files.Contains(manifestPath, StringComparer.Ordinal)) throw Invalid();
            var graph = new HashSet<string>(StringComparer.Ordinal) { manifestPath };
            try
            {
                var bytes = await ReadVerifiedAsync(bundle, files[manifestPath], token);
                var json = JsonSerializer.Deserialize<JsonElement>(bytes, LocalAiJson.Strict);
                if (json.GetProperty("schemaVersion").GetInt32() != 2) throw Invalid();
                AddBlob(json.GetProperty("config"));
                var layers = json.GetProperty("layers");
                if (layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() is < 1 or > 1024) throw Invalid();
                foreach (var layer in layers.EnumerateArray()) AddBlob(layer);
                void AddBlob(JsonElement descriptor)
                {
                    var digest = descriptor.GetProperty("digest").GetString();
                    if (digest is null || !BlobDigest().IsMatch(digest) || !descriptor.GetProperty("size").TryGetInt64(out var size) || size < 0) throw Invalid();
                    var path = "models/blobs/sha256-" + digest[7..];
                    if (!files.TryGetValue(path, out var file) || file.Bytes != size || !file.Sha256.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw Invalid();
                    graph.Add(path);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw Invalid(); }
            if (!graph.SetEquals(component.Files)) throw Invalid();
            imported.UnionWith(graph);
        }
        var paths = imported.Order(StringComparer.Ordinal).ToArray();
        return (selected.Select(component => component.Id).ToArray(), paths, paths.Sum(path => files[path].Bytes));
    }

    private static async Task<OfflineDependencyPlan> DependencyPlanAsync(string bundle, OfflineBundleResult snapshot, string id, CancellationToken token)
    {
        ValidateComponents(snapshot.Manifest);
        if (!DependencyCatalog.Supported.Any(dependency => dependency.ActionId == id)) throw Invalid();
        var component = snapshot.Manifest.Components.SingleOrDefault(item => item.Kind == "dependency" && item.Id == id && item.Availability == "included") ?? throw Invalid();
        var actionFile = snapshot.Manifest.Files.SingleOrDefault(file => file.Path == ActionsFile) ?? throw Invalid();
        OfflineActionDocument document;
        try { document = JsonSerializer.Deserialize<OfflineActionDocument>(await ReadVerifiedAsync(bundle, actionFile, token), OfflineEnvironmentBundle.Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (document.SchemaVersion != 1 || document.Actions is null || document.Actions.Length is < 1 or > 64 ||
            document.Actions.Any(action => action is null) || document.Actions.Select(action => action.Component).Distinct(StringComparer.Ordinal).Count() != document.Actions.Length)
            throw Invalid();
        var action = document.Actions.SingleOrDefault(action => action.Component == id) ?? throw Invalid();
        if (action.Kind is not ("exe" or "msi") || action.Installer is null || !component.Files.Contains(action.Installer, StringComparer.Ordinal) ||
            !Path.GetExtension(action.Installer).Equals("." + action.Kind, StringComparison.OrdinalIgnoreCase) || action.Arguments is null ||
            action.Arguments.Length > 64 || action.Arguments.Any(argument => argument is null || argument.Length > 4096 || argument.Any(char.IsControl)) ||
            action.Arguments.Sum(argument => argument.Length + 3) > 16000) throw Invalid();
        if (action.Kind == "msi" && action.Arguments.Any(argument => !MsiProperty().IsMatch(argument))) throw Invalid();
        var parent = Path.GetDirectoryName(action.Installer);
        // Full offline EXE/MSI installers and same-directory companion files only. No shell,
        // arbitrary working-directory recipe, package-manager fallback or bootstrap download.
        if (component.Files.Any(path => Path.GetDirectoryName(path) != parent) ||
            component.Files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != component.Files.Length) throw Invalid();
        var installer = snapshot.Manifest.Files.Single(file => file.Path == action.Installer);
        return new(id, component.Version, action.Kind, action.Installer, action.Arguments, snapshot.ManifestSha256, installer.Sha256, component.Files);
    }

    private static void ValidateComponents(OfflineBundleManifest manifest)
    {
        var keys = new HashSet<(string, string)>();
        var files = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var item in manifest.Components)
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Kind is not ("runtime" or "dependency" or "model") ||
                item.Availability is not ("included" or "missing") || item.Files is null || !keys.Add((item.Kind, item.Id)) ||
                item.Files.Distinct(StringComparer.Ordinal).Count() != item.Files.Length || item.Files.Any(path => !files.Contains(path)) ||
                (item.Availability == "included") != (item.Files.Length > 0)) throw Invalid();
    }

    private static async Task<byte[]> ReadVerifiedAsync(string root, OfflineBundleFile metadata, CancellationToken token)
    {
        if (metadata.Bytes is < 1 or > 1024 * 1024) throw Invalid();
        var path = OfflineEnvironmentBundle.Resolve(root, metadata.Path); OfflineEnvironmentBundle.EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length != metadata.Bytes) throw Invalid();
        var bytes = new byte[(int)metadata.Bytes]; await input.ReadExactlyAsync(bytes, token);
        if (input.ReadByte() != -1 || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return bytes;
    }

    private static async Task CopyVerifiedAsync(string root, string path, string target, OfflineBundleFile metadata, CancellationToken token)
    {
        var actual = await OfflineEnvironmentBundle.CopyAndHashAsync(OfflineEnvironmentBundle.Resolve(root, path), target, path, metadata.Bytes, token);
        if (actual.Bytes != metadata.Bytes || !actual.Sha256.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    private static string ModelManifestPath(string name)
    {
        var parts = name.Split('/'); if (parts.Length is < 1 or > 3) throw Invalid();
        var last = parts[^1].Split(':'); if (last.Length is < 1 or > 2) throw Invalid();
        var host = parts.Length == 3 ? parts[0] : "registry.ollama.ai";
        var space = parts.Length >= 2 ? parts[^2] : "library";
        var tag = last.Length == 2 ? last[1] : "latest";
        if (!NamePart().IsMatch(host) || !NamePart().IsMatch(space) || !NamePart().IsMatch(last[0]) || !NamePart().IsMatch(tag)) throw Invalid();
        var path = $"models/manifests/{host}/{space}/{last[0]}/{tag}";
        OfflineEnvironmentBundle.ValidatePath(path); return path;
    }

    private static InvalidDataException Invalid() => new("Invalid offline application material.");
    [GeneratedRegex("^sha256:[a-f0-9]{64}$")] private static partial Regex BlobDigest();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,79}$")] private static partial Regex NamePart();
    [GeneratedRegex("^[A-Z][A-Z0-9_]*=[^\\r\\n]*$")] private static partial Regex MsiProperty();
}
