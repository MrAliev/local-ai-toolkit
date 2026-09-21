using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Documentation;

public sealed record DocumentationUnitInput(DocumentationInput Content, string SourceHash, string ReferencesHash, string HistoryHash);
public sealed record DocumentationBuildInput(string WorkingRoot, string Snapshot, IReadOnlyList<DocumentationUnitInput> Units);
public sealed record DocumentationUnitResult(
    [property: JsonPropertyName("symbolId"), JsonRequired] string SymbolId,
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("reasons"), JsonRequired] string[] Reasons,
    [property: JsonPropertyName("reused"), JsonRequired] bool Reused,
    [property: JsonPropertyName("error"), JsonRequired] string? Error,
    [property: JsonPropertyName("previousKept"), JsonRequired] bool PreviousKept,
    [property: JsonPropertyName("artifactId"), JsonRequired] string? ArtifactId);
public sealed record DocumentationBuildReport(
    [property: JsonPropertyName("packagePath"), JsonRequired] string PackagePath,
    [property: JsonPropertyName("units"), JsonRequired] IReadOnlyList<DocumentationUnitResult> Units,
    [property: JsonPropertyName("elapsedSeconds"), JsonRequired] double ElapsedSeconds)
{
    [JsonPropertyName("reused")]
    public int Reused => Units.Count(u => u.Reused);
    [JsonPropertyName("failed")]
    public int Failed => Units.Count(u => u.Error is not null);
}
public sealed record DocumentationApplyReport(
    [property: JsonPropertyName("destination"), JsonRequired] string Destination,
    [property: JsonPropertyName("copied"), JsonRequired] int Copied,
    [property: JsonPropertyName("unchanged"), JsonRequired] int Unchanged,
    [property: JsonPropertyName("skipped"), JsonRequired] IReadOnlyList<string> Skipped);
public sealed record DocumentationBuildProgress(string SymbolId, string Name, int Index, int Total, string Phase);

