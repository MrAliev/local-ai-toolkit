using System.Security.Cryptography;
using System.Text;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// What a semantic rebuild has to recompute when a file is saved, measured against the overlay of
/// the commit the save was made against.
///
/// The corpus layer has done this since #388: it takes the index already written in the directory
/// it is about to write into and reuses every file whose content still hashes the same. Measured
/// on a feature branch 101 files ahead of the base, one saved file made it embed exactly one --
/// "reused from the previous overlay: 133, to embed: 1".
///
/// The semantic layer writes into a directory of the same shape and never looked at it, so it
/// recomputed everything the branch had touched rather than everything the save had touched: 373
/// projects opened and 296.9 s for a one-line comment, against 314.4 s before any of this was
/// narrowed at all (#519).
/// </summary>
public static class SemanticOverlayReuse
{
    /// <summary>
    /// The committed overlay sitting beside the one about to be written, or nothing when this
    /// commit has not been indexed clean yet. The directory already fixes the generation, the
    /// worktree and the head tree; only the dirty hash varies within it, which is exactly the
    /// axis a save moves along.
    ///
    /// The committed one and never merely the newest. A rebuild has to tell an edit that changed
    /// a declaration from one that changed a body, and that needs the earlier text of the saved
    /// file: git has the committed version of it, while whatever another dirty overlay was built
    /// from was overwritten the moment the file was saved again. Layering on that would carry
    /// across records that were resolved against declarations nothing can still read.
    ///
    /// The committed overlay is not its own predecessor either: a rebuild of the commit with
    /// nothing saved would otherwise reuse the very thing it was asked to replace.
    /// </summary>
    public static string? CommittedBeside(string overlayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        var target = Path.GetFullPath(overlayPath);
        var directory = Path.GetDirectoryName(target);
        if (directory is null)
        {
            return null;
        }

        var committed = Path.Combine(directory, CommittedOverlayName);
        return !string.Equals(committed, target, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(committed)
            ? committed
            : null;
    }

    /// <summary>
    /// Which of the candidates the layers no longer describe: content that moved, a file none of
    /// them ever saw, and a file that has since gone -- the last one because the overlay has to
    /// record the deletion, and leaving it out would go on answering about a file that is not
    /// there.
    /// </summary>
    /// <param name="layers">
    /// Nearest first: the committed overlay's own documents, then the base generation. A file the
    /// branch never committed is described only by the base, and one the branch changed is
    /// described by both -- where they disagree the nearer layer is the one that stands, or every
    /// committed change would be recomputed on every save.
    /// </param>
    public static IReadOnlyList<string> ChangedSince(
        IReadOnlyList<SemanticIndex> layers,
        string workingRoot,
        IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        var root = Path.GetFullPath(workingRoot).TrimEnd(Path.DirectorySeparatorChar);
        var known = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers.Reverse())
        {
            foreach (var document in layer.Documents)
            {
                known[Normalize(document.RelPath)] = document.Hash;
            }
        }

        var changed = new List<string>();
        foreach (var candidate in candidates)
        {
            var relative = Normalize(candidate);
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
    /// The name a semantic overlay of a commit with nothing saved on top of it is written under,
    /// which is what makes it findable from beside it. It is
    /// <c>RuntimeIndexLayout.SemanticOverlayPath</c> that chooses it; a test pins the two
    /// together.
    /// </summary>
    public const string CommittedOverlayName = "clean.sidx";

    private static string Normalize(string path) => path.Replace('\\', '/');

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
