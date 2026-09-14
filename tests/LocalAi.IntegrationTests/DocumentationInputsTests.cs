using CodeSearch.Core.Semantics;
using LocalAi.Documentation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationInputsTests
{
    [Fact]
    public void Public_delegates_are_documentation_types_too()
    {
        const string source = "public delegate int Resolve(int value);";
        var index = Index("public class A {}") with
        {
            Documents = [new() { RelPath = "src/Types.cs", Hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)) }],
            Symbols = [new() { Id = "Resolve", DisplayName = "Resolve", Signature = "Resolve(int)", Kind = SemanticSymbolKind.Type }],
            Occurrences = [new() { SymbolId = "Resolve", DocumentPath = "src/Types.cs", Precision = NavigationPrecision.Precise, Roles = SemanticOccurrenceRoles.Definition,
                Range = new(0, source.IndexOf("Resolve", StringComparison.Ordinal), 0, source.IndexOf("Resolve", StringComparison.Ordinal) + 7), EnclosingRange = new(0, 0, 0, source.Length) }],
        };
        Assert.Equal("Resolve", Assert.Single(Create(source, index)).Content.SymbolId);
    }

    [Fact]
    public void Selection_includes_only_precisely_indexed_public_CSharp_types_and_honors_limit()
    {
        const string source = "internal class Hidden { public class Nested {} } public class A {} public record B; public interface C { class D {} }";
        var index = Index(source);
        index = index with { Occurrences = index.Occurrences.Select(o => o.SymbolId == "B" ? o with { Precision = NavigationPrecision.Heuristic } : o).ToList() };
        var units = Create(source, index, limit: 2);
        Assert.Equal(["A", "C"], units.Select(u => u.Content.SymbolId));
        Assert.Equal(["A", "C", "D"], Create(source, index, limit: int.MaxValue).Select(u => u.Content.SymbolId));
    }

    [Fact]
    public void A_changed_body_invalidates_the_source_hash()
    {
        var before = Create("public class A { int Value => 1; }")[0];
        var after = Create("public class A { int Value => 2; }")[0];
        Assert.NotEqual(before.SourceHash, after.SourceHash);
    }

    [Fact]
    public void A_related_type_body_invalidates_references_without_changing_own_source()
    {
        var before = Create("public class A : B {} public class B { int Value => 1; }", relationship: true)[0];
        var after = Create("public class A : B {} public class B { int Value => 2; }", relationship: true)[0];
        Assert.Equal(before.SourceHash, after.SourceHash);
        Assert.NotEqual(before.ReferencesHash, after.ReferencesHash);
    }

    [Fact]
    public void Unrelated_type_in_the_same_file_does_not_invalidate_source_or_references()
    {
        var before = Create("public class A {} public class B { int Value => 1; }")[0];
        var after = Create("public class A {} public class B { int Value => 2; }")[0];
        Assert.Equal(before.SourceHash, after.SourceHash);
        Assert.Equal(before.ReferencesHash, after.ReferencesHash);
    }

    [Fact]
    public void History_is_scoped_to_source_and_related_definition_files_and_is_hashed()
    {
        const string source = "public class A {}";
        var one = DocumentationInputs.Create(Index(source), new Dictionary<string, string> { ["src/Types.cs"] = source }, paths =>
        {
            Assert.Equal(["src/Types.cs"], paths);
            return [new("commit-1", "message")];
        });
        var two = DocumentationInputs.Create(Index(source), new Dictionary<string, string> { ["src/Types.cs"] = source }, _ => [new("commit-2", "changed")]);
        Assert.NotEqual(Assert.Single(one).HistoryHash, Assert.Single(two).HistoryHash);
        Assert.Equal("message", one[0].Content.History[0].Message);
    }

    private static IReadOnlyList<DocumentationUnitInput> Create(string source, SemanticIndex? index = null, bool relationship = false, int limit = 5) =>
        DocumentationInputs.Create(index ?? Index(source, relationship), new Dictionary<string, string> { ["src/Types.cs"] = source }, _ => [], limit);

    internal static SemanticIndex Index(string source, bool relationship = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var declarations = tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().ToArray();
        return new()
        {
            RepositoryId = "repo", GenerationId = "gen", GitTree = "tree", IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [new() { RelPath = "src/Types.cs", Hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)) }],
            Symbols = declarations.Select(d => new SemanticSymbol { Id = d.Identifier.ValueText, DisplayName = d.Identifier.ValueText, Kind = SemanticSymbolKind.Type, Signature = d.Identifier.ValueText }).ToList(),
            Occurrences = declarations.Select(d =>
            {
                var name = tree.GetLineSpan(d.Identifier.Span); var body = tree.GetLineSpan(d.Span);
                return new SemanticOccurrence { SymbolId = d.Identifier.ValueText, DocumentPath = "src/Types.cs", Precision = NavigationPrecision.Precise, Roles = SemanticOccurrenceRoles.Definition,
                    Range = new(name.StartLinePosition.Line, name.StartLinePosition.Character, name.EndLinePosition.Line, name.EndLinePosition.Character),
                    EnclosingRange = new(body.StartLinePosition.Line, body.StartLinePosition.Character, body.EndLinePosition.Line, body.EndLinePosition.Character) };
            }).ToList(),
            Relationships = relationship ? [new() { SourceSymbolId = "A", TargetSymbolId = "B", Kind = SemanticRelationshipKind.Implementation }] : [],
        };
    }
}
