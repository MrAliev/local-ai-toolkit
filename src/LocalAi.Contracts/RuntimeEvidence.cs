using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

public sealed record RuntimeEvidenceField(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>Opaque, prevalidated index metadata supplied by the caller; this reader never scans repositories.</summary>
public sealed record RuntimeIndexEvidence(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("manifestSha256")] string? ManifestSha256,
    [property: JsonPropertyName("generationId")] string? GenerationId,
    [property: JsonPropertyName("sourceCommit")] string? SourceCommit,
    [property: JsonPropertyName("embeddingModelSha256")] string? EmbeddingModelSha256,
    [property: JsonPropertyName("basis")] string Basis = "recorded");

public sealed record RuntimeEvidenceSnapshot(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("componentFileSha256")] RuntimeEvidenceField ComponentFileSha256,
    [property: JsonPropertyName("imageKind")] RuntimeEvidenceField ImageKind,
    [property: JsonPropertyName("assemblyVersion")] RuntimeEvidenceField AssemblyVersion,
    [property: JsonPropertyName("declaredSourceCommit")] RuntimeEvidenceField DeclaredSourceCommit,
    [property: JsonPropertyName("installedVersionDirectory")] RuntimeEvidenceField InstalledVersionDirectory,
    [property: JsonPropertyName("installedReleaseVersion")] RuntimeEvidenceField InstalledReleaseVersion,
    [property: JsonPropertyName("installedRecordSha256")] RuntimeEvidenceField InstalledRecordSha256,
    [property: JsonPropertyName("catalogVersion")] RuntimeEvidenceField CatalogVersion,
    [property: JsonPropertyName("catalogSha256")] RuntimeEvidenceField CatalogSha256,
    [property: JsonPropertyName("catalogSource")] RuntimeEvidenceField CatalogSource,
    [property: JsonPropertyName("modelSelection")] RuntimeEvidenceField ModelSelection,
    [property: JsonPropertyName("releaseManifestSha256")] RuntimeEvidenceField ReleaseManifestSha256,
    [property: JsonPropertyName("indexes")] IReadOnlyList<RuntimeIndexEvidence> Indexes,
    [property: JsonPropertyName("indexState")] RuntimeEvidenceField IndexState)
{
    [JsonPropertyName("schema")]
    public string Schema => "localai.runtime-evidence.v1";
}

/// <summary>
/// A bounded, read-only observation. A file digest describes bytes read from disk, never
/// loaded memory, signatures, or build provenance. Installation metadata is only recorded.
/// No model, backend, repository scanner, settings writer, or migration is invoked.
/// </summary>
public static class RuntimeEvidenceReader
{
    public const long MaximumComponentBytes = 256L * 1024 * 1024;
    public const int MaximumMetadataBytes = 64 * 1024;
    public const int MaximumCatalogBytes = 1024 * 1024;
    public const int MaximumIndexes = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static RuntimeEvidenceSnapshot Capture(
        Assembly componentAssembly,
        string component,
        string? runtimeRoot = null,
        IReadOnlyList<RuntimeIndexEvidence>? indexes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(componentAssembly);
        cancellationToken.ThrowIfCancellationRequested();
        var componentName = SafeToken(component, 64) ? component : "unknown";
        var (image, imageKind) = ReadComponent(componentAssembly, cancellationToken);
        var assemblyVersion = Unknown("assembly_version_unavailable");
        var sourceCommit = Unknown("full_source_commit_not_declared");
        try
        {
            var version = componentAssembly.GetName().Version?.ToString();
            if (version is not null && version.Length <= 32 && version.All(character => char.IsAsciiDigit(character) || character == '.'))
                assemblyVersion = Recorded(version, "assembly_metadata_only");
            var info = componentAssembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (info is { Length: <= 256 })
            {
                var separator = info.LastIndexOf('+');
                if (separator >= 0 && Hex(info[(separator + 1)..], 40, 40))
                    sourceCommit = Recorded(info[(separator + 1)..].ToLowerInvariant(), "assembly_metadata_not_build_provenance");
            }
        }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException or IOException or CustomAttributeFormatException)
        {
            // A partial/unsupported reflection surface does not turn into a guessed identity.
        }

