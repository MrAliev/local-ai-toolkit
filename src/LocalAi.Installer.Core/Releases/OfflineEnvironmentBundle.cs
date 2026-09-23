using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineSourceComponent(string Kind, string Id, string Version, string[] Files);
public sealed record OfflineSourceDocument(int SchemaVersion, OfflineSourceComponent[] Components);
public sealed record OfflineBundleFile(string Path, long Bytes, string Sha256);
public sealed record OfflineBundleComponent(string Kind, string Id, string? Version, string Availability, string[] Files);
public sealed record OfflineBundleManifest(int SchemaVersion, string ReleaseVersion, string CatalogVersion,
    string Platform, long TransferredBytes, bool CompleteForInstallation, string InstallationState,
    OfflineBundleFile[] Files, OfflineBundleComponent[] Components);
public sealed record OfflineBundleResult(string ManifestSha256, OfflineBundleManifest Manifest, long TotalBundleBytes);

public sealed class OfflineEnvironmentBundle(ReleaseManifestVerifier verifier)
{
    public const string ManifestName = "offline-manifest.json";
    public const string SourceName = "offline-components.json";
    private const int MaximumFiles = 8192;
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024 * 1024;
    private static readonly string[] ReleaseFiles = ["release-manifest.json", "release-manifest.sig", "localai-package.zip"];
    public static JsonSerializerOptions Json { get; } = new(LocalAiJson.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public async Task<OfflineBundleResult> BuildAsync(string source, string destination, ModelRoutingCatalogDocument catalog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (Path.Exists(destination)) throw new IOException("Output already exists.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent)) throw new InvalidDataException("The output parent must exist.");
        EnsureNoLinks(source); EnsureNoLinks(parent);
        var sourcePrefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output is inside source.");
        var release = await ReadReleaseAsync(source, token);
        var description = Read<OfflineSourceDocument>(await ReadDocumentAsync(Resolve(source, SourceName), MaximumDocumentBytes, token));
        if (description.SchemaVersion != 1 || description.Components is null || description.Components.Length > 256)
            throw new InvalidDataException("Invalid source inventory.");
        var components = CreateComponents(description, catalog, release);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in components.SelectMany(component => component.Files))
        {
            ValidatePath(path);
            if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || path.Equals(SourceName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reserved inventory path.");
            if (paths.TryGetValue(path, out var previous) && previous != path) throw new InvalidDataException("Case-colliding paths.");
            paths[path] = path;
        }
        if (paths.Count > MaximumFiles) throw new InvalidDataException("Too many files.");
        var staging = Path.Combine(parent, ".localai-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<OfflineBundleFile>();
            long total = 0;
            foreach (var path in paths.Values.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var input = Resolve(source, path);
                var output = Resolve(staging, path);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var item = await CopyAndHashAsync(input, output, path, MaximumTotalBytes - total, token);
                total = checked(total + item.Bytes); files.Add(item);
            }
            // Re-read the copied metadata and compare the copied package. Source changes cannot
            // substitute different bytes between signature verification and publication.
            var copiedRelease = await ReadReleaseAsync(staging, token);
            ValidatePackage(files, copiedRelease);
            if (copiedRelease.ReleaseVersion != release.ReleaseVersion) throw new InvalidDataException("Release changed while copying.");
            var manifest = new OfflineBundleManifest(1, copiedRelease.ReleaseVersion, catalog.CatalogVersion, "win-x64",
                total, false, "installation-unverified", files.ToArray(), components);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Inventory is too large.");
            await File.WriteAllBytesAsync(Path.Combine(staging, ManifestName), bytes, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return new(Hash(bytes), manifest, checked(total + bytes.Length));
        }
        finally
        {
            // Only this invocation's freshly created sibling staging directory is removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<OfflineBundleResult> VerifyAsync(string directory, string expectedManifestSha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); EnsureNoLinks(directory);
        var bytes = await ReadDocumentAsync(Resolve(directory, ManifestName), MaximumDocumentBytes, token);
        var actualHash = Hash(bytes);
        if (!IsHash(expectedManifestSha256) || !actualHash.Equals(expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Untrusted inventory hash.");
        var manifest = Read<OfflineBundleManifest>(bytes);
        if (manifest.SchemaVersion != 1 || manifest.Platform != "win-x64" || manifest.CompleteForInstallation ||
            manifest.InstallationState != "installation-unverified" || manifest.Files is null || manifest.Files.Length is < 3 or > MaximumFiles ||
            manifest.Components is null || manifest.Components.Length > 257 || manifest.TransferredBytes is < 0 or > MaximumTotalBytes)
            throw new InvalidDataException("Invalid bundle inventory.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsHash(file.Sha256) || file.Bytes < 0 || !expected.Add(file.Path))
                throw new InvalidDataException("Invalid file inventory.");
            ValidatePath(file.Path); total = checked(total + file.Bytes);
            if (total > MaximumTotalBytes) throw new InvalidDataException("Bundle is too large.");
        }
        if (total != manifest.TransferredBytes) throw new InvalidDataException("Invalid byte total.");
        var actual = EnumerateFiles(directory, token);
        if (!expected.SetEquals(actual)) throw new InvalidDataException("Missing or unlisted bundle files.");
        foreach (var file in manifest.Files)
        {
            var actualFile = await CopyAndHashAsync(Resolve(directory, file.Path), null, file.Path, file.Bytes, token);
            if (actualFile.Bytes != file.Bytes || !actualFile.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Transferred file mismatch.");
        }
        var release = await ReadReleaseAsync(directory, token);
        ValidatePackage(manifest.Files, release);
        if (release.ReleaseVersion != manifest.ReleaseVersion) throw new InvalidDataException("Release identity mismatch.");
        return new(actualHash, manifest, checked(total + bytes.Length));
    }

    private static OfflineBundleComponent[] CreateComponents(OfflineSourceDocument source, ModelRoutingCatalogDocument catalog, ReleaseManifest release)
    {
        var result = new List<OfflineBundleComponent> { new("runtime", "localai", release.ReleaseVersion, "included", ReleaseFiles) };
        result.AddRange(DependencyCatalog.Supported.Select(item => new OfflineBundleComponent("dependency", item.ActionId, null, "missing", [])));
        result.AddRange(catalog.Models.Where(item => item.Lifecycle != LocalModelLifecycle.Disabled && item.InstallPolicy != LocalModelInstallPolicy.Never)
            .OrderBy(item => item.Tag, StringComparer.Ordinal).Select(item => new OfflineBundleComponent("model", item.Tag, null, "missing", [])));
        var used = new HashSet<(string, string)>();
        foreach (var item in source.Components)
        {
            if (item is null || item.Files is null || item.Files.Length is < 1 or > MaximumFiles ||
                string.IsNullOrWhiteSpace(item.Version) || item.Version.Length > 128 || item.Version.Any(char.IsControl) || !used.Add((item.Kind, item.Id)))
                throw new InvalidDataException("Invalid component.");
            var index = result.FindIndex(candidate => candidate.Kind == item.Kind && candidate.Id == item.Id);
            if (index < 0 || item.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Files.Length)
                throw new InvalidDataException("Unknown or duplicate component.");
            if (item.Kind == "runtime" && item.Version != release.ReleaseVersion) throw new InvalidDataException("Runtime version mismatch.");
            result[index] = new(item.Kind, item.Id, item.Version, "included",
                (item.Kind == "runtime" ? ReleaseFiles.Concat(item.Files) : item.Files).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        return result.ToArray();
    }

    private async Task<ReleaseManifest> ReadReleaseAsync(string directory, CancellationToken token)
    {
        var manifest = await ReadDocumentAsync(Resolve(directory, ReleaseFiles[0]), 1024 * 1024, token);
        var signature = await ReadDocumentAsync(Resolve(directory, ReleaseFiles[1]), 64, token);
        try { return verifier.Verify(manifest, signature); }
        catch (ReleaseVerificationException failure) { throw new InvalidDataException("Untrusted release metadata.", failure); }
    }

    private static void ValidatePackage(IEnumerable<OfflineBundleFile> files, ReleaseManifest release)
    {
        var package = files.SingleOrDefault(file => file.Path == ReleaseFiles[2]);
        if (package is null || package.Bytes != release.PackageSize || !package.Sha256.Equals(release.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package does not match signed release.");
    }

    private static T Read<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty inventory."); }
        catch (JsonException failure) { throw new InvalidDataException("Invalid inventory JSON.", failure); }
    }

    private static async Task<byte[]> ReadDocumentAsync(string path, int maximum, CancellationToken token)
    {
        EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length is <= 0 || input.Length > maximum) throw new InvalidDataException("Invalid document size.");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
        if (input.ReadByte() != -1) throw new InvalidDataException("Document changed.");
        return bytes;
    }

    internal static async Task<OfflineBundleFile> CopyAndHashAsync(string source, string? destination, string relative, long maximum, CancellationToken token)
    {
        EnsureNoLinks(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximum) throw new InvalidDataException("File exceeds expected size.");
        await using var output = destination is null ? null : new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token); if (read == 0) break;
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("File grew beyond expected size.");
            hash.AppendData(buffer.AsSpan(0, read));
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return new(relative, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static HashSet<string> EnumerateFiles(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumFiles * 2) throw new InvalidDataException("Too many bundle entries.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links are not permitted.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (!result.Add(Path.GetRelativePath(root, path).Replace('\\', '/'))) throw new InvalidDataException("Case-colliding files.");
            }
        return result;
    }

    internal static string Resolve(string root, string relative)
    {
        ValidatePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes bundle.");
        return path;
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path) || path.Any(char.IsControl) ||
            path.IndexOfAny(['\\', ':', '<', '>', '"', '|', '?', '*']) >= 0) throw new InvalidDataException("Invalid relative path.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
                throw new InvalidDataException("Invalid path component.");
        }
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links are not permitted.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
