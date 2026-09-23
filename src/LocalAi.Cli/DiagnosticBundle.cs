using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;

namespace LocalAi.Cli;

internal sealed record DiagnosticSnapshot(JsonElement Manifest, IReadOnlyDictionary<string, JsonElement> Contents);

/// <summary>A bounded projection of recorded state. Never copies a source file into a bundle.</summary>
internal static class DiagnosticBundle
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal const int MaximumJobs = 1024;
    internal const int MaximumRepositories = 256;
    internal const int MaximumReports = 2048;
    internal const int MaximumFileBytes = 1024 * 1024;
    internal const int MaximumTotalBytes = 32 * 1024 * 1024;
    private static readonly string[] Excluded = ["prompts", "documents", "model_answers", "raw_logs", "exception_messages", "paths", "repository_names", "model_names", "job_and_call_ids", "settings", "environment_variables"];

    internal static Task<DiagnosticSnapshot> CaptureAsync(string runtimeRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var reader = new SnapshotReader(runtimeRoot, cancellationToken);
        var contents = new Dictionary<string, JsonElement>
        {
            ["versions.json"] = Element(reader.Versions()),
            ["queue.json"] = Element(reader.Queue()),
            ["indexes.json"] = Element(reader.Indexes()),
            ["errors.json"] = Element(reader.Errors()),
            ["metrics.json"] = Element(reader.Metrics()),
        };
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = Element(new
        {
            schemaVersion = 1, startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
            scope = "recorded_state_only", consistency = "independent_file_reads_not_atomic", liveHealthChecked = false,
            indexFreshnessChecked = false, excluded = Excluded,
            limits = new { jobs = MaximumJobs, repositories = MaximumRepositories, reports = MaximumReports, fileBytes = MaximumFileBytes, totalBytes = MaximumTotalBytes },
            files = contents.Select(pair => new { name = pair.Key, bytes = Bytes(pair.Value).Length, sha256 = Convert.ToHexString(SHA256.HashData(Bytes(pair.Value))) }).ToArray(),
        });
        return Task.FromResult(new DiagnosticSnapshot(manifest, contents));
    }

    internal static async Task WriteAsync(DiagnosticSnapshot snapshot, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new DiagnosticOutputExistsException();
        // No runtime writes, parent creation or replacement. A cancelled write leaves no ZIP.
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (name, value) in snapshot.Contents.Prepend(new KeyValuePair<string, JsonElement>("manifest.json", snapshot.Manifest)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                        await entry.WriteAsync(Bytes(value), cancellationToken);
                    }
                }
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { throw new DiagnosticOutputExistsException(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Bytes(JsonElement element) => JsonSerializer.SerializeToUtf8Bytes(element, Json);
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException;
    private static string Reason(Exception error) => error switch
    {
        DiagnosticLimitException => "limit",
        DiagnosticLinkedSourceException => "linked_source",
        FileNotFoundException or DirectoryNotFoundException => "missing",
        JsonException or InvalidDataException or ArgumentException or OverflowException => "invalid",
        _ => "unreadable",
    };
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var number) ? number : null;
    private static string EnumName<T>(JsonElement value, string name) where T : struct, Enum =>
        Text(value, name) is { } text && Enum.GetNames<T>().Contains(text, StringComparer.Ordinal) ? text : "Unknown";
    private static DateTimeOffset? Timestamp(JsonElement value, string name) => value.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.String && child.TryGetDateTimeOffset(out var date) ? date.ToUniversalTime() : null;
    private static string? SafeVersion(string? value) => value is not null && Regex.IsMatch(value, @"\A\d{1,4}\.\d{1,4}\.\d{1,4}(?:-RC\d{1,4})?\z", RegexOptions.CultureInvariant) ? value : null;
    private static string? SafeHash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{12,64}\z", RegexOptions.CultureInvariant) ? value : null;

    private sealed class Coverage
    {
        public string State { get; set; } = "available";
        public int Read { get; set; }
        public int Omitted { get; set; }
        public bool LimitReached { get; set; }
        public Dictionary<string, int> Issues { get; } = new(StringComparer.Ordinal);
        internal void Omit(string reason)
        {
            Omitted++;
            State = "partial";
            LimitReached |= reason == "limit";
            Issues[reason] = Issues.GetValueOrDefault(reason) + 1;
        }
    }

    private sealed class SnapshotReader(string root, CancellationToken cancellationToken)
    {
        private readonly string _root = Path.GetFullPath(root);
        private int _remaining = MaximumTotalBytes;

        // Inspect every component, including the runtime root's ancestors. Refuse links rather
        // than following a telemetry or repository directory outside the selected runtime.
        private static void RequireRegularPath(string path)
        {
            for (var item = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; item is not null; item = Directory.GetParent(item.FullName))
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0) throw new DiagnosticLinkedSourceException();
        }

        private byte[] ReadBytes(string path, int maximum = MaximumFileBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireRegularPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > maximum || stream.Length > _remaining) throw new DiagnosticLimitException();
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = stream.Read(bytes)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + count > maximum || count > _remaining) throw new DiagnosticLimitException();
                _remaining -= count;
                buffer.Write(bytes, 0, count);
            }
            return buffer.ToArray();
        }

        private JsonElement? Read(string path, Coverage coverage, int maximum = MaximumFileBytes)
        {
            try
            {
                using var json = JsonDocument.Parse(ReadBytes(path, maximum), new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                return json.RootElement.Clone();
            }
            catch (Exception error) when (Recoverable(error)) { coverage.Omit(Reason(error)); return null; }
        }

        private string[] Children(string relative, bool directories, int maximum, Coverage coverage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_root, relative);
            try
            {
                RequireRegularPath(path);
                var entries = new List<string>();
                foreach (var item in directories ? Directory.EnumerateDirectories(path) : Directory.EnumerateFiles(path, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count == maximum) { coverage.Omit("limit"); break; }
                    entries.Add(item);
                }
                // Sort only the bounded sample. A truncated collection is not the newest N.
                return entries.Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception error) when (Recoverable(error))
            {
                coverage.Omit(Reason(error));
                coverage.State = error is FileNotFoundException or DirectoryNotFoundException ? "missing" : "unreadable";
                return [];
            }
        }

        internal object Versions()
        {
            var source = new Coverage();
            var pointer = Read(Path.Combine(_root, "bin", "current.json"), source, 4096);
            var release = Read(Path.Combine(_root, "bin", "installed-release.json"), source, 4096);
            var active = pointer is { } p && Number(p, "schemaVersion") == 1 ? SafeHash(Text(p, "version")) : null;
            var installed = release is { } r && Number(r, "schemaVersion") == 1 && active is not null && Text(r, "versionDirectory") == active
                ? SafeVersion(Text(r, "releaseVersion")) : null;
            source.Read = (pointer is null ? 0 : 1) + (release is null ? 0 : 1);
            var informational = typeof(DiagnosticBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return new
            {
                source, collectorVersion = typeof(DiagnosticBundle).Assembly.GetName().Version?.ToString(),
                collectorCommit = SafeHash(informational?.Split('+').Last()),
                dotnetVersion = Environment.Version.ToString(), osVersion = Environment.OSVersion.Version.ToString(),
                platform = Environment.OSVersion.Platform.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                installedRelease = installed, activeVersionDirectory = active, installedVersionStatus = installed is null ? "unknown" : "recorded_pointer_match",
            };
        }

        internal object Queue()
        {
            var source = new Coverage();
            var states = Enum.GetNames<LocalJobState>().Append("Unknown").ToDictionary(name => name, _ => 0);
            foreach (var directory in Children("jobs", true, MaximumJobs, source))
            {
                var state = Read(Path.Combine(directory, "state.json"), source, 65536);
                if (state is null) continue;
                if (Number(state.Value, "SchemaVersion") != 1) { source.Omit("unsupported_schema"); continue; }
                var name = EnumName<LocalJobState>(state.Value, "State");
                if (name == "Unknown") { source.Omit("invalid"); continue; }
                states[name]++;
                source.Read++;
            }
            return new { source, states, scope = "active_job_directory_only", leaseRecoveryPerformed = false };
        }

        internal object Indexes()
        {
            var source = new Coverage();
            var repositories = new List<object>();
            foreach (var directory in Children("repositories", true, MaximumRepositories, source))
            {
                var manifestSource = new Coverage();
                var progressSource = new Coverage();
                var document = Read(Path.Combine(directory, "manifest.json"), manifestSource);
                var progressDocument = Read(Path.Combine(directory, "progress.json"), progressSource, 65536);
                RepositoryManifest? manifest = null;
                RepositoryIndexProgress? progress = null;
                try
                {
                    if (document is { } m)
                    {
                        if (Number(m, "SchemaVersion") != 1) throw new InvalidDataException();
                        manifest = m.GetProperty("Manifest").Deserialize<RepositoryManifest>(LocalAiJson.Strict) ?? throw new InvalidDataException();
                        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, LocalAiJson.Strict)));
                        if (!string.Equals(Text(m, "Checksum"), checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        manifestSource.Read++;
                    }
                }
                catch (Exception error) when (Recoverable(error) || error is KeyNotFoundException)
                { manifest = null; manifestSource.Omit("invalid"); }
                try
                {
                    if (progressDocument is { } p)
                    {
                        if (Number(p, "SchemaVersion") != 1) throw new InvalidDataException();
                        progress = p.GetProperty("Progress").Deserialize<RepositoryIndexProgress>(LocalAiJson.Strict) ?? throw new InvalidDataException();
                        if (!Enum.IsDefined(progress.Phase) || progress.ProcessedChunks < 0 || progress.TotalChunks < progress.ProcessedChunks
                            || progress.UpdatedAtUtc == default) throw new InvalidDataException();
                        progressSource.Read++;
                    }
                }
                catch (Exception error) when (Recoverable(error) || error is KeyNotFoundException)
                { progress = null; progressSource.Omit("invalid"); }
                repositories.Add(new
                {
                    ordinal = repositories.Count + 1, manifestSource, progressSource,
                    recordedState = manifest is not null && Enum.IsDefined(manifest.State) ? manifest.State.ToString() : "Unknown",
                    recordedAtUtc = manifest?.UpdatedAtUtc.ToUniversalTime(),
                    indexFormatVersion = manifest?.IndexFormatVersion, chunkFormatVersion = manifest?.ChunkFormatVersion,
                    activeWorktrees = manifest?.ActiveWorktrees?.Count,
                    buildPhase = progress?.Phase.ToString(), buildUpdatedAtUtc = progress?.UpdatedAtUtc.ToUniversalTime(),
                    processedChunks = progress?.ProcessedChunks, totalChunks = progress?.TotalChunks,
                });
                source.Read++;
            }
            return new { source, repositories, freshnessChecked = false };
        }

        internal object Errors()
        {
            var source = new Coverage();
            var groups = new Dictionary<(string Operation, string Failure), (int Count, DateTimeOffset? Latest)>();
            string[] operations = ["idle-unload", "heartbeat", "duration-observe", "result-observe", "complete", "execute", "fail", "cancel", "schedule", "heartbeat-loop"];
            string[] failures = ["IOException", "InvalidDataException", "UnauthorizedAccessException", "InvalidOperationException", "OperationCanceledException", "TimeoutException", "HttpRequestException", "BackendUnavailableException", "JsonException", "OutOfMemoryException"];
            foreach (var name in new[] { "diagnostics.1.jsonl", "diagnostics.jsonl" })
            {
                try
                {
                    var lines = new UTF8Encoding(false, true).GetString(ReadBytes(Path.Combine(_root, name))).Split('\n');
                    foreach (var line in lines)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            if (line.Length > 16384) throw new DiagnosticLimitException();
                            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
                            var value = json.RootElement;
                            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                            var operation = Text(value, "operation");
                            var failure = Text(value, "failure")?.Split('.').Last();
                            var key = (operations.Contains(operation) ? operation! : "Other", failures.Contains(failure) ? failure! : "Other");
                            var previous = groups.GetValueOrDefault(key);
                            var at = Timestamp(value, "atUtc");
                            groups[key] = (previous.Count + 1, previous.Latest is null || at > previous.Latest ? at : previous.Latest);
                            source.Read++;
                        }
                        catch (Exception error) when (Recoverable(error)) { source.Omit(Reason(error)); }
                    }
                }
                catch (Exception error) when (Recoverable(error)) { source.Omit(Reason(error)); }
            }
            return new { source, groups = groups.OrderBy(pair => pair.Key.Operation).ThenBy(pair => pair.Key.Failure)
                .Select(pair => new { operation = pair.Key.Operation, failure = pair.Key.Failure, count = pair.Value.Count, latestAtUtc = pair.Value.Latest }).ToArray() };
        }

        internal object Metrics()
        {
            var source = new Coverage();
            var outcomes = Enum.GetNames<UsageTechnicalOutcome>().ToDictionary(name => name, _ => 0);
            var input = new Metric(); var output = new Metric(); var elapsed = new Metric();
            var calls = new HashSet<Guid>();
            foreach (var path in Children(Path.Combine("telemetry", "usage"), false, MaximumReports, source))
            {
                try
                {
                    var report = UsageReportJson.Read(new UTF8Encoding(false, true).GetString(ReadBytes(path, 131072)));
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var identity) || identity != report.Identity.CallId || !calls.Add(identity))
                        throw new InvalidDataException();
                    outcomes[report.Outcome.Technical.ToString()]++;
                    input.Add(report.CloudTokens[UsageCloudTokens.InputSaved]);
                    output.Add(report.CloudTokens[UsageCloudTokens.OutputSaved]);
                    elapsed.Add(report.Timings[UsageTiming.ElapsedMilliseconds]);
                    source.Read++;
                }
                catch (Exception error) when (Recoverable(error)) { source.Omit(Reason(error)); }
            }
            return new { source, scope = "bounded_unordered_retained_surface_reports", aggregation = "counts_and_extrema_only_no_additive_savings_total", outcomes,
                cloudInputSavedTokens = input, cloudOutputSavedTokens = output, elapsedMilliseconds = elapsed };
        }
    }

    private sealed class Metric
    {
        public Dictionary<string, MetricBucket> ByStatus { get; } = Enum.GetNames<UsageValueStatus>().ToDictionary(name => name, _ => new MetricBucket());
        internal void Add(UsageValue value)
        {
            var bucket = ByStatus[value.Status.ToString()];
            bucket.Samples++;
            if (value.Value is { } number)
            {
                bucket.Minimum = bucket.Minimum is null ? number : Math.Min(bucket.Minimum.Value, number);
                bucket.Maximum = bucket.Maximum is null ? number : Math.Max(bucket.Maximum.Value, number);
            }
        }
    }
    private sealed class MetricBucket
    {
        public int Samples { get; set; }
        public long? Minimum { get; set; }
        public long? Maximum { get; set; }
    }
    private sealed class DiagnosticLimitException : IOException;
    private sealed class DiagnosticLinkedSourceException : IOException;
}

internal sealed class DiagnosticOutputExistsException : IOException;
