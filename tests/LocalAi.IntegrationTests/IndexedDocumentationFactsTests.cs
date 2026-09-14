using CodeSearch.Core.Semantics;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class IndexedDocumentationFactsTests
{
    [Fact]
    public void Facts_use_indexed_definitions_members_references_and_relationships()
    {
        var input = IndexedDocumentationFacts.Create(Index(), "A", []);
        Assert.Equal("Example.A", input.Name);
        Assert.Equal("src/A.cs", input.SourcePath);
        Assert.Equal(4, input.SourceLine);
        Assert.Contains(input.Facts, f => f.SymbolId == "M" && f.Text.Contains("Run()", StringComparison.Ordinal));
        Assert.Contains(input.Facts, f => f.SymbolId == "A" && f.Text.Contains("src/Use.cs:5", StringComparison.Ordinal));
        var edge = Assert.Single(input.Edges);
        Assert.Equal("Implementation", edge.Kind);
        Assert.Equal("Example.B", input.Nodes.Single(n => n.Id == edge.To).Label);
    }

    [Fact]
    public void Unresolvable_symbol_is_refused() => Assert.Throws<DocumentationValidationException>(() =>
        IndexedDocumentationFacts.Create(Index(), "invented", []));

    [Fact]
    public void Heuristic_occurrence_cannot_become_verified_evidence()
    {
        var index = Index();
        index.Occurrences[0] = index.Occurrences[0] with { Precision = NavigationPrecision.Heuristic };
        Assert.Throws<DocumentationValidationException>(() => IndexedDocumentationFacts.Create(index, "A", []));
    }

    private static SemanticIndex Index() => new()
    {
        RepositoryId = "repo", GenerationId = "gen", GitTree = "tree", DirtyHash = null,
        IndexedAtUtc = DateTime.UnixEpoch,
        Documents = [new() { RelPath = "src/A.cs", Hash = new byte[32] }, new() { RelPath = "src/Use.cs", Hash = new byte[32] }],
        Symbols = [new() { Id = "A", DisplayName = "Example.A", Kind = SemanticSymbolKind.Type, Signature = "public class A : B" },
            new() { Id = "M", DisplayName = "Example.A.Run", Kind = SemanticSymbolKind.Method, Signature = "public void Run()" },
            new() { Id = "B", DisplayName = "Example.B", Kind = SemanticSymbolKind.Type, Signature = "public class B" }],
        Occurrences = [new() { DocumentPath = "src/A.cs", SymbolId = "A", Range = new(3, 1, 3, 2), EnclosingRange = new(3, 0, 12, 1), Roles = SemanticOccurrenceRoles.Definition, Precision = NavigationPrecision.Precise },
            new() { DocumentPath = "src/A.cs", SymbolId = "M", Range = new(5, 1, 5, 2), Roles = SemanticOccurrenceRoles.Definition, Precision = NavigationPrecision.Precise },
            new() { DocumentPath = "src/Use.cs", SymbolId = "A", Range = new(4, 1, 4, 2), Roles = SemanticOccurrenceRoles.Reference, Precision = NavigationPrecision.Precise }],
        Relationships = [new() { SourceSymbolId = "A", TargetSymbolId = "B", Kind = SemanticRelationshipKind.Implementation }],
    };
}
