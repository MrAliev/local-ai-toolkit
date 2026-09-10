using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// What a semantic rebuild has to recompute when it layers a save on the overlay of the commit
/// it was made against, and how it finds that overlay.
///
/// The corpus layer has reused its neighbour since #388: on a feature branch 101 files ahead of
/// the base, one saved file made it embed exactly one -- "reused from the previous overlay: 133,
/// to embed: 1". The semantic layer never looked, so the same save recomputed everything the
/// branch had touched: 373 projects, 296.9 s (#519).
///
/// The neighbour it looks for is the committed one, `clean.sidx`, and not whichever is newest.
/// A rebuild has to tell an API edit from a body edit, which needs the earlier text of the saved
/// file; git keeps the committed version, and whatever another dirty overlay was built from has
/// been overwritten on disk.
/// </summary>
public sealed class WhatASaveChangedSinceTheCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-semantic-reuse-" + Guid.NewGuid().ToString("N"));

    public WhatASaveChangedSinceTheCommitTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_file_whose_content_is_unchanged_needs_nothing()
    {
        Write("Kept.cs", "class Kept { }");

        Assert.Empty(SemanticOverlayReuse.ChangedSince([IndexOf("Kept.cs")], _root, ["Kept.cs"]));
    }

    [Fact]
    public void A_file_whose_content_moved_needs_recomputing()
    {
        Write("Edited.cs", "class Edited { }");
        var committed = IndexOf("Edited.cs");
        Write("Edited.cs", "class Edited { public int Added() => 1; }");

        Assert.Equal(
            ["Edited.cs"],
            SemanticOverlayReuse.ChangedSince([committed], _root, ["Edited.cs"]));
    }

    [Fact]
    public void A_file_no_layer_ever_saw_needs_recomputing()
    {
        Write("Known.cs", "class Known { }");
        var committed = IndexOf("Known.cs");
        Write("Fresh.cs", "class Fresh { }");

        Assert.Equal(
            ["Fresh.cs"],
            SemanticOverlayReuse.ChangedSince([committed], _root, ["Known.cs", "Fresh.cs"]));
    }

    /// <summary>
    /// Gone from disk. The recompute set has to carry it so the overlay can record the deletion;
    /// leaving it out would keep answering about a file that is not there.
    /// </summary>
    [Fact]
    public void A_file_that_is_gone_needs_recomputing()
    {
        Write("Doomed.cs", "class Doomed { }");
        var committed = IndexOf("Doomed.cs");
        File.Delete(Path.Combine(_root, "Doomed.cs"));

        Assert.Equal(
            ["Doomed.cs"],
            SemanticOverlayReuse.ChangedSince([committed], _root, ["Doomed.cs"]));
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
        var committed = IndexOf("Marked.cs");
        File.WriteAllText(Path.Combine(_root, "Marked.cs"), source, new UTF8Encoding(true));

        Assert.Empty(SemanticOverlayReuse.ChangedSince([committed], _root, ["Marked.cs"]));
    }

    /// <summary>
    /// A saved file the branch never committed is described by the base generation, not by the
    /// committed overlay, so the layers are consulted in turn rather than only the nearest.
    /// </summary>
    [Fact]
    public void A_file_only_the_base_describes_is_measured_against_the_base()
    {
        Write("Untouched.cs", "class Untouched { }");
        Write("Branch.cs", "class Branch { }");
        var @base = IndexOf("Untouched.cs");
        var committed = IndexOf("Branch.cs");

        Assert.Empty(SemanticOverlayReuse.ChangedSince(
            [committed, @base],
            _root,
            ["Untouched.cs", "Branch.cs"]));
    }

    /// <summary>
    /// Where both describe a file, the nearer layer is the one that stands: the commit changed it
    /// after the base was built, and measuring against the base would recompute it every time.
    /// </summary>
    [Fact]
    public void The_nearer_layer_answers_for_a_file_both_describe()
    {
        Write("Branch.cs", "class Branch { }");
        var stale = IndexOf("Branch.cs");
        Write("Branch.cs", "class Branch { public int Added() => 1; }");
        var committed = IndexOf("Branch.cs");

        Assert.Empty(SemanticOverlayReuse.ChangedSince([committed, stale], _root, ["Branch.cs"]));
    }

    [Fact]
    public void The_committed_overlay_beside_it_is_the_one_layered_on()
    {
        var directory = OverlayDirectory();
        var committed = Path.Combine(directory, "clean.sidx");
        File.WriteAllText(committed, "committed");
        File.WriteAllText(Path.Combine(directory, "another-save.sidx"), "dirty");

        Assert.Equal(
            committed,
            SemanticOverlayReuse.CommittedBeside(Path.Combine(directory, "this-save.sidx")));
    }

    /// <summary>
    /// The committed overlay is not layered on itself: a rebuild of the commit with nothing
    /// saved would otherwise reuse the very thing it was asked to replace.
    /// </summary>
    [Fact]
    public void The_committed_overlay_is_not_its_own_predecessor()
    {
        var directory = OverlayDirectory();
        var committed = Path.Combine(directory, "clean.sidx");
        File.WriteAllText(committed, "committed");

        Assert.Null(SemanticOverlayReuse.CommittedBeside(committed));
    }

    /// <summary>
    /// The behaviour that separates this from "reuse the newest": a directory holding somebody
    /// else's save and no committed overlay has nothing that can be layered on, and says so
    /// rather than offering the save.
    /// </summary>
    [Fact]
    public void A_save_beside_it_is_not_offered_in_place_of_the_commit()
    {
        var directory = OverlayDirectory();
        File.WriteAllText(Path.Combine(directory, "another-save.sidx"), "dirty");

        Assert.Null(SemanticOverlayReuse.CommittedBeside(Path.Combine(directory, "this-save.sidx")));
    }

    [Fact]
    public void A_commit_indexed_for_the_first_time_has_nothing_beside_it()
    {
        var directory = OverlayDirectory();

        Assert.Null(SemanticOverlayReuse.CommittedBeside(Path.Combine(directory, "first.sidx")));
    }

    /// <summary>
    /// The name is chosen where the overlay is written and looked for where it is reused. One
    /// literal in two places is how the two come apart -- silently, since a rebuild that finds
    /// nothing beside it simply does the slow thing.
    /// </summary>
    [Fact]
    public void The_name_it_looks_for_is_the_one_a_sync_with_nothing_saved_writes()
    {
        var identity = new WorkingIndexIdentity(
            FsPath.From(_root),
            FsPath.From(_root),
            "repository",
            FsPath.From(_root),
            "commit",
            "head-tree",
            DirtyHash: null,
            SemanticDirtyHash: null);

        Assert.Equal(
            SemanticOverlayReuse.CommittedOverlayName,
            Path.GetFileName(RuntimeIndexLayout.SemanticOverlayPath(identity, "generation")));
    }

    private string OverlayDirectory()
    {
        var directory = Path.Combine(_root, "overlays");
        Directory.CreateDirectory(directory);
        return directory;
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
