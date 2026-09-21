using System.Text.Json.Serialization;

namespace LocalAi.Documentation;

public sealed record DocumentationRetentionEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("delete")] bool Delete,
    [property: JsonPropertyName("reasons")] string[] Reasons);
public sealed record DocumentationRetentionReport(
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("keepPacks")] int KeepPacks,
    [property: JsonPropertyName("entries")] IReadOnlyList<DocumentationRetentionEntry> Entries)
{
    [JsonPropertyName("reclaimableBytes")] public long ReclaimableBytes => Entries.Where(e => e.Delete).Sum(e => e.Bytes);
    [JsonPropertyName("deletedBytes")] public long DeletedBytes => DryRun ? 0 : ReclaimableBytes;
}
public sealed record DocumentationPinReport(
    [property: JsonPropertyName("packagePath")] string PackagePath,
    [property: JsonPropertyName("pinned")] bool Pinned);

public sealed partial class DocumentationPackStore
{
    private sealed record AppliedFileOrigin(string PackId, string ArtifactId, string Hash);
    private sealed record AppliedOrigins(int Schema, Dictionary<string, AppliedFileOrigin> Files, string? PendingPackId);
    private sealed record RetainedItem(string Path, string Kind, string Id, string[] Files, long Bytes, DateTime Modified);

    public DocumentationPinReport Pin(string workingRoot, string packageId, bool pinned = true)
    {
        ValidateLocation(workingRoot);
        if (!IsId(packageId) || !Directory.Exists(Root)) throw Failure("package_missing");
        using var lease = AcquireLock();
        var path = Path.Combine(Root, "packs", packageId);
        RejectLinks(path);
        var pack = Read<Pack>(Path.Combine(path, "manifest.json"));
        if (pack?.RepositoryId != repositoryId) throw Failure("package_missing");
        foreach (var page in new[] { "index.en.html", "index.ru.html" })
        { RejectLinks(Path.Combine(path, page)); if (!File.Exists(Path.Combine(path, page))) throw Failure("package_missing"); }
        var pin = Path.Combine(Root, "pins", packageId + ".json");
        RejectLinks(pin);
        if (pinned) WriteJson(pin, packageId);
        else if (File.Exists(pin)) File.Delete(pin);
        return new(path, pinned);
    }

