using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// An overlay built from a rebuild that only looked at part of the tree.
///
/// `Create` works out what changed by diffing the base against a current index of everything, and
/// treats a document present in the base but absent from the current one as deleted. Hand it an
/// index built from three projects out of four hundred and it would tombstone the rest of the
/// repository.
///
/// This takes the recomputed paths as given instead: those come from the index that was built,
/// everything else is left exactly as the base has it.
/// </summary>
public sealed class AnOverlayFromAScopedRebuildTests
{
    private const string Symbol = "dotnet M:Demo.Service.Go";
    private static readonly DateTime IndexedAt =
        new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The whole point. `Use.cs` was not recomputed and is not in the scoped index; it must come
    /// through the overlay untouched rather than as a deletion.
    /// </summary>
    [Fact]
    public void A_document_nobody_recomputed_is_left_alone()
    {
        var @base = Index(
            "base-tree",
            [Document("Def.cs", 1), Document("Use.cs", 2)],
            [Definition("Def.cs", 1), Reference("Use.cs", 4)]);
        var scoped = Index(
            "working-tree",
            [Document("Def.cs", 3)],
            [Definition("Def.cs", 7)]);

        var overlay = SemanticIndexOverlay.CreateScoped(@base, scoped, ["Def.cs"]);

        Assert.Empty(overlay.DeletedDocuments);
        var materialized = overlay.Materialize(@base);
        Assert.Contains(materialized.Documents, document => document.RelPath == "Use.cs");
        Assert.Contains(
            materialized.Occurrences,
            occurrence => occurrence.DocumentPath == "Use.cs" && occurrence.Range.StartLine == 4);
    }

    [Fact]
    public void A_recomputed_document_replaces_the_one_in_the_base()
    {
        var @base = Index(
            "base-tree",
            [Document("Def.cs", 1)],
            [Definition("Def.cs", 1)]);
        var scoped = Index(
            "working-tree",
            [Document("Def.cs", 3)],
            [Definition("Def.cs", 7)]);

        var materialized = SemanticIndexOverlay
            .CreateScoped(@base, scoped, ["Def.cs"])
            .Materialize(@base);

        var occurrence = Assert.Single(
            materialized.Occurrences,
            item => item.DocumentPath == "Def.cs");
        Assert.Equal(7, occurrence.Range.StartLine);
    }

    /// <summary>
    /// Recomputed and gone: the file was deleted, and that is the one case where a tombstone is
    /// right. It is distinguishable from "not looked at" only because the caller says which paths
    /// were looked at.
    /// </summary>
    [Fact]
    public void A_recomputed_document_that_is_no_longer_there_is_a_deletion()
    {
        var @base = Index(
            "base-tree",
            [Document("Def.cs", 1), Document("Use.cs", 2)],
            [Definition("Def.cs", 1), Reference("Use.cs", 4)]);
        var scoped = Index("working-tree", [], []);

        var overlay = SemanticIndexOverlay.CreateScoped(@base, scoped, ["Def.cs"]);

        Assert.Equal(["Def.cs"], overlay.DeletedDocuments);
    }

    [Fact]
    public void A_path_that_is_new_in_the_scoped_index_arrives()
    {
        var @base = Index("base-tree", [Document("Use.cs", 2)], [Reference("Use.cs", 4)]);
        var scoped = Index("working-tree", [Document("New.cs", 5)], [Definition("New.cs", 1)]);

        var materialized = SemanticIndexOverlay
            .CreateScoped(@base, scoped, ["New.cs"])
            .Materialize(@base);

        Assert.Contains(materialized.Documents, document => document.RelPath == "New.cs");
        Assert.Contains(materialized.Documents, document => document.RelPath == "Use.cs");
    }

    private static SemanticIndex Index(
        string gitTree,
        List<SemanticDocument> documents,
        List<SemanticOccurrence> occurrences) =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = gitTree,
            DirtyHash = gitTree == "base-tree" ? null : "dirty-hash",
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
