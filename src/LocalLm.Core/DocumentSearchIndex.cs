using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

public static class DocumentSearchIndex
{
    private const int MaximumDocuments = 32, MaximumPages = 128, MaximumText = 8_000_000;
    private const long MaximumPdfBytes = 256L * 1024 * 1024, MaximumIndexBytes = 64L * 1024 * 1024;
    private const string Prompt = "Transcribe all visible text exactly in its original language. Preserve reading order. Do not summarize or paraphrase.";
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task<DocumentIndexSummary> RefreshAsync(LocalTasks tasks, string sourceDirectory,
        string indexDirectory, string? model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = FullDirectory(sourceDirectory);
        var index = FullDirectory(indexDirectory);
        if (!Directory.Exists(source)) throw Error("source_missing", "DocumentsSourceFolderMissing", source);
        var relative = Path.GetRelativePath(source, index);
        if (Paths.Equals(source, index) ||
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw Error("invalid_path", "DocumentsIndexOutsideSource");
        RejectLink(source);
        Directory.CreateDirectory(index);
        await using var lease = Acquire(index);
        var previous = await ReadAsync(index, required: false, cancellationToken);
        if (previous is not null && !Paths.Equals(previous.SourceDirectory, source))
            throw Error("source_mismatch", "DocumentsIndexSourceMismatch", previous.SourceDirectory);
        var files = Enumerate(source);
        if (files.Sum(path => new FileInfo(path).Length) > 1024L * 1024 * 1024)
            throw Error("source_limit", "DocumentsTotalSizeExceeded", 1);
        // Preflight the entire set before spending any model work.
        foreach (var path in files)
        {
            RejectLink(path);
            await using var pdf = await PdfPageReader.OpenAsync(path, cancellationToken);
            if (pdf.TotalPageCount > MaximumPages)
                throw Error("page_limit", "DocumentsPageCountExceeded", path, pdf.TotalPageCount, MaximumPages);
        }
        var processor = typeof(LocalTasks).Module.ModuleVersionId.ToString("N") + ":" +
            new ModelRoutingCatalogStore().Read().CatalogVersion;
        var entries = new List<Entry>();
        var reused = 0;
        long textSize = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await HashAsync(path, cancellationToken);
            var name = Path.GetFileName(path);
            var old = previous?.Documents.SingleOrDefault(entry => Paths.Equals(entry.Name, name));
            Entry entry;
            if (old?.Sha256 == hash && previous!.Processor == processor && previous.Model == model)
            {
                entry = old;
                reused++;
                UsageCall.Current?.CacheHit("document_ocr_index");
            }
            else
            {
                var result = await tasks.ReadImageAsync([path], Prompt, LocalTaskProfile.Ocr, model, false, cancellationToken);
                var evidence = result.PdfEvidence!;
                if (evidence.Sha256 != hash) throw Error("source_changed", "DocumentsSourceChanged", path);
                entry = new Entry(name, hash, evidence.Pages.Select(page => new Page(page.Number, page.Text,
                    PdfOcrCheckpoint.Hash(page.Text), page.Model)).ToArray());
            }
            textSize += entry.Pages.Sum(page => (long)page.Text.Length);
            if (textSize > MaximumText || entry.Pages.Any(page => page.Text.Length > 64_000))
                throw Error("text_limit", "DocumentsTextLimitExceeded");
            entries.Add(entry);
        }
        // An update publishes one complete snapshot, never a mixture of old and new pages.
        if (!files.SequenceEqual(Enumerate(source), Paths)) throw Error("source_changed", "DocumentsSourceChanged", source);
        foreach (var entry in entries)
            if (await HashAsync(Path.Combine(source, entry.Name), cancellationToken) != entry.Sha256)
                throw Error("source_changed", "DocumentsSourceChanged", Path.Combine(source, entry.Name));
        var snapshot = new Snapshot(1, source, processor, model, entries.ToArray());
        await AtomicFile.WriteAsync(Path.Combine(index, "snapshot.json"), async (stream, token) =>
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, token);
            if (stream.Length > MaximumIndexBytes) throw Error("text_limit", "DocumentsTextLimitExceeded");
        }, AtomicWrite.Irrecoverable, cancellationToken);
        if (reused == files.Length) UsageCall.Current?.NoInference();
        return new(1, source, entries.Count, entries.Sum(entry => entry.Pages.Length), reused,
            previous?.Documents.Count(entry => !entries.Any(current => Paths.Equals(current.Name, entry.Name))) ?? 0);
    }

    public static async Task<DocumentSearchResult> SearchAsync(string indexDirectory, string query, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256) throw Error("invalid_query", "DocumentsQueryInvalid");
        if (limit is < 1 or > 100) throw Error("invalid_limit", "DocumentsLimitInvalid");
        var snapshot = (await ReadAsync(FullDirectory(indexDirectory), required: true, cancellationToken))!;
        var hits = new List<DocumentSearchHit>();
        var excluded = new List<DocumentExcludedSource>();
        var truncated = false;
        foreach (var entry in snapshot.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(snapshot.SourceDirectory, entry.Name);
            string? reason = null;
            try
            {
                RejectLink(snapshot.SourceDirectory);
                RejectLink(path);
                if (await HashAsync(path, cancellationToken) != entry.Sha256) reason = "changed";
            }
            catch (FileNotFoundException) { reason = "deleted"; }
            catch (DirectoryNotFoundException) { reason = "deleted"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DocumentIndexException)
            { reason = "unreadable"; }
            if (reason is not null) { excluded.Add(new(path, reason)); continue; }
            foreach (var page in entry.Pages)
            {
                var offset = page.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (offset < 0) continue;
                if (hits.Count == limit) { truncated = true; continue; }
                var start = Math.Max(0, offset - 120);
                if (start > 0 && char.IsLowSurrogate(page.Text[start])) start--;
                var length = Math.Min(2000, page.Text.Length - start);
                if (start + length < page.Text.Length && char.IsHighSurrogate(page.Text[start + length - 1])) length--;
                var id = PdfOcrCheckpoint.Hash(path + "\0" + entry.Sha256 + "\0" + page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + page.TextHash);
                hits.Add(new(id, path, entry.Sha256, page.Number, new("page", "normalized", 0, 0, 1, 1),
                    "ocr", page.Text.Substring(start, length), page.Model, offset, query.Length, start));
                // Compare only delivered pages with their excerpts, not the entire corpus scanned.
                UsageCall.Current?.Comparison.SourceText(page.Text, path + ":" + entry.Sha256 + ":" + page.Number);
            }
        }
        UsageCall.Current?.NoInference();
        return new(1, "literal-ocr-phrase", hits.ToArray(), excluded.ToArray(), truncated);
    }

    private static string FullDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Error("invalid_path", "DocumentsPathInvalid", path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error("invalid_path", "DocumentsPathInvalid", path);
    }
    private static string[] Enumerate(string source)
    {
        var files = Directory.EnumerateFiles(source).Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Take(MaximumDocuments + 1).Order(Paths).ToArray();
        if (files.Length > MaximumDocuments) throw Error("source_limit", "DocumentsFileCountExceeded", files.Length, MaximumDocuments);
        return files;
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumPdfBytes) throw Error("source_limit", "DocumentsFileSizeExceeded", path, 256);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
    }
    private static FileStream Acquire(string index)
    {
        try { return new FileStream(Path.Combine(index, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw Error("index_busy", "DocumentsIndexUnavailable", index); }
    }
    private static async Task<Snapshot?> ReadAsync(string index, bool required, CancellationToken cancellationToken)
    {
        var path = Path.Combine(index, "snapshot.json");
        if (!File.Exists(path))
        {
            if (required) throw Error("index_missing", "DocumentsIndexMissing", index);
            return null;
        }
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > MaximumIndexBytes) throw new JsonException();
            var value = await JsonSerializer.DeserializeAsync<Snapshot>(input, JsonOptions, cancellationToken) ?? throw new JsonException();
            if (value.SchemaVersion != 1) throw Error("index_schema", "DocumentsIndexSchemaUnsupported", index);
            if (value.SourceDirectory is null || !Path.IsPathFullyQualified(value.SourceDirectory) || value.Processor is null ||
                value.Documents is null || value.Documents.Length > MaximumDocuments) throw new JsonException();
            long size = 0;
            var names = new HashSet<string>(Paths);
            foreach (var entry in value.Documents)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Name != Path.GetFileName(entry.Name) ||
                    !entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !names.Add(entry.Name) || !IsHash(entry.Sha256) ||
                    entry.Pages is null || entry.Pages.Length is < 1 or > MaximumPages) throw new JsonException();
                for (var i = 0; i < entry.Pages.Length; i++)
                {
                    var page = entry.Pages[i];
                    if (page is null || page.Number != i + 1 || page.Text is null || page.Text.Length > 64_000 ||
                        string.IsNullOrWhiteSpace(page.Model) || page.TextHash != PdfOcrCheckpoint.Hash(page.Text)) throw new JsonException();
                    size += page.Text.Length;
                }
            }
            if (size > MaximumText) throw new JsonException();
            return value;
        }
        catch (JsonException) { throw Error("index_corrupt", "DocumentsIndexCorrupt", index); }
    }
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static DocumentIndexException Error(string code, string key, params object[] values) =>
        new(code, LocalLmText.Catalogue.Format(key, values));
    private sealed record Snapshot(int SchemaVersion, string SourceDirectory, string Processor, string? Model, Entry[] Documents);
    private sealed record Entry(string Name, string Sha256, Page[] Pages);
    private sealed record Page(int Number, string Text, string TextHash, string Model);
}

public sealed record DocumentIndexSummary(int SchemaVersion, string SourceDirectory, int Documents, int Pages, int ReusedDocuments, int RemovedDocuments);
public sealed record DocumentSearchResult(int SchemaVersion, string SearchKind, DocumentSearchHit[] Hits, DocumentExcludedSource[] ExcludedSources, bool Truncated)
{
    public string TokenComparisonSource => "returned-ocr-pages";
}
public sealed record DocumentExcludedSource(string Path, string Reason);
public sealed record DocumentSearchHit(string Id, string Path, string Sha256, int Page, DocumentRegion Region,
    string TextKind, string Text, string Model, int OcrOffset, int MatchLength, int TextOffset)
{
    public string TextVerification => "unverified";
}
public sealed record DocumentRegion(string Kind, string Units, int X, int Y, int Width, int Height);
public sealed class DocumentIndexException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
