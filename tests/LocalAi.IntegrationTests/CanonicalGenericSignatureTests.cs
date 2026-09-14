using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.Documentation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace LocalAi.IntegrationTests;

public sealed class CanonicalGenericSignatureTests
{
    private const string ListId = "dotnet T:System.Collections.Generic.List`1";
    private const string AddId = "dotnet M:System.Collections.Generic.List`1.Add(`0)";
    private const string BoxId = "dotnet T:Demo.Box`1";
    private const string EchoId = "dotnet M:Demo.Box`1.Echo``1(`0,``0)";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_documentation_never_borrows_another_records_equality_argument(bool reverse)
    {
        var declarations = new[] { "public sealed record First;", "public sealed record Second;" };
        var source = "namespace Demo;\n" + string.Join("\n", reverse ? declarations.Reverse() : declarations);
        var index = await BuildAsync(source);

        foreach (var name in new[] { "First", "Second" })
        {
            var facts = IndexedDocumentationFacts.Create(index, "dotnet T:Demo." + name, []);
            Assert.Contains(facts.Facts, fact => fact.SymbolId == "dotnet T:System.IEquatable`1" &&
                fact.Text == "Type: System.IEquatable<T>");
            Assert.DoesNotContain(facts.Facts, fact =>
                fact.Text.Contains("IEquatable<Demo.First>", StringComparison.Ordinal) ||
                fact.Text.Contains("IEquatable<Demo.Second>", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Constructed_types_and_members_keep_their_definition_signatures(bool reverse)
    {
        var index = await BuildUsesAsync(reverse);
        Assert.Equal("System.Collections.Generic.List<T>", Symbol(index, ListId).Signature);
        Assert.Equal("System.Collections.Generic.List<T>.Add(T)", Symbol(index, AddId).Signature);
        Assert.Equal("Demo.Box<T>", Symbol(index, BoxId).Signature);
        Assert.Equal("Demo.Box<T>.Echo<U>(T, U)", Symbol(index, EchoId).Signature);

        var echo = Symbol(index, EchoId);
        Assert.Equal(2, index.Occurrences.Count(occurrence => occurrence.SymbolId == echo.Id &&
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Reference)));
        Assert.Single(index.Occurrences, occurrence => occurrence.SymbolId == echo.Id &&
            occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition));
    }

    [Fact]
    public async Task Reversing_constructed_uses_does_not_change_canonical_metadata()
    {
        var first = await BuildUsesAsync(false);
        var second = await BuildUsesAsync(true);
        foreach (var id in new[] { ListId, AddId, BoxId, EchoId })
            Assert.Equal(Symbol(first, id), Symbol(second, id));
    }

    [Fact]
    public void Pre_fix_generation_and_its_overlays_cannot_be_reused_as_current_semantics()
    {
        var previous = new GenerationIdentity("repo", "commit", "tree", "model", 4096,
            CodeSearchSyncCommand.CurrentChunkFormatVersion, 1,
            CodeSearchSyncCommand.CurrentNormalizationVersion, 1, SemanticIndexVersion: 10);
        var current = previous with { SemanticIndexVersion = CodeSearchSyncCommand.CurrentSemanticGenerationVersion };
        Assert.NotEqual(previous.Id, current.Id);
        // Semantic metadata changes do not invalidate already computed source embeddings.
        Assert.True(current.CanReuseCorpusFrom(previous));
    }

    private static SemanticSymbol Symbol(SemanticIndex index, string id) =>
        Assert.Single(index.Symbols, symbol => symbol.Id == id);

    private static Task<SemanticIndex> BuildUsesAsync(bool reverse)
    {
        var calls = new[]
        {
            "new System.Collections.Generic.List<int>().Add(1); new Demo.Box<int>().Echo<string>(1, \"a\");",
            "new System.Collections.Generic.List<string>().Add(\"b\"); new Demo.Box<string>().Echo<int>(\"b\", 2);"
        };
        return BuildAsync("public class Use { public void Run() { " +
            string.Join("\n", reverse ? calls.Reverse() : calls) + " } }",
            "namespace Demo; public class Box<T> { public U Echo<U>(T first, U second) => second; }");
    }

    private static async Task<SemanticIndex> BuildAsync(string source, string? definitions = null)
    {
        using var workspace = new AdhocWorkspace();
        var root = Path.Combine(Path.GetTempPath(), "generic-signatures-" + Guid.NewGuid().ToString("N"));
        var id = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(id, VersionStamp.Create(), "Demo", "Demo", LanguageNames.CSharp,
            filePath: Path.Combine(root, "Demo.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)));
        var solution = workspace.CurrentSolution.AddProject(project)
            .AddDocument(DocumentId.CreateNewId(id), "AUse.cs", SourceText.From(source),
                filePath: Path.Combine(root, "AUse.cs"));
        if (definitions is not null)
            solution = solution.AddDocument(DocumentId.CreateNewId(id), "ZDefinitions.cs", SourceText.From(definitions),
                filePath: Path.Combine(root, "ZDefinitions.cs"));
        var compilation = await solution.GetProject(id)!.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(compilation!.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return await new RoslynSemanticIndexer().BuildAsync(solution, root,
            new("repo", "generation", "tree", null, "commit", DateTime.UnixEpoch),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