        string? root = runtimeRoot;
        if (root is null)
        {
            try { root = ModelResidencyPolicyStore.DefaultRuntimeRoot; }
            catch (InvalidOperationException) { }
        }
        var installedDirectory = Unknown("pointer_not_available");
        var installedVersion = Unknown("installation_record_not_available");
        var recordHash = Unknown("installation_record_not_available");
        var catalogVersion = Unknown("catalog_not_available");
        var catalogHash = Unknown("catalog_not_available");
        var catalogSource = Unknown("catalog_not_available");
        if (TryLocalPath(root, out var localRoot))
        {
            ReadInstallation(localRoot, cancellationToken, out installedDirectory, out installedVersion, out recordHash);
            ReadCatalog(localRoot, cancellationToken, out catalogVersion, out catalogHash, out catalogSource);
        }
        else
        {
            installedDirectory = installedVersion = recordHash = Unknown("unsafe_runtime_path");
            catalogVersion = catalogHash = catalogSource = Unknown("unsafe_runtime_path");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var (safeIndexes, indexState) = CopyIndexes(indexes);
        return new(componentName, image, imageKind, assemblyVersion, sourceCommit,
            installedDirectory, installedVersion, recordHash, catalogVersion, catalogHash, catalogSource,
            new(null, "not_evaluated", "no_inference_or_backend_query"),
            Unknown("no_retained_release_manifest_evidence"), safeIndexes, indexState);
    }

