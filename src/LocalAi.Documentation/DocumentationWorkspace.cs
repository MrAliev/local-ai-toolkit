using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;

namespace LocalAi.Documentation;

public static class DocumentationWorkspace
{
    public static string Snapshot(WorkingIndexIdentity identity) => identity.HeadCommit + ":" + identity.HeadTree +
        (identity.SemanticDirtyHash is null ? "" : ":" + identity.SemanticDirtyHash);

    public static DocumentationBuildInput Capture(string workingRoot, string runtimeRoot, int limit = 5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot);
        SemanticIndex index;
        try
        {
            var store = new GenerationStore(identity.RepositoryRuntimeRoot);
            var current = store.ReadCurrent() ?? throw new InvalidDataException();
            var manifest = store.ReadManifest(current.GenerationId);
            var baseline = SemanticIndex.Load(store.SemanticIndexPath(current.GenerationId));
            index = identity.HeadTree == manifest.Identity.DevTree && identity.SemanticDirtyHash is null ? baseline :
                SemanticIndexOverlay.Load(RuntimeIndexLayout.SemanticOverlayPath(identity, current.GenerationId)).Materialize(baseline);
            if (index.RepositoryId != identity.RepositoryId || index.GenerationId != current.GenerationId ||
                index.GitTree != identity.HeadTree || index.DirtyHash != identity.SemanticDirtyHash) throw new InvalidDataException();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            throw new DocumentationValidationException(["index_outdated"]);
        }
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in index.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(identity.WorkingRoot.Value, document.RelPath));
            var relative = Path.GetRelativePath(identity.WorkingRoot.Value, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new DocumentationValidationException(["invalid_source_path"]);
            try
            {
                var text = File.ReadAllText(path);
                if (!SHA256.HashData(Encoding.UTF8.GetBytes(text)).SequenceEqual(document.Hash)) throw new DocumentationValidationException(["index_outdated"]);
                sources.Add(document.RelPath, text);
            }
            catch (IOException) { throw new DocumentationValidationException(["index_outdated"]); }
        }
        var input = new DocumentationBuildInput(identity.WorkingRoot.Value, Snapshot(identity), DocumentationInputs.Create(index, sources,
            paths => History(identity.WorkingRoot.Value, identity.HeadCommit, paths, cancellationToken), limit));
        if (Snapshot(RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot)) != input.Snapshot) throw new DocumentationValidationException(["index_outdated"]);
        return input;
    }

    private static IReadOnlyList<DocumentationHistory> History(string root, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "log", "-10", "--no-show-signature", "--format=%H%x00%B%x00", commit, "--" }.Concat(paths.Select(path => ":(literal)" + path))) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new DocumentationValidationException(["history_unavailable"]);
        var fields = output.Result.Split('\0');
        var history = new List<DocumentationHistory>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            if (fields[i].Trim().Length > 0) history.Add(new(fields[i].Trim(), fields[i + 1].TrimEnd()));
        return history;
    }
}