    // Planning and deletion share the build/apply/pin lock. No saved plan is ever executed.
    public DocumentationRetentionReport Prune(string workingRoot, int keepPacks = 5, bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        ValidateLocation(workingRoot);
        if (keepPacks < 1) throw new ArgumentOutOfRangeException(nameof(keepPacks));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(Root)) return new(dryRun, keepPacks, []);
        using var lease = AcquireLock();
        var items = Inventory();
        var protectedBy = items.ToDictionary(item => item.Path, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        void Keep(RetainedItem item, string reason) => protectedBy[item.Path].Add(reason);
        var pending = Path.Combine(Root, "unfinished.json");
        RejectLinks(pending);
        if (File.Exists(pending))
            foreach (var item in items) Keep(item, "unfinished_build");
        else Plan();
        cancellationToken.ThrowIfCancellationRequested();
        var entries = items.Select(item => new DocumentationRetentionEntry(item.Path, item.Kind, item.Bytes,
            protectedBy[item.Path].Count == 0, protectedBy[item.Path].Count == 0
                ? [item.Kind == "pack" ? "outside_retention" : "unreferenced"] : protectedBy[item.Path].Order(StringComparer.Ordinal).ToArray())).ToArray();
        if (!dryRun)
        {
            // Inventory is flat and refuses reparse points before any deletion. Remove packs
            // before their artifacts, and artifacts before resources/text. Partial failure cannot
            // leave a retained pack referring to a resource deleted by this collector.
            foreach (var item in items.Where(item => protectedBy[item.Path].Count == 0)
                .OrderBy(item => item.Kind switch { "pack" => 0, "artifact" => 1, _ => 2 }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = item.Path;
                if (item.Kind is "pack" or "artifact")
                {
                    target = Path.Combine(Root, "retired", item.Kind + "-" + item.Id);
                    RejectLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    RejectLinks(item.Path);
                    // Atomically remove a directory from the live graph before removing its
                    // files. A crash leaves a recognizable retired entry for the next prune.
                    Directory.Move(item.Path, target);
                }
                foreach (var original in item.Files)
                {
                    var file = item.Kind is "pack" or "artifact" ? Path.Combine(target, Path.GetFileName(original)) : original;
                    RejectLinks(file); File.Delete(file);
                }
                if (item.Kind is "pack" or "artifact" or "retired") { RejectLinks(target); Directory.Delete(target, recursive: false); }
            }
        }
        return new(dryRun, keepPacks, entries);

        void Plan()
        {
            var packItems = items.Where(i => i.Kind == "pack").ToDictionary(i => i.Id, StringComparer.Ordinal);
            var artifactItems = items.Where(i => i.Kind == "artifact").ToDictionary(i => i.Id, StringComparer.Ordinal);
            var texts = items.Where(i => i.Kind == "text").ToDictionary(i => i.Id, StringComparer.Ordinal);
            var resources = items.Where(i => i.Kind == "resource").ToDictionary(i => i.Id, StringComparer.Ordinal);
            var packs = packItems.ToDictionary(p => p.Key, p => Read<Pack>(Path.Combine(p.Value.Path, "manifest.json")) ?? throw Invalid(p.Value.Path), StringComparer.Ordinal);
            var artifacts = artifactItems.ToDictionary(p => p.Key, p => Read<Artifact>(Path.Combine(p.Value.Path, "artifact.json")) ?? throw Invalid(p.Value.Path), StringComparer.Ordinal);
            foreach (var (id, pack) in packs)
            {
                if (pack.RepositoryId != repositoryId || pack.Report?.Units is null) throw Invalid(packItems[id].Path);
                foreach (var unit in pack.Report.Units)
                    if (unit is null || unit.SymbolId is null || (unit.ArtifactId is not null &&
                        (!artifacts.TryGetValue(unit.ArtifactId, out var artifact) || artifact.SymbolId != unit.SymbolId))) throw Invalid(packItems[id].Path);
            }
            foreach (var (id, artifact) in artifacts)
                if (string.IsNullOrEmpty(artifact.SymbolId) || artifact.Files is null || artifact.Files.Count != FileNames.Length ||
                    FileNames.Any(name => !artifact.Files.TryGetValue(name, out var hash) || !IsHash(hash)) ||
                    (artifact.TextKey is not (null or "") && !IsHash(artifact.TextKey)) ||
                    (artifact.Resources is not null && artifact.Resources.Any(r => !DocumentationHtmlResources.ValidName(r.Key, r.Value))))
                    throw Invalid(artifactItems[id].Path);

            void KeepPack(string id, string reason)
            {
                if (!packItems.TryGetValue(id, out var item)) throw Invalid(Path.Combine(Root, "packs", id));
                Keep(item, reason);
            }
            var current = Path.Combine(Root, "current.json");
            RejectLinks(current);
            if (File.Exists(current)) KeepPack(Read<string>(current) ?? throw Invalid(current), "current");
            foreach (var item in packItems.Values.OrderByDescending(i => i.Modified).ThenBy(i => i.Id, StringComparer.Ordinal).Take(keepPacks)) Keep(item, "recent");
            foreach (var pin in MetadataFiles("pins"))
            {
                var id = Read<string>(pin);
                if (!IsId(id) || Path.GetFileName(pin) != id + ".json") throw Invalid(pin);
                KeepPack(id!, "pinned");
            }
            foreach (var pointer in MetadataFiles("latest"))
            {
                var id = Read<string>(pointer);
                if (id is null || !artifactItems.TryGetValue(id, out var item)) throw Invalid(pointer);
                Keep(item, "latest");
            }
            // Preserve two valid versions of each type, including the fallback after a failed build.
            foreach (var group in artifactItems.Values.GroupBy(i => artifacts[i.Id].SymbolId))
            {
                var valid = group.OrderByDescending(i => i.Modified).ThenBy(i => i.Id, StringComparer.Ordinal)
                    .Where(i => ValidArtifact(i.Id, artifacts[i.Id].SymbolId) is not null).Take(2);
                foreach (var item in valid) Keep(item, "previous_valid");
            }
            foreach (var baselinePath in MetadataFiles("applied"))
            {
                var baseline = Read<Dictionary<string, string>>(baselinePath) ?? throw Invalid(baselinePath);
                var originPath = Path.Combine(Root, "applied-origins", Path.GetFileName(baselinePath));
                RejectLinks(originPath);
                var origin = Read<AppliedOrigins>(originPath);
                var tracked = origin is { Schema: 1, Files: not null };
                if (tracked && origin!.PendingPackId is not null) KeepPack(origin.PendingPackId, "apply_in_progress");
                foreach (var (relative, hash) in baseline)
                {
                    if (origin is not { Schema: 1, Files: not null } || !origin.Files.TryGetValue(relative, out var owner) || owner is null || owner.Hash != hash ||
                        !packs.TryGetValue(owner.PackId, out var pack) || !pack.Report.Units.Any(u => u.ArtifactId == owner.ArtifactId) ||
                        !artifacts.TryGetValue(owner.ArtifactId, out var artifact) || !OwnedFileMatches(relative, hash, artifact))
                    { tracked = false; break; }
                    KeepPack(owner.PackId, "applied");
                }
                if (!tracked && baseline.Count > 0)
                    foreach (var item in items) Keep(item, "untracked_apply");
            }
            // A first interrupted apply can have a pending marker before creating its baseline.
            foreach (var path in MetadataFiles("applied-origins"))
            {
                var origin = Read<AppliedOrigins>(path) ?? throw Invalid(path);
                if (origin.Schema != 1 || origin.Files is null) throw Invalid(path);
                if (origin.PendingPackId is not null) KeepPack(origin.PendingPackId, "apply_in_progress");
                if (origin.Files.Count > 0 && !File.Exists(Path.Combine(Root, "applied", Path.GetFileName(path))))
                    foreach (var item in items) Keep(item, "untracked_apply");
            }
            foreach (var (id, pack) in packs.Where(p => protectedBy[packItems[p.Key].Path].Count > 0))
                foreach (var unit in pack.Report.Units.Where(u => u.ArtifactId is not null)) Keep(artifactItems[unit.ArtifactId!], "retained_pack");
            foreach (var (id, artifact) in artifacts.Where(a => protectedBy[artifactItems[a.Key].Path].Count > 0))
            {
                if (artifact.TextKey is { Length: > 0 } && texts.TryGetValue(artifact.TextKey, out var text)) Keep(text, "retained_artifact");
                foreach (var resource in artifact.Resources ?? [])
                    if (resources.TryGetValue(resource.Key, out var item)) Keep(item, "retained_artifact");
            }
            foreach (var item in items.Where(i => i.Kind == "unknown")) Keep(item, "unknown_entry");
        }
    }

    private List<RetainedItem> Inventory()
    {
        var result = new List<RetainedItem>();
        foreach (var (folder, kind) in new[] { ("packs", "pack"), ("artifacts", "artifact") })
        {
            var parent = Path.GetFullPath(Path.Combine(Root, folder));
            RejectLinks(parent);
            if (!Directory.Exists(parent)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal))
            {
                RejectLinks(path);
                if (folder == "artifacts" && Path.GetFileName(path) == "_assets") continue;
                var id = Path.GetFileName(path);
                var files = Directory.Exists(path) ? FlatFiles(path) : [path];
                var known = Directory.Exists(path) && IsId(id);
                Add(path, known ? kind : "unknown", id, files);
            }
        }
        foreach (var (folder, kind, extension) in new[] { ("texts", "text", ".json"), ("artifacts/_assets", "resource", ".css") })
        {
            var parent = Path.GetFullPath(Path.Combine(Root, folder));
            RejectLinks(parent);
            if (!Directory.Exists(parent)) continue;
            foreach (var path in FlatFiles(parent))
            {
                var known = Path.GetExtension(path) == extension && IsHash(Path.GetFileNameWithoutExtension(path));
                Add(path, known ? kind : "unknown", kind == "resource" ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path), [path]);
            }
        }
        var retired = Path.Combine(Root, "retired");
        RejectLinks(retired);
        if (Directory.Exists(retired))
            foreach (var path in Directory.EnumerateFileSystemEntries(retired).Order(StringComparer.Ordinal))
            {
                RejectLinks(path);
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('-');
                var recognized = separator > 0 && name[..separator] is "pack" or "artifact" && IsId(name[(separator + 1)..]) && Directory.Exists(path);
                Add(path, recognized ? "retired" : "unknown", name, Directory.Exists(path) ? FlatFiles(path) : [path]);
            }
        return result;

