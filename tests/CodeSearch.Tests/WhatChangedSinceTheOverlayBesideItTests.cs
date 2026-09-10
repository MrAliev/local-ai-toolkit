using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// What a semantic rebuild has to recompute, measured against the overlay written beside it
/// rather than against the base generation.
///
/// The corpus layer has done this since #388: it takes the newest index in the directory it is
/// about to write into and reuses every file whose content still hashes the same. On a feature
/// branch 101 files ahead of the base, one saved file made it embed exactly one -- "reused from
/// the previous overlay: 133, to embed: 1".
///
/// The semantic layer writes into a directory of the same shape and never looks at the
/// neighbour, so the same save recomputed everything the branch had touched: 373 projects,
/// 296.9 s. This is the missing comparison.
/// </summary>
public sealed class WhatChangedSinceTheOverlayBesideItTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-semantic-reuse-" + Guid.NewGuid().ToString("N"));

    public WhatChangedSinceTheOverlayBesideItTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_file_whose_content_is_unchanged_needs_nothing()
    {
        Write("Kept.cs", "class Kept { }");
        var previous = IndexOf("Kept.cs");

        Assert.Empty(SemanticOverlayReuse.ChangedSince(previous, _root, ["Kept.cs"]));
    }

    [Fact]
    public void A_file_whose_content_moved_needs_recomputing()
    {
        Write("Edited.cs", "class Edited { }");
        var previous = IndexOf("Edited.cs");
        Write("Edited.cs", "class Edited { public int Added() => 1; }");

        Assert.Equal(["Edited.cs"], SemanticOverlayReuse.ChangedSince(previous, _root, ["Edited.cs"]));
    }

    [Fact]
    public void A_file_the_previous_overlay_never_saw_needs_recomputing()
    {
        Write("Known.cs", "class Known { }");
        var previous = IndexOf("Known.cs");
        Write("Fresh.cs", "class Fresh { }");

        Assert.Equal(
            ["Fresh.cs"],
            SemanticOverlayReuse.ChangedSince(previous, _root, ["Known.cs", "Fresh.cs"]));
    }

    /// <summary>
    /// Gone from disk. The recompute set has to carry it so the overlay can record the deletion;
    /// leaving it out would keep answering about a file that is not there.
    /// </summary>
    [Fact]
    public void A_file_that_is_gone_needs_recomputing()
    {
        Write("Doomed.cs", "class Doomed { }");
        var previous = IndexOf("Doomed.cs");
        File.Delete(Path.Combine(_root, "Doomed.cs"));

        Assert.Equal(["Doomed.cs"], SemanticOverlayReuse.ChangedSince(previous, _root, ["Doomed.cs"]));
    }

    /// <summary>
    /// The hash is taken over the text as Roslyn read it -- decoded, then re-encoded as UTF-8 --
    /// so a file that differs only by a byte-order mark is the same document. Comparing raw bytes
    /// would call every such file changed and recompute the repository for nothing.
    /// </summary>
    [Fact]
    public void A_byte_order_mark_is_not_a_change()
    {
        const string source = "class Marked { }";
        Write("Marked.cs", source);
        var previous = IndexOf("Marked.cs");
        File.WriteAllText(Path.Combine(_root, "Marked.cs"), source, new UTF8Encoding(true));

        Assert.Empty(SemanticOverlayReuse.ChangedSince(previous, _root, ["Marked.cs"]));
    }

    [Fact]
    public void The_newest_overlay_beside_the_one_being_written_is_the_one_reused()
    {
        var directory = Path.Combine(_root, "overlays");
        Directory.CreateDirectory(directory);
        var older = Path.Combine(directory, "older.sidx");
        var newer = Path.Combine(directory, "newer.sidx");
        File.WriteAllText(older, "older");
        File.WriteAllText(newer, "newer");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        Assert.Equal(
            newer,
            SemanticOverlayReuse.NewestBeside(Path.Combine(directory, "about-to-write.sidx")));
    }

    /// <summary>
    /// The file about to be written is not its own predecessor: a rebuild forced over an existing
    /// overlay would otherwise reuse the very thing it was asked to replace.
    /// </summary>
    [Fact]
    public void The_overlay_being_written_is_not_its_own_neighbour()
    {
        var directory = Path.Combine(_root, "overlays");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "target.sidx");
        File.WriteAllText(target, "target");

        Assert.Null(SemanticOverlayReuse.NewestBeside(target));
    }

    [Fact]
    public void An_empty_directory_has_no_neighbour()
    {
        var directory = Path.Combine(_root, "overlays");
        Directory.CreateDirectory(directory);

        Assert.Null(SemanticOverlayReuse.NewestBeside(Path.Combine(directory, "first.sidx")));
    }

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_root, relative), content, new UTF8Encoding(false));

    private SemanticIndex IndexOf(params string[] paths) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = paths.Select(path => new SemanticDocument
            {
                RelPath = path,
                Hash = SHA256.HashData(Encoding.UTF8.GetBytes(
                    File.ReadAllText(Path.Combine(_root, path)))),
            }).ToList(),
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
