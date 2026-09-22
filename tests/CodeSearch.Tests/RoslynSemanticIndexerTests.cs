using CodeSearch.Core.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Tests;

public sealed class RoslynSemanticIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-roslyn-indexer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Impact_evidence_preserves_exact_overload_reference_and_override_coordinates()
    {
        var (solution, appSource, libSource) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(solution, _root, Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var position = Position(libSource, "Work(int value) =>");
        var report = SemanticImpactAnalysis.Analyze(index, "Lib/Base.cs", position.Line, position.Character,
            new("repo", "generation", "tree", null), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Evidence.Count);
        var reference = Assert.Single(report.Evidence, edge => edge.Kind == "reference");
        Assert.Equal(Position(appSource, "Work(value);"), new LinePosition(
            reference.Source.Location.Range.StartLine, reference.Source.Location.Range.StartCharacter));
        var overridden = Assert.Single(report.Evidence, edge => edge.Kind == "override");
        Assert.Equal(Position(appSource, "Work(int"), new LinePosition(
            overridden.Source.Location.Range.StartLine, overridden.Source.Location.Range.StartCharacter));
        Assert.All(report.Evidence, edge =>
        {
            Assert.Equal(report.SymbolId, edge.TargetSymbolId);
            Assert.Equal("App/Worker.cs", edge.Source.Location.DocumentPath);
            Assert.Equal(NavigationPrecision.Precise, edge.Source.Location.Precision);
            Assert.Equal(Convert.ToHexStringLower(index.Documents.Single(d => d.RelPath == "App/Worker.cs").Hash),
                edge.Source.DocumentHash);
        });
        Assert.Equal("Lib/Base.cs", Assert.Single(report.Definitions).Location.DocumentPath);
        Assert.False(report.Truncated);

        var callPosition = Position(appSource, "Work(value);");
        var fromReference = SemanticImpactAnalysis.Analyze(index, "App/Worker.cs", callPosition.Line, callPosition.Character,
            report.Snapshot, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(report.SymbolId, fromReference.SymbolId);
        Assert.Equal(report.Evidence, fromReference.Evidence);
    }

    [Fact]
    public async Task Impact_pilot_labels_incoming_interface_and_base_type_declarations()
    {
        var (solution, _, libSource) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(solution, _root, Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        SemanticImpactReport Analyze(string name)
        {
            var position = Position(libSource, name);
            return SemanticImpactAnalysis.Analyze(index, "Lib/Base.cs", position.Line, position.Character,
                new("repo", "generation", "tree", null), cancellationToken: TestContext.Current.CancellationToken);
        }
        var implementation = Assert.Single(Analyze("IWorker").Evidence, edge => edge.Kind == "implementation");
        var derived = Assert.Single(Analyze("Base").Evidence, edge => edge.Kind == "type-definition");
        Assert.Equal(implementation.RelatedSymbolId, derived.RelatedSymbolId);
        Assert.Equal(SemanticOccurrenceRoles.Definition, implementation.Source.Location.Roles);
        Assert.Equal("App/Worker.cs", implementation.Source.Location.DocumentPath);
    }

    [Fact]
    public async Task Impact_limit_is_explicit_and_deterministic_across_index_order()
    {
        var (solution, _, libSource) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(solution, _root, Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var position = Position(libSource, "Work(int value) =>");
        SemanticImpactReport Analyze(SemanticIndex value) => SemanticImpactAnalysis.Analyze(value, "Lib/Base.cs",
            position.Line, position.Character, new("repo", "generation", "tree", null), limit: 1,
            cancellationToken: TestContext.Current.CancellationToken);
        var before = Analyze(index);
        Assert.True(before.Truncated);
        Assert.Equal(2, before.TotalEvidence);
        Assert.Single(before.Evidence);
        var after = Analyze(index with
        {
            Occurrences = index.Occurrences.AsEnumerable().Reverse().Concat(index.Occurrences).ToList(),
            Relationships = index.Relationships.AsEnumerable().Reverse().Concat(index.Relationships).ToList(),
        });
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal(before.TotalEvidence, after.TotalEvidence);
    }

    [Fact]
    public async Task Impact_never_promotes_heuristics_or_unhashed_or_other_language_locations()
    {
        var (solution, _, libSource) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(solution, _root, Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var position = Position(libSource, "Work(int value) =>");
        SemanticImpactReport Analyze(SemanticIndex value) => SemanticImpactAnalysis.Analyze(value, "Lib/Base.cs",
            position.Line, position.Character, new("repo", "generation", "tree", null),
            cancellationToken: TestContext.Current.CancellationToken);
        var before = Analyze(index);
        var reference = index.Occurrences.Single(o => o.SymbolId == before.SymbolId && o.Roles.HasFlag(SemanticOccurrenceRoles.Reference));
        var extras = new[]
        {
            reference with { Precision = NavigationPrecision.Heuristic, Range = new(90, 0, 90, 4) },
            reference with { Precision = NavigationPrecision.Inferred, Range = new(91, 0, 91, 4) },
            reference with { DocumentPath = "Unknown.cs" },
            reference with { DocumentPath = "other.py" },
        };
        var after = Analyze(index with
        {
            Occurrences = [.. index.Occurrences, .. extras],
            Documents = [.. index.Documents, new() { RelPath = "other.py", Hash = new byte[32] }],
        });
        Assert.Equal(before.Evidence, after.Evidence);
        var withoutEvidence = Analyze(index with { Documents = index.Documents.Where(d => d.RelPath != "App/Worker.cs").ToList() });
        Assert.Empty(withoutEvidence.Evidence);
        Assert.Equal(0, withoutEvidence.TotalEvidence);
    }

    [Fact]
    public async Task Impact_rejects_a_different_snapshot_and_unverifiable_selection()
    {
        var (solution, _, libSource) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(solution, _root, Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var position = Position(libSource, "Work(int value) =>");
        Assert.Throws<SemanticSnapshotMismatchException>(() => SemanticImpactAnalysis.Analyze(index, "Lib/Base.cs",
            position.Line, position.Character, new("repo", "generation", "different", null),
            cancellationToken: TestContext.Current.CancellationToken));
        var error = Assert.Throws<InvalidOperationException>(() => SemanticImpactAnalysis.Analyze(
            index with { Documents = [] }, "Lib/Base.cs", position.Line, position.Character,
            new("repo", "generation", "tree", null), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("impact_source_hash_required", error.Message);
        var imprecise = index with { Occurrences = index.Occurrences.Select(o => o with { Precision = NavigationPrecision.Heuristic }).ToList() };
        Assert.Equal("impact_precise_csharp_required", Assert.Throws<InvalidOperationException>(() => SemanticImpactAnalysis.Analyze(
            imprecise, "Lib/Base.cs", position.Line, position.Character, new("repo", "generation", "tree", null),
            cancellationToken: TestContext.Current.CancellationToken)).Message);
    }

    [Fact]
    public void Impact_honors_precancelled_requests_before_loading_the_index()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticImpactAnalysis.Analyze(null!, "Missing.cs", 0, 0,
            new("repo", "generation", "tree", null), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Resolves_overloads_and_cross_project_references_precisely()
    {
        var (solution, appSource, _) = Solution();
        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var navigation = new SemanticNavigationService(index);
        var snapshot = new SemanticSnapshotIdentity("repo", "generation", "tree", null);

        var intCall = Position(appSource, "Work(42)");
        var stringCall = Position(appSource, "Work(\"x\")");
        var intDefinitions = navigation.GoToDefinition(
            "App/Worker.cs", intCall.Line, intCall.Character, snapshot);
        var stringDefinitions = navigation.GoToDefinition(
            "App/Worker.cs", stringCall.Line, stringCall.Character, snapshot);

        var intDefinition = Assert.Single(intDefinitions);
        var stringDefinition = Assert.Single(stringDefinitions);
        Assert.Equal("App/Worker.cs", intDefinition.DocumentPath);
        Assert.Equal("Lib/Base.cs", stringDefinition.DocumentPath);
        Assert.NotEqual(intDefinition.SymbolId, stringDefinition.SymbolId);
    }

    [Fact]
    public async Task Emits_override_and_interface_relationships()
    {
        var (solution, _, libSource) = Solution();

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Override &&
            index.Symbols.Single(symbol => symbol.Id == relationship.SourceSymbolId)
                .Signature.Contains("Worker.Work(int)", StringComparison.Ordinal) &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("Base.Work(int)", StringComparison.Ordinal));
        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Implementation &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("IWorker.Work(int)", StringComparison.Ordinal));

        Assert.Contains(index.Relationships, relationship =>
            relationship.Kind == SemanticRelationshipKind.Implementation &&
            index.Symbols.Single(symbol => symbol.Id == relationship.SourceSymbolId)
                .Signature.Contains("Worker", StringComparison.Ordinal) &&
            index.Symbols.Single(symbol => symbol.Id == relationship.TargetSymbolId)
                .Signature.Contains("IWorker", StringComparison.Ordinal));

        var interfacePosition = Position(libSource, "IWorker");
        var implementations = new SemanticNavigationService(index).FindImplementations(
            "Lib/Base.cs",
            interfacePosition.Line,
            interfacePosition.Character,
            new SemanticSnapshotIdentity("repo", "generation", "tree", null));

        Assert.Contains(implementations, implementation =>
            implementation.DocumentPath == "App/Worker.cs" &&
            index.Symbols.Single(symbol => symbol.Id == implementation.SymbolId)
                .Signature.Contains("Worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_type_definitions_share_one_canonical_symbol()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        var project = AddProject(workspace.CurrentSolution, "Partial");
        var solution = project.Solution
            .AddDocument(DocumentId.CreateNewId(project.Id), "One.cs",
                SourceText.From("namespace Demo; public partial class Pair { }"),
                filePath: Path.Combine(_root, "One.cs"))
            .AddDocument(DocumentId.CreateNewId(project.Id), "Two.cs",
                SourceText.From("namespace Demo; public partial class Pair { }"),
                filePath: Path.Combine(_root, "Two.cs"));

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var pairDefinitions = index.Occurrences.Where(occurrence =>
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
            index.Symbols.Single(symbol => symbol.Id == occurrence.SymbolId).DisplayName == "Pair")
            .ToArray();

        Assert.Equal(2, pairDefinitions.Length);
        Assert.Single(pairDefinitions.Select(occurrence => occurrence.SymbolId).Distinct());
        var position = Position("namespace Demo; public partial class Pair { }", "Pair");
        var impact = SemanticImpactAnalysis.Analyze(index, "One.cs", position.Line, position.Character,
            new("repo", "generation", "tree", null), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["One.cs", "Two.cs"], impact.Definitions.Select(source => source.Location.DocumentPath));
        Assert.All(impact.Definitions, source => Assert.Equal(64, source.DocumentHash.Length));
    }

    [Fact]
    public async Task Alias_references_resolve_to_the_alias_definition()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        var project = AddProject(workspace.CurrentSolution, "Aliases");
        const string source =
            """
            using PairAlias = Demo.Pair<int>;
            namespace Demo { public class Pair<T> { public T Echo(T value) => value; } }
            public class Use { public int Run(PairAlias pair) => pair.Echo(1); }
            """;
        var solution = project.Solution.AddDocument(
            DocumentId.CreateNewId(project.Id),
            "Aliases.cs",
            SourceText.From(source),
            filePath: Path.Combine(_root, "Aliases.cs"));
        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);
        var position = Position(source, "PairAlias pair");
        var definitions = new SemanticNavigationService(index).GoToDefinition(
            "Aliases.cs",
            position.Line,
            position.Character,
            new SemanticSnapshotIdentity("repo", "generation", "tree", null));

        var definition = Assert.Single(definitions);
        Assert.Equal(Position(source, "PairAlias ="), new LinePosition(
            definition.Range.StartLine,
            definition.Range.StartCharacter));
        Assert.DoesNotContain(
            index.Symbols,
            symbol => symbol.Id.Contains(_root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            index.Symbols,
            symbol => symbol.DisplayName == "PairAlias" &&
                      symbol.Id.Contains("Aliases.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linked_source_in_multiple_projects_is_indexed_once()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        const string source = "namespace Shared; public sealed class Linked { }";
        var sharedPath = Path.Combine(_root, "Shared", "Linked.cs");
        var first = AddProject(workspace.CurrentSolution, "First");
        var solution = first.Solution.AddDocument(
            DocumentId.CreateNewId(first.Id),
            "Linked.cs",
            SourceText.From(source),
            filePath: sharedPath);
        var second = AddProject(solution, "Second");
        solution = second.Solution.AddDocument(
            DocumentId.CreateNewId(second.Id),
            "Linked.cs",
            SourceText.From(source),
            filePath: sharedPath);

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(index.Documents);
        Assert.Equal("Shared/Linked.cs", index.Documents[0].RelPath);
        Assert.Single(index.Occurrences, occurrence =>
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
            index.Symbols.Single(symbol => symbol.Id == occurrence.SymbolId).DisplayName == "Linked");
        index.NormalizeForUse();
    }

    private (Microsoft.CodeAnalysis.Solution Solution, string AppSource, string LibSource) Solution()
    {
        Directory.CreateDirectory(_root);
        var workspace = new AdhocWorkspace();
        var lib = AddProject(workspace.CurrentSolution, "Lib");
        const string libSource =
            """
            namespace Lib;
            public interface IWorker { int Work(int value); }
            public class Base
            {
                public virtual int Work(int value) => value;
                public int Work(string value) => value.Length;
            }
            """;
        var solution = lib.Solution.AddDocument(
            DocumentId.CreateNewId(lib.Id),
            "Base.cs",
            SourceText.From(libSource),
            filePath: Path.Combine(_root, "Lib", "Base.cs"));
        var app = AddProject(solution, "App");
        solution = app.Solution.AddProjectReference(app.Id, new ProjectReference(lib.Id));
        const string appSource =
            """
            using Lib;
            public class Worker : Base, IWorker
            {
                public override int Work(int value) => base.Work(value);
                int IWorker.Work(int value) => Work(value);
                public int Run() => Work(42) + Work("x");
            }
            """;
        solution = solution.AddDocument(
            DocumentId.CreateNewId(app.Id),
            "Worker.cs",
            SourceText.From(appSource),
            filePath: Path.Combine(_root, "App", "Worker.cs"));
        return (solution, appSource, libSource);
    }

    private static Project AddProject(Microsoft.CodeAnalysis.Solution solution, string name)
    {
        var id = ProjectId.CreateNewId(name);
        var info = ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: TrustedPlatformReferences());
        return solution.AddProject(info).GetProject(id)!;
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <summary>
    /// End-to-end and deliberately free of hand-made occurrences. The containment rule for
    /// column 0 shipped proven against a fixture that supplied enclosing ranges by hand,
    /// while the C# indexer produced none — so column 0 kept degrading on exactly the
    /// language the rule was written for (#184). This arrangement goes through the real
    /// indexer: if it stops emitting enclosing ranges for definitions, this fails.
    /// </summary>
    [Fact]
    public async Task Column_zero_on_a_single_line_signature_resolves_to_the_method()
    {
        Directory.CreateDirectory(_root);
        using var workspace = new AdhocWorkspace();
        var project = AddProject(workspace.CurrentSolution, "Signature");
        const string source =
            """
            namespace Demo;
            public sealed class Journal
            {
                private static bool IsRunAlive(string journalPath)
                {
                    return journalPath.Length > 0;
                }
            }
            """;
        var solution = project.Solution.AddDocument(
            DocumentId.CreateNewId(project.Id),
            "Journal.cs",
            SourceText.From(source),
            filePath: Path.Combine(_root, "Journal.cs"));

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);

        var method = index.Occurrences.Single(occurrence =>
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
            index.Symbols.Single(symbol => symbol.Id == occurrence.SymbolId)
                .DisplayName == "IsRunAlive");
        Assert.NotNull(method.EnclosingRange);

        var signatureLine = Position(source, "IsRunAlive").Line;
        var resolved = new SemanticNavigationService(index).ResolveOccurrence(
            "Journal.cs",
            signatureLine,
            0,
            new SemanticSnapshotIdentity("repo", "generation", "tree", null));

        Assert.NotNull(resolved);
        Assert.Equal(method.SymbolId, resolved.SymbolId);
    }

    private static SemanticIndexBuildIdentity Identity() =>
        new(
            "repo",
            "generation",
            "tree",
            null,
            "commit",
            new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));

    private static LinePosition Position(string source, string token)
    {
        var offset = source.IndexOf(token, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        return SourceText.From(source).Lines.GetLinePosition(offset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