    public static string ToJson(RuntimeEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, LocalAiJson.Strict);
    }

    private static (RuntimeEvidenceField Hash, RuntimeEvidenceField Kind) ReadComponent(Assembly assembly, CancellationToken cancellationToken)
    {
        string? path;
        try { path = assembly.IsDynamic ? null : assembly.Location; }
        catch (NotSupportedException) { path = null; }
        var kind = "assembly_file";
        if (string.IsNullOrEmpty(path))
        {
            if (!ReferenceEquals(assembly, Assembly.GetEntryAssembly()))
                return (Unknown("component_file_unavailable"), Unknown("component_file_unavailable"));
            path = Environment.ProcessPath;
            kind = "entry_bundle";
            if (string.IsNullOrEmpty(path) || string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
                return (Unknown("host_is_not_component"), Unknown("host_is_not_component"));
        }
        var read = ReadFile(path, MaximumComponentBytes, includeBytes: false, cancellationToken);
        return (read.Sha256 is null ? Unknown(read.Reason) : Observed(read.Sha256, "file_bytes_not_memory_or_provenance"),
            Observed(kind, "component_location_kind"));
    }

    private static void ReadInstallation(string root, CancellationToken cancellationToken,
        out RuntimeEvidenceField directory, out RuntimeEvidenceField version, out RuntimeEvidenceField recordHash)
    {
        directory = Unknown("pointer_not_available");
        version = Unknown("installation_record_not_available");
        var pointer = ReadFile(Path.Combine(root, "bin", "current.json"), MaximumMetadataBytes, true, cancellationToken);
        if (pointer.Bytes is not null)
        {
            try
            {
                using var json = ParseMetadata(pointer.Bytes, 4);
                var value = json.RootElement;
                RequireShape(value, "schemaVersion", "version");
                if (value.GetProperty("schemaVersion").GetInt32() != 1) throw new JsonException();
                var candidate = value.GetProperty("version").GetString();
                if (!Hex(candidate, 12, 64)) throw new JsonException();
                directory = Recorded(candidate!, "pointer_only_not_running_component");
            }
            catch (Exception failure) when (InvalidJson(failure)) { directory = Unknown("invalid_pointer"); }
        }
        else directory = Unknown(pointer.Reason);

        var record = ReadFile(Path.Combine(root, "bin", "installed-release.json"), MaximumMetadataBytes, true, cancellationToken);
        recordHash = record.Sha256 is null ? Unknown(record.Reason) : Observed(record.Sha256, "installation_record_bytes_only");
        if (record.Bytes is null) { version = Unknown(record.Reason); return; }
        try
        {
            using var json = ParseMetadata(record.Bytes, 4);
            var value = json.RootElement;
            RequireShape(value, "schemaVersion", "versionDirectory", "releaseVersion");
            if (value.GetProperty("schemaVersion").GetInt32() != 1) throw new JsonException();
            var recordedDirectory = value.GetProperty("versionDirectory").GetString();
            var release = value.GetProperty("releaseVersion").GetString();
            if (!Hex(recordedDirectory, 12, 64) || !SafeVersion(release)) throw new JsonException();
            version = directory.Value is not null && string.Equals(directory.Value, recordedDirectory, StringComparison.Ordinal)
                ? Recorded(release!, "pointer_record_agreement_not_binary_verification")
                : Unknown("pointer_record_mismatch");
        }
        catch (Exception failure) when (InvalidJson(failure)) { version = Unknown("invalid_installation_record"); }
    }

    private static void ReadCatalog(string root, CancellationToken cancellationToken,
        out RuntimeEvidenceField version, out RuntimeEvidenceField hash, out RuntimeEvidenceField source)
    {
        var read = ReadFile(Path.Combine(root, RuntimeDirectories.SettingsDirectoryName, ModelRoutingCatalogStore.FileName),
            MaximumCatalogBytes, true, cancellationToken);
        version = hash = source = Unknown(read.Reason);
        try
        {
            ModelRoutingCatalogDocument document;
            string digest;
            string sourceKind;
            if (read.Bytes is not null)
            {
                using var parsed = ParseMetadata(read.Bytes, 32);
                RejectDuplicateProperties(parsed.RootElement);
                document = ModelRoutingCatalogStore.ParseConfigured(Utf8Payload(read.Bytes));
                digest = document.CatalogVersion["custom-".Length..];
                sourceKind = "configured";
            }
            else if (read.Reason == "missing")
            {
                document = ModelRoutingCatalogStore.LoadEmbedded();
                digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, LocalAiJson.Strict))).ToLowerInvariant();
                sourceKind = "embedded";
            }
            else return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeToken(document.CatalogVersion, 128)) throw new InvalidDataException();
            version = Recorded(document.CatalogVersion, "effective_catalog_identity");
            hash = Observed(digest, "canonical_catalog_document_not_raw_file_digest");
            source = Observed(sourceKind, "read_only_catalog_source");
        }
        catch (Exception failure) when (InvalidJson(failure) || failure is InvalidDataException)
        {
            version = hash = source = Unknown("invalid_catalog");
        }
    }

    private static (IReadOnlyList<RuntimeIndexEvidence> Indexes, RuntimeEvidenceField State) CopyIndexes(IReadOnlyList<RuntimeIndexEvidence>? indexes)
    {
        if (indexes is null) return (Array.Empty<RuntimeIndexEvidence>(), new(null, "not_evaluated", "no_index_scan"));
        if (indexes.Count > MaximumIndexes) return (Array.Empty<RuntimeIndexEvidence>(), Unknown("index_evidence_limit"));
        var result = new List<RuntimeIndexEvidence>(indexes.Count);
        var ordinals = new HashSet<int>();
        var hasUnknown = false;
        foreach (var index in indexes)
        {
            if (index is null || index.Ordinal < 1 || !ordinals.Add(index.Ordinal))
            {
                hasUnknown = true;
                continue;
            }
            if (index.Basis != "recorded" ||
                !OptionalHex(index.ManifestSha256, 64, 64) || !OptionalHex(index.GenerationId, 12, 64) ||
                !OptionalHex(index.SourceCommit, 40, 40) || !OptionalHex(index.EmbeddingModelSha256, 64, 64))
            {
                // Preserve this slot as explicitly unknown, without copying untrusted values
                // or erasing independent, prevalidated evidence for the other repositories.
                result.Add(new(index.Ordinal, null, null, null, null, "unknown"));
                hasUnknown = true;
                continue;
            }
            result.Add(index with { });
        }
        var state = hasUnknown
            ? Unknown(result.Any(index => index.Basis == "recorded") ? "partial_index_evidence" : "index_evidence_unknown")
            : Recorded("supplied", "prevalidated_index_list_not_navigation_health");
        return (result.AsReadOnly(), state);
    }

    private sealed record FileRead(byte[]? Bytes, string? Sha256, string Reason);

    private static FileRead ReadFile(string? path, long maximumBytes, bool includeBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryLocalPath(path, out var full)) return new(null, null, "unsafe_path");
        try
        {
            var ancestors = new Stack<string>();
            for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) ancestors.Push(current);
            while (ancestors.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return new(null, null, "linked_path");
                if (current != full && (attributes & FileAttributes.Directory) == 0) return new(null, null, "unsafe_path");
                if (current == full && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0) return new(null, null, "not_regular_file");
            }
            var before = new FileInfo(full);
            var length = before.Length;
            var written = before.LastWriteTimeUtc;
            if (length < 1 || length > maximumBytes) return new(null, null, "file_size_out_of_bounds");
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length != length) return new(null, null, "file_changed_during_read");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var contents = includeBytes ? new MemoryStream(checked((int)length)) : null;
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                total += count;
                if (total > length || total > maximumBytes) return new(null, null, "file_changed_during_read");
                digest.AppendData(buffer, 0, count);
                contents?.Write(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            before.Refresh();
            if (total != length || stream.Length != length || !before.Exists || before.Length != length || before.LastWriteTimeUtc != written ||
                (before.Attributes & FileAttributes.ReparsePoint) != 0) return new(null, null, "file_changed_during_read");
            return new(contents?.ToArray(), Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), "file_bytes_read");
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException) { return new(null, null, "missing"); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new(null, null, "unreadable_file");
        }
    }

    private static bool TryLocalPath(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path)) return false;
        try
        {
            full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) return false;
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static JsonDocument ParseMetadata(byte[] bytes, int depth) =>
        JsonDocument.Parse(StrictUtf8.GetString(Utf8Payload(bytes)), new JsonDocumentOptions { MaxDepth = depth });

    private static ReadOnlySpan<byte> Utf8Payload(byte[] bytes) =>
        bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes;

    private static void RequireShape(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != names.Length || names.Any(name => properties.Count(property => property.NameEquals(name)) != 1)) throw new JsonException();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private static bool SafeToken(string? value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '+');
    private static bool SafeVersion(string? value)
    {
        if (!SafeToken(value, 100) || !char.IsAsciiDigit(value![0])) return false;
        var numeric = value.Split('-', '+')[0].Split('.');
        return numeric.Length == 3 && numeric.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }
    private static bool Hex(string? value, int minimum, int maximum) => value is not null && value.Length >= minimum && value.Length <= maximum && value.All(char.IsAsciiHexDigit);
    private static bool OptionalHex(string? value, int minimum, int maximum) => value is null || Hex(value, minimum, maximum);
    private static bool InvalidJson(Exception failure) => failure is JsonException or InvalidOperationException or FormatException or DecoderFallbackException or OverflowException;
    private static RuntimeEvidenceField Unknown(string reason) => new(null, "unknown", reason);
    private static RuntimeEvidenceField Recorded(string value, string reason) => new(value, "recorded", reason);
    private static RuntimeEvidenceField Observed(string value, string reason) => new(value, "observed", reason);
}