        void Add(string path, string kind, string id, string[] files)
        {
            var metadata = kind == "pack" ? Path.Combine(path, "manifest.json") : kind == "artifact" ? Path.Combine(path, "artifact.json") : path;
            result.Add(new(path, kind, id, files, files.Sum(file => new FileInfo(file).Length), File.GetLastWriteTimeUtc(metadata)));
        }
    }

    private string[] MetadataFiles(string folder)
    {
        var path = Path.Combine(Root, folder);
        RejectLinks(path);
        if (!Directory.Exists(path)) return [];
        return FlatFiles(path).Where(p => Path.GetExtension(p) == ".json").ToArray();
    }
    private static string[] FlatFiles(string path)
    {
        var entries = Directory.GetFileSystemEntries(path);
        foreach (var entry in entries)
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) throw Invalid(entry);
        }
        return entries.Order(StringComparer.Ordinal).ToArray();
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool OwnedFileMatches(string relative, string hash, Artifact artifact)
    {
        var symbolDirectory = Hash(System.Text.Encoding.UTF8.GetBytes(artifact.SymbolId)) + "/";
        return relative.StartsWith(symbolDirectory, StringComparison.Ordinal)
            ? artifact.Files.TryGetValue(relative[symbolDirectory.Length..], out var expected) && expected == hash
            : relative.StartsWith("_assets/", StringComparison.Ordinal) && artifact.Resources?.TryGetValue(relative[8..], out var resourceHash) == true && resourceHash == hash;
    }
    private static DocumentationValidationException Invalid(string path) => Failure("retention_metadata_invalid", path);
}
