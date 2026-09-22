using CodeSearch.Core.Semantics;
using LocalAi.Documentation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationInputsTests
{
    [Fact]
    public void Path_selection_applies_before_public_type_reads_and_the_limit()
    {
        var sources = new Dictionary<string, string>
        {
            ["a/Unrelated.cs"] = "public class Unrelated {}",
            ["src/Chosen.cs"] = "public class Chosen {}",
            ["src-extra/Other.cs"] = "public class Other {}",
        };
        var reads = new List<string>();
        var unit = Assert.Single(DocumentationInputs.CreateScoped(CombinedIndex(sources), p =>
        {
            reads.Add(p);
            return sources[p];
        }, _ => [], limit: 1, path: "src", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Chosen", unit.Content.SymbolId);
        Assert.Equal(["src/Chosen.cs"], reads);
    }

    [Theory]
    [InlineData("src/A.cs")]
    [InlineData("./src\\A.cs")]
    [InlineData("src/")]
    public void Path_scope_keeps_partial_declarations_and_external_dependency_hashes(string path)
    {
        var sources = new Dictionary<string, string>
        {
            ["src/A.cs"] = "public partial class A : B {}",
            ["other/Partial.cs"] = "public partial class A { int Value => 1; }",
            ["lib/B.cs"] = "public class B { int Value => 1; }",
            ["src-extra/Unrelated.cs"] = "public class Unrelated {}",
        };
        DocumentationUnitInput Select()
        {
            var index = CombinedIndex(sources) with
            {
                Relationships = [new() { SourceSymbolId = "A", TargetSymbolId = "B", Kind = SemanticRelationshipKind.Implementation }],
            };
            return Assert.Single(DocumentationInputs.CreateScoped(index, p => sources[p], _ => [], 1, path,
                TestContext.Current.CancellationToken));
        }
        var before = Select();
        Assert.Equal(["other/Partial.cs", "src/A.cs"], before.SourcePaths);
        Assert.Equal(["lib/B.cs"], before.DependencyPaths);
        sources["lib/B.cs"] = "public class B { int Value => 2; }";
        var changedDependency = Select();
        Assert.Equal(before.SourceHash, changedDependency.SourceHash);
        Assert.NotEqual(before.ReferencesHash, changedDependency.ReferencesHash);
        sources["src-extra/Unrelated.cs"] = "public class Unrelated { int Value => 99; }";
        Assert.Equal(changedDependency.ReferencesHash, Select().ReferencesHash);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("\\\\server\\share")]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_scopes_are_rejected_before_reading_sources(string path)
    {
        var error = Assert.Throws<DocumentationValidationException>(() => DocumentationInputs.CreateScoped(Index("public class A {}"),
            _ => throw new InvalidOperationException("must not read"), _ => [], path: path,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("scope_invalid", error.Errors);
    }

    [Fact]
    public void Empty_scope_does_not_fall_back_to_an_unrelated_type()
    {
        var error = Assert.Throws<DocumentationValidationException>(() => DocumentationInputs.CreateScoped(Index("public class A {}"),
            _ => throw new InvalidOperationException("must not read"), _ => [], path: "missing",
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("scope_empty", error.Errors);
    }

    [Fact]
    public void Scoped_collection_observes_cancellation_during_source_reads()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => DocumentationInputs.CreateScoped(Index("public class A {}"),
            _ => { cancellation.Cancel(); return "public class A {}"; }, _ => [], path: "src", cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(int.MaxValue, 8)]
    public void Scoped_collection_stops_loading_after_the_requested_public_types(int limit, int expected)
    {
        var sources = Enumerable.Range(0, 8).ToDictionary(i => $"src/Type{i}.cs", i => $"public class Type{i} {{}}", StringComparer.Ordinal);
        var index = CombinedIndex(sources);
        var reads = new List<string>();
        var units = DocumentationInputs.CreateScoped(index, path =>
        {
            reads.Add(path);
            return sources[path];
        }, _ => [], limit, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, units.Count);
        Assert.Equal(sources.Keys.Take(expected), reads);
    }

    [Fact]
    public void Scoped_collection_includes_all_partial_bodies_and_dependencies_once()
    {
        var sources = new Dictionary<string, string>
        {
            ["src/A.cs"] = "public partial class A : B {}",
            ["src/AOther.cs"] = "public partial class A { int Value => 1; }",
            ["src/B.cs"] = "public class B {}",
            ["src/ZUnused.cs"] = "public class ZUnused {}"
        };
        var index = CombinedIndex(sources) with
        {
            Relationships = [new() { SourceSymbolId = "A", TargetSymbolId = "B", Kind = SemanticRelationshipKind.Implementation }]
        };
        var reads = new List<string>();
        var unit = Assert.Single(DocumentationInputs.CreateScoped(index, path =>
        {
            reads.Add(path);
            return sources[path];
        }, paths => { Assert.Equal(["src/A.cs", "src/AOther.cs", "src/B.cs"], paths); return []; }, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("A", unit.Content.SymbolId);
        Assert.Equal(["src/A.cs", "src/AOther.cs", "src/B.cs"], reads);
        Assert.Equal(["A", "B"], DocumentationInputs.CreateScoped(index, path => sources[path], _ => [], 2,
            cancellationToken: TestContext.Current.CancellationToken).Select(u => u.Content.SymbolId));
    }

    [Fact]
    public void Scoped_collection_continues_past_hidden_types_and_ignores_non_CSharp_definitions()
    {
        var sources = new Dictionary<string, string>
        {
            ["src/0.txt"] = "public class TextOnly {}",
            ["src/1.cs"] = "internal class Hidden { public class Nested {} }",
            ["src/2.cs"] = "public class Visible {}",
            ["src/3.cs"] = "public class Unused {}"
        };
        var reads = new List<string>();
        var unit = Assert.Single(DocumentationInputs.CreateScoped(CombinedIndex(sources), path =>
        {
            reads.Add(path);
            return sources[path];
        }, _ => [], 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Visible", unit.Content.SymbolId);
        Assert.Equal(["src/1.cs", "src/2.cs"], reads);
    }

    private static SemanticIndex CombinedIndex(IReadOnlyDictionary<string, string> sources)
    {
        var indexes = sources.Select(pair => (pair.Key, Index: Index(pair.Value))).ToArray();
        return indexes[0].Index with
        {
            Documents = indexes.SelectMany(pair => pair.Index.Documents.Select(d => d with { RelPath = pair.Key })).ToList(),
            Symbols = indexes.SelectMany(pair => pair.Index.Symbols).DistinctBy(s => s.Id).ToList(),
            Occurrences = indexes.SelectMany(pair => pair.Index.Occurrences.Select(o => o with { DocumentPath = pair.Key })).ToList()
        };
    }

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
