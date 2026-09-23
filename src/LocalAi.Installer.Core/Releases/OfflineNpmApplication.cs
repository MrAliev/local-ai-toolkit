using System.Text.Json;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineNpmPlan(string Component, string Package, string Version, string NodeExecutable, string NpmCli,
    string Destination, string[] Files, long Bytes, string ManifestSha256);
public sealed record OfflineNpmResult(OfflineNpmPlan Plan, string State, int? ExitCode, string? RetainedDirectory, bool ReadyForUse);
public sealed record OfflineNpmMaterials(string Component, string Package, string Version, string[] Files, long Bytes, string ManifestSha256);

public sealed partial class OfflineBundleApplication
{
    public async Task<OfflineNpmPlan> PlanNpmAsync(string bundle, string pin, string component, string node, string npmCli,
        string destination, CancellationToken token)
    {
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        return await NpmPlanAsync(bundle, snapshot, component, node, npmCli, destination, token);
    }

    private static async Task<OfflineNpmPlan> NpmPlanAsync(string bundle, OfflineBundleResult snapshot, string component,
        string node, string npmCli, string destination, CancellationToken token)
    {
        var materials = await NpmMaterialsAsync(bundle, snapshot, component, token);
        node = Path.GetFullPath(node); npmCli = Path.GetFullPath(npmCli); destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("The output already exists.");
        if (!File.Exists(node) || !File.Exists(npmCli) || Path.GetExtension(node) != ".exe" || Path.GetFileName(npmCli) != "npm-cli.js" ||
            Path.GetDirectoryName(destination) is not { } parent || !Directory.Exists(parent) || PathsOverlap(bundle, destination)) throw Invalid();
        OfflineEnvironmentBundle.EnsureNoLinks(node); OfflineEnvironmentBundle.EnsureNoLinks(npmCli); OfflineEnvironmentBundle.EnsureNoLinks(parent);
        return new(component, materials.Package, materials.Version, node, npmCli, destination, materials.Files, materials.Bytes, materials.ManifestSha256);
    }

