using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// What a semantic rebuild has to recompute, measured against the overlay written beside it.
///
/// The corpus layer has done this since #388: it takes the newest index in the directory it is
/// about to write into and reuses every file whose content still hashes the same. Measured on a
/// feature branch 101 files ahead of the base, one saved file made it embed exactly one --
/// "reused from the previous overlay: 133, to embed: 1".
///
/// The semantic layer writes into a directory of the same shape and never looked at the
/// neighbour, so it recomputed everything the branch had touched rather than everything the save
/// had touched: 373 projects opened and 296.9 s for a one-line comment, against 314.4 s before
/// any of this was narrowed at all (#519).
/// </summary>
public static class SemanticOverlayReuse
{
    /// <summary>
    /// The newest semantic overlay sitting beside the one about to be written, or nothing when
    /// this is the first. The directory already fixes the generation, the worktree and the head
    /// tree; only the dirty hash varies within it, which is exactly the axis a save moves along.
    ///
    /// The file about to be written is never its own predecessor: a forced rebuild would
    /// otherwise reuse the very thing it was asked to replace.
    /// </summary>
    public static string? NewestBeside(string overlayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        var target = Path.GetFullPath(overlayPath);
        var directory = Path.GetDirectoryName(target);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*.sidx")
                .Where(file => !string.Equals(
                    file.FullName,
                    target,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which of the candidates the previous index no longer describes: content that moved, a file
    /// it never saw, and a file that has since gone -- the last one because the overlay has to
    /// record the deletion, and leaving it out would go on answering about a file that is not
    /// there.
    /// </summary>
    public static IReadOnlyList<string> ChangedSince(
        SemanticIndex previous,
        string workingRoot,
        IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        var root = Path.GetFullPath(workingRoot).TrimEnd(Path.DirectorySeparatorChar);
        var known = previous.Documents.ToDictionary(
            document => document.RelPath.Replace((char)92, '/'),
            document => document.Hash,
            StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        foreach (var candidate in candidates)
        {
            var relative = candidate.Replace((char)92, '/');
            if (!known.TryGetValue(relative, out var before))
            {
                changed.Add(candidate);
                continue;
            }

            var now = HashOf(root, relative);
            if (now is null || !before.AsSpan().SequenceEqual(now))
            {
                changed.Add(candidate);
            }
        }

        return changed;
    }

    /// <summary>
    /// The hash the indexer writes: taken over the text as Roslyn read it -- decoded from the
    /// file, then re-encoded as UTF-8 -- rather than over the bytes on disk. A byte-order mark is
    /// therefore not a change, and comparing raw bytes would call every such file changed and
    /// recompute the repository for nothing.
    ///
    /// A file this cannot read is reported as absent, which puts it in the recompute set: the one
    /// direction that costs time rather than correctness.
    /// </summary>
    private static byte[]? HashOf(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
        {
            return null;
        }

        try
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(full)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