public sealed class DocumentationPackStore(string runtimeRoot, string repositoryId, string? generatorVersion = null,
    DocumentationCacheVersions? cacheVersions = null, DocumentationHtmlMode htmlMode = DocumentationHtmlMode.Shared)
{
    private static readonly string[] FileNames = ["page.en.md", "page.ru.md", "page.en.html", "page.ru.html", "page.en.pdf", "page.ru.pdf", "diagram.svg", "diagram.png", "LICENSES.txt"];
    private string Root => Path.GetFullPath(Path.Combine(runtimeRoot, "repositories", repositoryId, "documentation"));
    private sealed record Artifact(string SymbolId, string SourceHash, string ReferencesHash, string HistoryHash, string Version, Dictionary<string, string> Files,
        string TextKey = "", string PresentationHash = "", string RendererVersion = "", Dictionary<string, string>? Resources = null);
    private sealed record TextCheckpoint(int Schema, string Key, string DraftHash, DocumentationDraft Draft);
    private sealed record Pack(string RepositoryId, string Snapshot, DocumentationBuildReport Report);

    public async Task<DocumentationBuildReport> BuildAsync(DocumentationBuildInput input,
        Func<DocumentationInput, CancellationToken, Task<DocumentationDraft>> generate,
        Action<DocumentationBuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateLocation(input.WorkingRoot);
        if (!Enum.IsDefined(htmlMode)) throw new ArgumentOutOfRangeException(nameof(htmlMode));
        Directory.CreateDirectory(Root);
        using var lease = AcquireLock();
        var timer = Stopwatch.StartNew();
        var versions = cacheVersions ?? DocumentationCacheVersions.ForRuntime(runtimeRoot);
        var rendererVersion = versions.Renderer + ":" + htmlMode;
        var textVersion = Hash(JsonSerializer.SerializeToUtf8Bytes(new { Schema = "documentation-text-1", generatorVersion, versions.Prompt, versions.Validator, versions.Route }));
        var results = new List<DocumentationUnitResult>();
        foreach (var unit in input.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Hash(Encoding.UTF8.GetBytes(unit.Content.SymbolId));
            var pointer = Path.Combine(Root, "latest", key + ".json");
            var previousId = Read<string>(pointer);
            var previous = ValidArtifact(previousId, unit.Content.SymbolId);
            var textKey = Hash(JsonSerializer.SerializeToUtf8Bytes(new { textVersion, unit.SourceHash, unit.ReferencesHash, unit.HistoryHash,
                unit.Content.SymbolId, unit.Content.Name, unit.Content.Facts, unit.Content.Nodes, unit.Content.Edges }));
            var presentationHash = Hash(JsonSerializer.SerializeToUtf8Bytes(unit.Content));
            var reasons = Reasons(unit, previous, textVersion, textKey, presentationHash, rendererVersion);
            if (reasons.Length == 0)
            {
                results.Add(new(unit.Content.SymbolId, unit.Content.Name, [], true, null, false, previousId));
                continue;
            }
            IReadOnlyDictionary<string, byte[]> files;
            try
            {
                void Progress(string phase) => progress?.Invoke(new(unit.Content.SymbolId, unit.Content.Name, results.Count + 1, input.Units.Count, phase));
                var draft = ReadText(textKey);
                if (draft is not null)
                {
                    Progress("validate");
                    if (DocumentationValidator.Validate(unit.Content, draft).Count != 0) draft = null;
                }
                if (draft is null)
                {
                    Progress("generate");
                    draft = await generate(unit.Content, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Progress("validate");
                    var errors = DocumentationValidator.Validate(unit.Content, draft);
                    if (errors.Count != 0) throw new DocumentationValidationException(errors);
                    if (cacheVersions is null && DocumentationCacheVersions.ForRuntime(runtimeRoot).Route != versions.Route)
                        throw new DocumentationValidationException(["documentation_route_changed"]);
                    WriteJson(Path.Combine(Root, "texts", textKey + ".json"), new TextCheckpoint(1, textKey, Hash(JsonSerializer.SerializeToUtf8Bytes(draft)), draft));
                }
                Progress("render");
                cancellationToken.ThrowIfCancellationRequested();
                files = DocumentationRenderer.RenderValidated(unit.Content, draft, htmlMode);
                if (htmlMode == DocumentationHtmlMode.Shared)
                    foreach (var (name, bytes) in DocumentationHtmlResources.Files)
                    {
                        var resourcePath = Path.Combine(Root, "artifacts", "_assets", name);
                        RejectLinks(resourcePath);
                        if (!File.Exists(resourcePath) || Hash(File.ReadAllBytes(resourcePath)) != Hash(bytes)) WriteBytes(resourcePath, bytes);
                    }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(new(unit.Content.SymbolId, unit.Content.Name, reasons, false, error.Message, previous is not null, previous is null ? null : previousId));
                continue;
            }
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Root, "artifacts", id);
            Directory.CreateDirectory(directory);
            foreach (var name in FileNames) WriteBytes(Path.Combine(directory, name), files[name]);
            WriteJson(Path.Combine(directory, "artifact.json"), new Artifact(unit.Content.SymbolId, unit.SourceHash, unit.ReferencesHash, unit.HistoryHash, textVersion,
                FileNames.ToDictionary(name => name, name => Hash(files[name]), StringComparer.Ordinal), textKey, presentationHash, rendererVersion,
                htmlMode == DocumentationHtmlMode.Shared ? DocumentationHtmlResources.Files.ToDictionary(pair => pair.Key, pair => Hash(pair.Value), StringComparer.Ordinal) : null));
            // Only a completely rendered artifact becomes a resumable checkpoint.
            WriteJson(pointer, id);
            results.Add(new(unit.Content.SymbolId, unit.Content.Name, reasons, false, null, false, id));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var packId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Root, "packs", packId);
        var report = new DocumentationBuildReport(path, results, timer.Elapsed.TotalSeconds);
        foreach (var language in new[] { "en", "ru" })
            WriteBytes(Path.Combine(path, "index." + language + ".html"), Encoding.UTF8.GetBytes(ReviewPage(report, language)));
        WriteJson(Path.Combine(path, "manifest.json"), new Pack(repositoryId, input.Snapshot, report));
        cancellationToken.ThrowIfCancellationRequested();
        WriteJson(Path.Combine(Root, "current.json"), packId);
        return report;
    }

    public DocumentationApplyReport Apply(string workingRoot, string currentSnapshot)
    {
        ValidateLocation(workingRoot);
        var id = Read<string>(Path.Combine(Root, "current.json"));
        var pack = IsId(id) ? Read<Pack>(Path.Combine(Root, "packs", id!, "manifest.json")) : null;
        if (pack is null || pack.RepositoryId != repositoryId) throw Failure("package_missing");
        if (pack.Snapshot != currentSnapshot) throw Failure("sources_changed");
        var destination = Path.GetFullPath(Path.Combine(workingRoot, "docs", "generated"));
        RejectLinks(destination);
        using var lease = AcquireLock();
        var baselinePath = Path.Combine(Root, "applied", Hash(Encoding.UTF8.GetBytes(Normalize(workingRoot))) + ".json");
        var baseline = Read<Dictionary<string, string>>(baselinePath) ?? new(StringComparer.Ordinal);
        var copied = 0; var unchanged = 0;
        var skipped = new List<string>();
        // Check every artifact before changing the destination.
        var artifacts = pack.Report.Units.Where(unit => unit.ArtifactId is not null)
            .Select(unit => (Unit: unit, Artifact: ValidArtifact(unit.ArtifactId, unit.SymbolId) ?? throw Failure("artifact_corrupt"))).ToArray();
        var resources = artifacts.SelectMany(item => item.Artifact.Resources ?? []).DistinctBy(pair => pair.Key).ToArray();
        foreach (var (name, _) in resources) RejectLinks(Path.Combine(destination, "_assets", name));
        foreach (var (name, hash) in resources)
            Copy("_assets/" + name, Path.Combine(Root, "artifacts", "_assets", name), hash);
        foreach (var (unit, artifact) in artifacts)
        {
            var key = Hash(Encoding.UTF8.GetBytes(unit.SymbolId));
            foreach (var name in FileNames)
                Copy(key + "/" + name, Path.Combine(Root, "artifacts", unit.ArtifactId!, name), artifact.Files[name]);
        }
        WriteJson(baselinePath, baseline);
        return new(destination, copied, unchanged, skipped);

        void Copy(string relative, string source, string desired)
        {
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            RejectLinks(target);
            var bytes = File.ReadAllBytes(source);
            if (Hash(bytes) != desired) throw Failure("artifact_corrupt");
            var existing = File.Exists(target) ? Hash(File.ReadAllBytes(target)) : null;
            if (existing == desired) { unchanged++; baseline[relative] = desired; }
            else if (existing is not null && (!baseline.TryGetValue(relative, out var prior) || prior != existing)) skipped.Add(target);
            else
            {
                WriteBytes(target, bytes);
                copied++;
                baseline[relative] = desired;
                // Persist each successful copy so an interrupted apply can be safely resumed.
                WriteJson(baselinePath, baseline);
            }
        }
    }

    private static string[] Reasons(DocumentationUnitInput unit, Artifact? previous, string textVersion, string textKey, string presentationHash, string rendererVersion)
    {
        if (previous is null) return ["new"];
        var result = new List<string>();
        if (previous.SourceHash != unit.SourceHash) result.Add("source");
        if (previous.ReferencesHash != unit.ReferencesHash) result.Add("references");
        if (previous.HistoryHash != unit.HistoryHash) result.Add("history");
        if (previous.Version != textVersion) result.Add("generator");
        if (previous.TextKey != textKey && result.Count == 0) result.Add("references");
        if (previous.PresentationHash != presentationHash) result.Add("presentation");
        if (previous.RendererVersion != rendererVersion) result.Add("renderer");
        return result.ToArray();
    }

    private DocumentationDraft? ReadText(string key)
    {
        var path = Path.Combine(Root, "texts", key + ".json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            var checkpoint = Read<TextCheckpoint>(path);
            return checkpoint is { Schema: 1, Draft: not null } && checkpoint.Key == key &&
                checkpoint.DraftHash == Hash(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Draft)) ? checkpoint.Draft : null;
        }
        catch (IOException) { return null; }
    }

    private Artifact? ValidArtifact(string? id, string symbolId)
    {
        if (!IsId(id)) return null;
        var directory = Path.Combine(Root, "artifacts", id!);
        var artifact = Read<Artifact>(Path.Combine(directory, "artifact.json"));
        if (artifact is null || artifact.SymbolId != symbolId || artifact.Files is null || artifact.Files.Count != FileNames.Length) return null;
        if (artifact.RendererVersion?.EndsWith(":" + DocumentationHtmlMode.Shared, StringComparison.Ordinal) == true && artifact.Resources is null) return null;
        try
        {
            foreach (var name in FileNames)
                if (!artifact.Files.TryGetValue(name, out var hash) || !File.Exists(Path.Combine(directory, name)) || Hash(File.ReadAllBytes(Path.Combine(directory, name))) != hash) return null;
            if (artifact.Resources is not null)
            {
                if (artifact.Resources.Count != 1) return null;
                foreach (var (name, hash) in artifact.Resources)
                {
                    if (!DocumentationHtmlResources.ValidName(name, hash)) return null;
                    var resource = Path.Combine(Root, "artifacts", "_assets", name);
                    RejectLinks(resource);
                    if (!File.Exists(resource) || Hash(File.ReadAllBytes(resource)) != hash) return null;
                }
            }
            return artifact;
        }
        catch (IOException) { return null; }
    }

    private void ValidateLocation(string workingRoot)
    {
        if (repositoryId.Length != 64 || repositoryId.Any(c => !Uri.IsHexDigit(c))) throw Failure("invalid_repository_id");
        var repository = Normalize(workingRoot);
        var storage = Normalize(Root);
        if (storage == repository || storage.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw Failure("runtime_inside_repository", Root);
        RejectLinks(Root);
    }

    private FileStream AcquireLock()
    {
        try { return new FileStream(Path.Combine(Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw Failure("build_already_running"); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Failure("destination_link", current);
    }

    private static string Normalize(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    private static bool IsId(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static DocumentationValidationException Failure(string code, string? path = null)
    {
        var error = new DocumentationValidationException([code]);
        if (path is not null) error.Data["path"] = path;
        return error;
    }
    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception error) when (error is IOException or JsonException) { return default; }
    }
    private static void WriteJson<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string ReviewPage(DocumentationBuildReport report, string language)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        string T(string key) => DocumentationText.Get(key, language);
        var changes = report.Units.Where(unit => !unit.Reused && unit.Error is null).Select(unit => "<li><h3>" + E(unit.Name) + "</h3><p>" +
            E(string.Join(" ", unit.Reasons.Select(reason => T("Reason_" + reason)))) + "</p></li>").ToArray();
        var failures = report.Units.Where(unit => unit.Error is not null).Select(unit => "<li><h3>" + E(unit.Name) + "</h3><p>" + E(unit.Error!) + " — " +
            E(T(unit.PreviousKept ? "PreviousKept" : "NoPrevious")) + "</p></li>").ToArray();
        var contents = report.Units.Where(unit => unit.ArtifactId is not null).Select(unit => "<li>" + E(unit.Name) + " — " +
            string.Join(" · ", new[] { "html", "pdf", "md" }.Select(format => "<a href=\"../../artifacts/" + unit.ArtifactId + "/page." + language + "." + format + "\">" + format.ToUpperInvariant() + "</a>")) +
            (unit.PreviousKept ? " — " + E(T("PreviousKept")) : "") + "</li>").ToArray();
        string Section(string title, string[] rows, string empty) => "<h2>" + E(T(title)) + "</h2>" + (rows.Length == 0 ? "<p>" + E(T(empty)) + "</p>" : "<ul>" + string.Join("", rows) + "</ul>");
        var sections = report.Units.Count == 0 ? "<p>" + E(T("EmptySelection")) + "</p>" :
            Section("ChangesTitle", changes, "NoChanges") + (failures.Length == 0 ? "" : Section("FailuresTitle", failures, "")) + Section("ContentsTitle", contents, "EmptyContents");
        return "<!doctype html><html lang=\"" + language + "\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + E(T("ReportTitle")) + "</title>" +
            "<style>body{font:16px/1.6 system-ui;max-width:960px;margin:48px auto;padding:0 24px;color:#13233a;overflow-wrap:anywhere}a{color:#164b94}li{margin:24px 0}h1,h2{line-height:1.2}</style><body><a href=\"index." + (language == "en" ? "ru" : "en") + ".html\">" + (language == "en" ? "Русская версия" : "English version") + "</a><h1>" + E(T("ReportTitle")) + "</h1><p>" + E(T("ReviewNotice")) + "</p><p>" +
            E(string.Format(CultureInfo.InvariantCulture, T("ReportCounts"), report.Units.Count, report.Reused, report.Failed, report.ElapsedSeconds.ToString("F1", CultureInfo.InvariantCulture))) + "</p>" + sections + "</body></html>";
    }
}