    private static async Task<OfflineNpmMaterials> NpmMaterialsAsync(string bundle, OfflineBundleResult snapshot, string component, CancellationToken token)
    {
        ValidateComponents(snapshot.Manifest);
        var definition = DependencyCatalog.Supported.SingleOrDefault(item => item.ActionId == component && item.InstallerKind == DependencyInstallerKind.Npm)
            ?? throw Invalid();
        if (definition.PackageVersion is null) throw Invalid();
        var entry = snapshot.Manifest.Components.SingleOrDefault(item => item.Kind == "dependency" && item.Id == component && item.Availability == "included") ?? throw Invalid();
        if (entry.Version != definition.PackageVersion) throw Invalid();
        var prefix = "npm/" + component + "/";
        if (!entry.Files.Contains(prefix + "package.json", StringComparer.Ordinal) || !entry.Files.Contains(prefix + "package-lock.json", StringComparer.Ordinal) ||
            !entry.Files.Any(path => path.StartsWith(prefix + "cache/_cacache/", StringComparison.Ordinal)) ||
            entry.Files.Any(path => path != prefix + "package.json" && path != prefix + "package-lock.json" && !path.StartsWith(prefix + "cache/_cacache/", StringComparison.Ordinal))) throw Invalid();
        var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        try
        {
            using var project = JsonDocument.Parse(await ReadVerifiedAsync(bundle, files[prefix + "package.json"], token));
            using var locked = JsonDocument.Parse(await ReadVerifiedAsync(bundle, files[prefix + "package-lock.json"], token));
            if (project.RootElement.EnumerateObject().Any(property => property.Name is not ("name" or "version" or "private" or "dependencies")) ||
                !NpmDependenciesMatch(project.RootElement.GetProperty("dependencies"), definition)) throw Invalid();
            var root = locked.RootElement;
            if (root.GetProperty("lockfileVersion").GetInt32() != 3) throw Invalid();
            var packages = root.GetProperty("packages");
            if (packages.ValueKind != JsonValueKind.Object || packages.EnumerateObject().Count() is < 2 or > 4096 ||
                !NpmDependenciesMatch(packages.GetProperty("").GetProperty("dependencies"), definition)) throw Invalid();
            var selected = packages.GetProperty("node_modules/" + definition.PackageId);
            if (selected.GetProperty("version").GetString() != definition.PackageVersion) throw Invalid();
            foreach (var package in packages.EnumerateObject().Where(item => item.Name.Length != 0))
            {
                if (!package.Name.StartsWith("node_modules/", StringComparison.Ordinal) || package.Name.Contains('\\') ||
                    package.Name.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(character => char.IsControl(character) || character == ':'))) throw Invalid();
                var item = package.Value;
                if (item.TryGetProperty("link", out var link) && link.ValueKind != JsonValueKind.False) throw Invalid();
                var resolved = item.GetProperty("resolved").GetString();
                if (!Uri.TryCreate(resolved, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "registry.npmjs.org" ||
                    uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Fragment.Length != 0 || uri.Query.Length != 0) throw Invalid();
                var integrity = item.GetProperty("integrity").GetString();
                if (integrity is null || !integrity.StartsWith("sha512-", StringComparison.Ordinal) || Convert.FromBase64String(integrity[7..]).Length != 64) throw Invalid();
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Invalid(); }
        return new(component, definition.PackageId, definition.PackageVersion, entry.Files,
            entry.Files.Sum(path => files[path].Bytes), snapshot.ManifestSha256);
    }

    private static bool NpmDependenciesMatch(JsonElement dependencies, DependencyDefinition definition) =>
        dependencies.ValueKind == JsonValueKind.Object && dependencies.EnumerateObject().Count() == 1 &&
        dependencies.TryGetProperty(definition.PackageId, out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == definition.PackageVersion;

    public async Task<OfflineNpmResult> ApplyNpmAsync(string bundle, string pin, string component, string node, string npmCli,
        string destination, CancellationToken token)
    {
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        var plan = await NpmPlanAsync(bundle, snapshot, component, node, npmCli, destination, token);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (runner is not IProcessWorkingDirectoryRunner npmRunner) throw new NotSupportedException();
        var path = Path.Combine(Path.GetDirectoryName(plan.Destination)!, ".localai-npm-" + Guid.NewGuid().ToString("N"));
        using var stage = new WindowsStagingRootFactory().CreateExclusive(path);
        var preserve = false;
        var released = false;
        try
        {
            var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var prefix = "npm/" + component + "/";
            foreach (var file in plan.Files)
            {
                var target = OfflineEnvironmentBundle.Resolve(stage.CanonicalPath, file[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyVerifiedAsync(bundle, file, target, files[file], token);
            }
            // npm owns this private cache copy: never hand it the bundle or user's cache.
            var userConfig = Path.Combine(path, "empty-user.npmrc"); var globalConfig = Path.Combine(path, "empty-global.npmrc");
            File.WriteAllText(userConfig, ""); File.WriteAllText(globalConfig, "");
            stage.Revalidate(); token.ThrowIfCancellationRequested();
            ProcessResult result;
            try
            {
                result = await npmRunner.RunInDirectoryAsync(plan.NodeExecutable,
                    [plan.NpmCli, "ci", "--prefix=" + path, "--cache=" + Path.Combine(path, "cache"), "--offline", "--ignore-scripts",
                     "--global=false", "--no-audit", "--no-fund", "--logs-max=0", "--workspaces=false", "--install-links=false",
                     "--userconfig=" + userConfig, "--globalconfig=" + globalConfig], path, TimeSpan.FromMinutes(20), token);
            }
            catch (ProcessTerminationException)
            { preserve = true; return new(plan, "termination-unconfirmed", null, path, false); }
            var state = result.Cancelled ? "cancelled" : result.TimedOut ? "timed-out" : result.ExitCode == 0 ? "applied" : "failed";
            if (state != "applied") { preserve = true; return new(plan, state, result.ExitCode, path, false); }
            stage.Revalidate(); token.ThrowIfCancellationRequested();
            var installedManifest = Path.Combine(path, "node_modules", plan.Package.Replace('/', Path.DirectorySeparatorChar), "package.json");
            OfflineEnvironmentBundle.EnsureNoLinks(installedManifest);
            using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(installedManifest, token)))
                if (!document.RootElement.TryGetProperty("name", out var name) || name.GetString() != plan.Package ||
                    !document.RootElement.TryGetProperty("version", out var version) || version.GetString() != plan.Version) throw Invalid();
            if (plan.Package == DependencyCatalog.ScipPython.PackageId)
                new ScipPythonWindowsCompatibilityPatch().Apply(Path.Combine(path, "node_modules"), plan.Version);
            stage.Revalidate(); token.ThrowIfCancellationRequested();
            // Publishing requires releasing the no-delete-share handle. Only this new,
            // explicit output can receive the stage; existing prefixes are never merged.
            stage.Dispose(); released = true;
            Directory.Move(path, plan.Destination);
            return new(plan, "applied", result.ExitCode, null, false);
        }
        finally
        {
            if (!preserve && !released) { RemoveNpmChildren(path); stage.Cleanup(); }
            else if (!preserve && Directory.Exists(path)) { RemoveNpmChildren(path); Directory.Delete(path); }
        }
    }

    private static void RemoveNpmChildren(string root)
    {
        OfflineEnvironmentBundle.EnsureNoLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw Invalid();
            if (Directory.Exists(entry)) { RemoveNpmChildren(entry); Directory.Delete(entry); }
            else File.Delete(entry);
        }
    }
}
