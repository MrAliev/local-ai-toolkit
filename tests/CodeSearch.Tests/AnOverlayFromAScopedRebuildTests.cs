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

    /// <summary>
    /// An implementor in a project the rebuild never opened keeps implementing.
    ///
    /// A relationship is read off the declaration it belongs to: the indexer walks `class Bar :
    /// IService` and writes `Bar -> IService` down against `Bar`. So the question of whether a
    /// base relationship still holds is a question about the file `Bar` is declared in, and a
    /// save that touches `IService` says nothing about it.
    ///
    /// Dropping on either endpoint, as this did, was harmless while every overlay carried a
    /// rebuild of the whole tree -- whatever was dropped came straight back. Since #521 a rebuild
    /// covers the projects it opened, and those are the ones the changed file *references*, never
    /// the ones that reference it. Adding a doc comment to an interface -- not a surface change,
    /// so the narrowest plan there is -- would empty `find_implementations` for it until the next
    /// commit.
    /// </summary>
    [Fact]
    public void An_implementor_the_rebuild_never_opened_keeps_implementing()
    {
        const string bar = "dotnet T:Demo.Bar";
        const string service = "dotnet T:Demo.IService";
        var @base = Index(
            "base-tree",
            [Document("Bar.cs", 1), Document("IService.cs", 2)],
            []) with
        {
            Symbols = [Type(bar, "Bar"), Type(service, "IService")],
            Occurrences =
            [
                Declaration("Bar.cs", 1, bar),
                Declaration("IService.cs", 1, service),
                Use("Bar.cs", 1, service),
            ],
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = bar,
                    TargetSymbolId = service,
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
        };
        // A doc comment on the interface. Its own project is reopened; `Bar.cs` lives in one that
        // references it, so nothing about `Bar` is recomputed and no relationship is re-emitted.
        var scoped = Index("working-tree", [Document("IService.cs", 3)], []) with
        {
            Symbols = [Type(service, "IService")],
            Occurrences = [Declaration("IService.cs", 4, service)],
            Relationships = [],
        };

        var materialized = SemanticIndexOverlay
            .CreateScoped(@base, scoped, ["IService.cs"])
            .Materialize(@base);

        Assert.Contains(
            materialized.Relationships,
            relationship =>
                relationship.SourceSymbolId == bar && relationship.TargetSymbolId == service);
    }

    private static SemanticSymbol Type(string id, string name) =>
        new()
        {
            Id = id,
            DisplayName = name,
            Kind = SemanticSymbolKind.Type,
            Signature = "Demo." + name,
        };

    private static SemanticOccurrence Declaration(string path, int line, string symbolId) =>
        At(path, line, SemanticOccurrenceRoles.Definition, symbolId);

    private static SemanticOccurrence Use(string path, int line, string symbolId) =>
        At(path, line, SemanticOccurrenceRoles.Reference, symbolId);

    private static SemanticOccurrence At(
        string path,
        int line,
        SemanticOccurrenceRoles roles,
        string symbolId) =>
        new()
        {
            DocumentPath = path,
            Range = new SourceRange(line, 0, line, 4),
            SymbolId = symbolId,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };

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
