using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>Publish a translation without overwriting its input or its resumable session.</summary>
public static class TranslationFileOutput
{
    public static void Validate(string? source, string target, string? checkpointDirectory)
    {
        target = Path.GetFullPath(target);
        if (source is not null && OutputFileIdentity.IsSource(Path.GetFullPath(source), target))
            throw new ArgumentException(LocalLmText.TranslateOutputIsSource("input", "output", Path.GetFullPath(source)));
        if (checkpointDirectory is null) return;
        var checkpoint = Path.GetFullPath(checkpointDirectory);
        var relative = Path.GetRelativePath(ResolveAliases(checkpoint), ResolveAliases(target));
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new ArgumentException(LocalLmText.TranslationCheckpointOutputConflict(checkpoint));
        if (!Directory.Exists(checkpoint)) return;
        foreach (var file in Directory.EnumerateFiles(checkpoint))
        {
            var name = Path.GetFileName(file);
            if ((name is "session.json" or "session.lock" || name.StartsWith("segment-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)) &&
                OutputFileIdentity.IsSource(file, target))
                throw new ArgumentException(LocalLmText.TranslationCheckpointOutputConflict(checkpoint));
        }
    }

    public static async Task WriteAsync(string target, string text, string? source, string? checkpointDirectory,
        CancellationToken cancellationToken)
    {
        try { Validate(source, target, checkpointDirectory); }
        catch (ArgumentException error) { throw new IOException(error.Message, error); }
        await AtomicFile.WriteAsync(Path.GetFullPath(target), async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteAsync(text.AsMemory(), token);
            await writer.FlushAsync(token);
            token.ThrowIfCancellationRequested();
        }, AtomicWrite.Irrecoverable, cancellationToken);
    }

    private static string ResolveAliases(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.Exists && item.LinkTarget is not null) current = item.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }
}
