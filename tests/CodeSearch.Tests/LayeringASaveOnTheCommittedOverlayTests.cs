using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// A save layered on the overlay of the commit it was made against.
///
/// A worktree's overlay covers two different things at once: what the branch has committed since
/// the base generation, and what is saved but not committed. Only the second moves when a file is
/// saved, and the first is already written down -- the `clean` overlay in the same directory is
/// the same commit with nothing dirty on top of it.
///
/// So a save recomputes the files it touched and takes everything else from that overlay, which
/// is the corpus layer's arrangement since #388 and the reason a one-line edit embedded one chunk
/// while the semantic layer reparsed the branch's other hundred files (#519).
///
/// The committed overlay in particular, never whichever one happens to be newest: telling an API
/// edit from a body edit needs the earlier version of the saved file, and git has the committed
/// one. Whatever another dirty overlay was built from has been overwritten on disk.
/// </summary>
public sealed class LayeringASaveOnTheCommittedOverlayTests
{
    private const string Symbol = "dotnet M:Demo.Service.Go";
    private const string Contract = "dotnet T:Demo.IService";
    private static readonly DateTime IndexedAt =
        new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The whole point. `Branch.cs` was changed by a commit on this branch and not by this save,
    /// so it is not in the recomputed index -- and it has to keep the records the committed
    /// overlay gave it rather than fall back to the base or vanish.
    /// </summary>
    [Fact]
    public void A_file_the_branch_changed_and_the_save_did_not_keeps_its_records()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Branch.cs", 1), Document("Saved.cs", 2)],
            [Definition("Branch.cs", 1), Reference("Saved.cs", 2)]);
        var committed = SemanticIndexOverlay.CreateScoped(
            @base,
            Index(
                "head-tree",
                null,
                [Document("Branch.cs", 3), Document("Saved.cs", 2)],
                [Definition("Branch.cs", 5), Reference("Saved.cs", 2)]),
            ["Branch.cs"]);
        var recomputed = Index(
            "head-tree",
            "dirty",
            [Document("Saved.cs", 9)],
            [Reference("Saved.cs", 12)]);

        var layered = SemanticIndexOverlay.Layer(committed, @base, recomputed, ["Saved.cs"]);

        var materialized = layered.Materialize(@base);
        Assert.Equal(
            Document("Branch.cs", 3).Hash,
            materialized.Documents.Single(document => document.RelPath == "Branch.cs").Hash);
        Assert.Contains(
            materialized.Occurrences,
            occurrence => occurrence.DocumentPath == "Branch.cs" && occurrence.Range.StartLine == 5);
    }

    [Fact]
    public void A_saved_file_the_branch_had_also_changed_takes_the_new_records()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Branch.cs", 1)],
            [Definition("Branch.cs", 1)]);
        var committed = SemanticIndexOverlay.CreateScoped(
            @base,
            Index("head-tree", null, [Document("Branch.cs", 3)], [Definition("Branch.cs", 5)]),
            ["Branch.cs"]);
        var recomputed = Index(
            "head-tree",
            "dirty",
            [Document("Branch.cs", 9)],
            [Definition("Branch.cs", 12)]);

        var materialized = SemanticIndexOverlay
            .Layer(committed, @base, recomputed, ["Branch.cs"])
            .Materialize(@base);

        Assert.Equal(
            Document("Branch.cs", 9).Hash,
            materialized.Documents.Single(document => document.RelPath == "Branch.cs").Hash);
        Assert.Equal(
            12,
            materialized.Occurrences.Single(occurrence => occurrence.DocumentPath == "Branch.cs")
                .Range.StartLine);
    }

    /// <summary>
    /// A file the branch deleted is a tombstone in the committed overlay, and the save says
    /// nothing about it. Carrying the tombstone across is what stops the base's copy coming back.
    /// </summary>
    [Fact]
    public void A_file_the_branch_deleted_stays_deleted()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Gone.cs", 1), Document("Saved.cs", 2)],
            [Definition("Gone.cs", 1), Reference("Saved.cs", 2)]);
        var committed = SemanticIndexOverlay.CreateScoped(
            @base,
            Index("head-tree", null, [Document("Saved.cs", 2)], [Reference("Saved.cs", 2)]),
            ["Gone.cs"]);
        var recomputed = Index(
            "head-tree",
            "dirty",
            [Document("Saved.cs", 9)],
            [Reference("Saved.cs", 12)]);

        var layered = SemanticIndexOverlay.Layer(committed, @base, recomputed, ["Saved.cs"]);

        Assert.Contains("Gone.cs", layered.DeletedDocuments);
        Assert.DoesNotContain(
            layered.Materialize(@base).Documents,
            document => document.RelPath == "Gone.cs");
    }

    /// <summary>
    /// A file deleted since the commit is named among the recomputed paths and produces no
    /// document. Left out of the overlay it would go on answering from the committed layer.
    /// </summary>
    [Fact]
    public void A_file_deleted_since_the_commit_becomes_a_tombstone()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Gone.cs", 1), Document("Saved.cs", 2)],
            [Definition("Gone.cs", 1), Reference("Saved.cs", 2)]);
        var committed = SemanticIndexOverlay.CreateScoped(
            @base,
            Index(
                "head-tree",
                null,
                [Document("Gone.cs", 3), Document("Saved.cs", 2)],
                [Definition("Gone.cs", 3), Reference("Saved.cs", 2)]),
            ["Gone.cs"]);
        var recomputed = Index("head-tree", "dirty", [], []);

        var layered = SemanticIndexOverlay.Layer(committed, @base, recomputed, ["Gone.cs"]);

        Assert.Contains("Gone.cs", layered.DeletedDocuments);
        Assert.DoesNotContain(
            layered.Materialize(@base).Documents,
            document => document.RelPath == "Gone.cs");
    }

    /// <summary>
    /// Only the committed overlay will do. Another dirty overlay stands for a state whose files
    /// are no longer on disk, so nothing can say whether one of them changed a declaration -- and
    /// if one did, every record layered over it is stale in a way no later sync notices.
    /// </summary>
    [Fact]
    public void Layering_on_a_dirty_overlay_is_refused()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Branch.cs", 1)],
            [Definition("Branch.cs", 1)]);
        var dirty = SemanticIndexOverlay.CreateScoped(
            @base,
            Index("head-tree", "earlier", [Document("Branch.cs", 3)], [Definition("Branch.cs", 5)]),
            ["Branch.cs"]);
        var recomputed = Index(
            "head-tree",
            "dirty",
            [Document("Branch.cs", 9)],
            [Definition("Branch.cs", 12)]);

        Assert.Throws<InvalidDataException>(() =>
            SemanticIndexOverlay.Layer(dirty, @base, recomputed, ["Branch.cs"]));
    }

    [Fact]
    public void Layering_a_rebuild_of_another_commit_is_refused()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Branch.cs", 1)],
            [Definition("Branch.cs", 1)]);
        var committed = SemanticIndexOverlay.CreateScoped(
            @base,
            Index("head-tree", null, [Document("Branch.cs", 3)], [Definition("Branch.cs", 5)]),
            ["Branch.cs"]);
        var recomputed = Index(
            "other-head-tree",
            "dirty",
            [Document("Branch.cs", 9)],
            [Definition("Branch.cs", 12)]);

        Assert.Throws<InvalidDataException>(() =>
            SemanticIndexOverlay.Layer(committed, @base, recomputed, ["Branch.cs"]));
    }

    /// <summary>
    /// A relationship is held against the symbol it was read from: the indexer walks a
    /// declaration and writes down what it implements. Remove the interface from that
    /// declaration and the committed layer's copy has to go with it, or `find_implementations`
    /// answers with a base class the file no longer names.
    ///
    /// Written after the rule it checks rather than before it -- said here rather than left to
    /// look like the rest.
    /// </summary>
    [Fact]
    public void An_implementation_the_save_removed_does_not_survive_in_the_layer()
    {
        var @base = Index(
            "base-tree",
            null,
            [Document("Saved.cs", 1)],
            [Definition("Saved.cs", 1)]);
        var committedIndex = Index(
            "head-tree",
            null,
            [Document("Saved.cs", 3)],
            [Definition("Saved.cs", 3)]) with
        {
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = Symbol,
                    TargetSymbolId = Contract,
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
            Symbols = [Method(), Interface()],
        };
        var committed = SemanticIndexOverlay.CreateScoped(@base, committedIndex, ["Saved.cs"]);
        Assert.NotEmpty(committed.Changed.Relationships);
        var recomputed = Index(
            "head-tree",
            "dirty",
            [Document("Saved.cs", 9)],
            [Definition("Saved.cs", 9)]);

        var layered = SemanticIndexOverlay.Layer(committed, @base, recomputed, ["Saved.cs"]);

        Assert.Empty(layered.Changed.Relationships);
        Assert.Empty(layered.Materialize(@base).Relationships);
    }

    private static SemanticIndex Index(
        string gitTree,
        string? dirtyHash,
        List<SemanticDocument> documents,
        List<SemanticOccurrence> occurrences) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            BaseCommit = "commit",
            IndexedAtUtc = IndexedAt,
            Documents = documents,
            Symbols = [Method()],
            Occurrences = occurrences,
            Relationships = [],
        };

    private static SemanticDocument Document(string path, int value) =>
        new()
        {
            RelPath = path,
            Hash = Enumerable.Repeat(unchecked((byte)value), 32).ToArray(),
        };

    private static SemanticSymbol Method() =>
        new()
        {
            Id = Symbol,
            DisplayName = "Go",
            Kind = SemanticSymbolKind.Method,
            Signature = "void Go()",
        };

    private static SemanticSymbol Interface() =>
        new()
        {
            Id = Contract,
            DisplayName = "IService",
            Kind = SemanticSymbolKind.Type,
            Signature = "Demo.IService",
        };

    private static SemanticOccurrence Definition(string path, int line) =>
        Occurrence(path, line, SemanticOccurrenceRoles.Definition);

    private static SemanticOccurrence Reference(string path, int line) =>
        Occurrence(path, line, SemanticOccurrenceRoles.Reference);

    private static SemanticOccurrence Occurrence(
        string path,
        int line,
        SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = new SourceRange(line, 0, line, 4),
            SymbolId = Symbol,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };
}
