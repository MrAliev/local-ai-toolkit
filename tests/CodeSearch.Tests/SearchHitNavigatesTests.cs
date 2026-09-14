using CodeSearch.Core.Chunking;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeSearch.Tests;

/// <summary>
/// The one test that composes the two ends: a line produced by the chunker, handed to navigation
/// unchanged.
///
/// #302 was a numbering disagreement between them, and it survived a documented promise, a release
/// note and four tool descriptions, because every test asserted one end against its own
/// convention. `A_hit_navigates_at_the_line_it_is_printed_with` states the promise but reads the
/// same fixture constant on both sides, so it cannot catch a drift: both numbers come from the
/// same place.
///
/// Here they do not. The line comes from <see cref="RoslynChunker"/>, which is what `search_code`
/// prints, and the symbol comes from <see cref="RoslynSemanticIndexer"/> over the same source
/// text. Nothing but the file is shared, so the two have to agree about what line 12 means.
/// </summary>
public sealed class SearchHitNavigatesTests : IDisposable
{
    private const string RelativePath = "Work.cs";

    private const string Source =
        """
        namespace Fixture;

        public sealed class Machine
        {
            public int Idle() => 0;

            public int Work(int value)
            {
                return value + 1;
            }
        }
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-hit-navigates-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_hit_navigates_to_the_symbol_it_named()
    {
        var chunk = Chunks().Single(item =>
            item.Symbol.EndsWith("Work", StringComparison.Ordinal));
        var gateway = await GatewayAsync();

        // Exactly what an agent does with a hit: the start line as printed, column 1.
        var response = await CodeSearchTools.GoToDefinition(
            gateway, NoBuildRunning.Notice, RelativePath, chunk.StartLine,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Work", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Idle", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attributed_method_navigates_from_the_unchanged_search_hit()
    {
        var source = Source.Replace("public int Work", "[System.Obsolete]\n    [System.Diagnostics.DebuggerStepThrough]\n    public int Work", StringComparison.Ordinal);
        var chunk = Chunks(source).Single(item => item.Symbol.EndsWith("Work", StringComparison.Ordinal));
        Assert.Contains("[System.Obsolete]", source.Split('\n')[chunk.StartLine - 1], StringComparison.Ordinal);
        var gateway = await GatewayAsync(source);

        var response = await CodeSearchTools.GoToDefinition(
            gateway, NoBuildRunning.Notice, RelativePath, chunk.StartLine,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("symbol: dotnet M:Fixture.Machine.Work(System.Int32)", response, StringComparison.Ordinal);
        Assert.Contains("precision: Precise", response, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic_navigation_degraded", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the neighbouring member is not what answers. Without this, a navigation that resolved
    /// to the nearest declaration in either direction would pass the test above on a file where
    /// every line is close to something.
    /// </summary>
    [Fact]
    public async Task A_hit_on_one_member_does_not_navigate_to_its_neighbour()
    {
        var idle = Chunks().Single(item =>
            item.Symbol.EndsWith("Idle", StringComparison.Ordinal));
        var gateway = await GatewayAsync();

        var response = await CodeSearchTools.GoToDefinition(
            gateway, NoBuildRunning.Notice, RelativePath, idle.StartLine,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Idle", response, StringComparison.Ordinal);
    }

    /// <summary>The lines `search_code` prints, produced by the chunker from the file itself.</summary>
    private IReadOnlyList<Chunk> Chunks(string source = Source)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, RelativePath);
        File.WriteAllText(path, source);
        return new RoslynChunker().Split(RelativePath, source).ToArray();
    }

    /// <summary>
    /// Navigation over the same text, built by Roslyn rather than by the chunker — which is what
    /// makes the two numbers independent.
    /// </summary>
    private async Task<SemanticNavigationGateway> GatewayAsync(string source = Source)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, RelativePath);
        File.WriteAllText(path, source);

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Fixture");
        var project = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Fixture",
            "Fixture",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: TrustedPlatformReferences());
        var solution = workspace.CurrentSolution
            .AddProject(project)
            .AddDocument(DocumentId.CreateNewId(projectId), RelativePath, source, filePath: path);

        var index = await new RoslynSemanticIndexer().BuildAsync(
            solution,
            _root,
            new SemanticIndexBuildIdentity(
                "repository",
                "generation",
                "tree",
                null,
                "commit",
                DateTime.UnixEpoch),
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = new SemanticSnapshotIdentity("repository", "generation", "tree", null);
        return new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(new SemanticNavigationService(index), snapshot));
    }

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
