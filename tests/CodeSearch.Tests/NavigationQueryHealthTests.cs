using CodeSearch.Core.Resources;
using CodeSearch.Core.Search;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;

namespace CodeSearch.Tests;

public sealed class NavigationQueryHealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Header_evidence_does_not_establish_query_readiness_or_coverage()
    {
        var health = await NavigationQueryHealth.ProbeAsync(Status(), null, "Def.cs", 1, 1, "definition", cancellationToken: Ct);
        Assert.Equal("HeaderOnly", health.HeaderEvidence);
        Assert.Equal("Current", health.Freshness);
        Assert.Equal("CSharp", health.Language);
        Assert.Equal("Unknown", health.Coverage);
        Assert.Equal("Unknown", health.Precision);
        Assert.Null(health.ResultCount);
        Assert.Equal(IndexText.NavigationProbeUnavailable, health.Reason);
    }

    [Fact]
    public async Task One_precise_query_does_not_claim_corpus_coverage_or_erase_staleness()
    {
        var calls = 0;
        var gateway = Gateway(() => calls++);
        var health = await NavigationQueryHealth.ProbeAsync(Status() with { CurrentCommit = "new-tree" },
            gateway, "Def.cs", 1, 1, "definition", cancellationToken: Ct);
        Assert.Equal(1, calls);
        Assert.Equal("HeaderOnly", health.HeaderEvidence);
        Assert.Equal("Stale", health.Freshness);
        Assert.Equal("Unknown", health.Coverage);
        Assert.Equal("Precise", health.Precision);
        Assert.Equal(1, health.ResultCount);
        Assert.Null(health.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task References_preserve_heuristic_or_unknown_precision_without_claiming_absence(bool hasFallback)
    {
        var calls = 0;
        var health = await NavigationQueryHealth.ProbeAsync(Status(), Gateway(() => calls++, hasFallback),
            "Def.cs", 1, 1, "references", cancellationToken: Ct);
        Assert.Equal(1, calls);
        Assert.Equal(hasFallback ? "Heuristic" : "Unknown", health.Precision);
        Assert.Equal(hasFallback ? 1 : 0, health.ResultCount);
        Assert.Equal("Unknown", health.Coverage);
        Assert.Equal(IndexText.NoPreciseReferencesForQuery, health.Reason);
    }

    [Fact]
    public async Task Unknown_header_and_unavailable_query_stay_separate()
    {
        var calls = 0;
        var gateway = new SemanticNavigationGateway(_ =>
        {
            calls++;
            throw new SemanticNavigationNotReadyException("test language coverage is unavailable");
        });
        var health = await NavigationQueryHealth.ProbeAsync(Status() with { SemanticIndexHeaderReadable = false },
            gateway, "Use.unknown", 1, 1, "definition", cancellationToken: Ct);
        Assert.Equal(1, calls);
        Assert.Equal("Unknown", health.HeaderEvidence);
        Assert.Equal("Unknown", health.Language);
        Assert.Equal("Unknown", health.Coverage);
        Assert.Equal("Unknown", health.Precision);
        Assert.Null(health.ResultCount);
        Assert.Equal("test language coverage is unavailable", health.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_context_loading_does_not_publish_query_evidence(bool unavailable)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = 0;
        var gateway = unavailable
            ? new SemanticNavigationGateway(_ =>
            {
                calls++;
                stop.Cancel();
                throw new SemanticNavigationNotReadyException("query became unavailable");
            })
            : Gateway(() =>
            {
                calls++;
                stop.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NavigationQueryHealth.ProbeAsync(
            Status(), gateway, "Def.cs", 1, 1, "definition", cancellationToken: stop.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null, 2, 1, "definition")]
    [InlineData("../outside.cs", 1, 1, "definition")]
    [InlineData("Def.cs", 0, 1, "definition")]
    [InlineData("Def.cs", 1, 0, "definition")]
    [InlineData("Def.cs", 1, 1, "all")]
    public void Invalid_queries_are_rejected_before_repository_or_gateway_access(string? path, int line, int column, string operation)
    {
        var response = CodeSearchTools.IndexStatus(null!, null!, navigationPath: path,
            navigationLine: line, navigationColumn: column, navigationOperation: operation, cancellationToken: Ct);
        Assert.Equal(CodeSearchText.NavigationProbeParametersInvalid, response);
    }

    [Fact]
    public void Runtime_evidence_mode_rejects_a_query_without_accessing_repository_services()
    {
        var response = CodeSearchTools.IndexStatus(null!, null!, runtimeEvidenceOnly: true,
            navigationPath: "Def.cs", cancellationToken: Ct);
        Assert.Equal(CodeSearchText.NavigationProbeRuntimeEvidenceConflict, response);
    }

    private static IndexStatus Status() => new(
        "root", "root", "index", true, "model", 3, 1, 1, 4,
        "tree", "tree", DateTime.UnixEpoch, "root", false, default!,
        SemanticIndexPresent: true) { SemanticIndexHeaderReadable = true };

    private static SemanticNavigationGateway Gateway(Action onContext, bool hasFallback = false)
    {
        var index = new SemanticIndex
        {
            RepositoryId = "repository", GenerationId = "generation", GitTree = "tree", DirtyHash = null,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [new SemanticDocument { RelPath = "Def.cs", Hash = new byte[32] }],
            Symbols = [new SemanticSymbol { Id = "symbol", DisplayName = "Run", Kind = SemanticSymbolKind.Method }],
            Occurrences = [new SemanticOccurrence
            {
                DocumentPath = "Def.cs", Range = new SourceRange(0, 0, 0, 3), SymbolId = "symbol",
                Roles = SemanticOccurrenceRoles.Definition, Precision = NavigationPrecision.Precise
            }],
            Relationships = []
        };
        return new SemanticNavigationGateway(_ =>
        {
            onContext();
            return new SemanticNavigationContext(new SemanticNavigationService(index),
                new SemanticSnapshotIdentity("repository", "generation", "tree", null));
        }, heuristicNavigation: new Fallback(hasFallback));
    }

    private sealed class Fallback(bool hasLocation) : IHeuristicSemanticNavigation
    {
        public Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(string root, string path, int line,
            int column, CancellationToken cancellationToken) => Result();
        public Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(string root, string path, int line,
            int column, bool includeDefinition, CancellationToken cancellationToken) => Result();
        private Task<IReadOnlyList<SemanticLocation>> Result() => Task.FromResult<IReadOnlyList<SemanticLocation>>(
            hasLocation ? [new SemanticLocation("Use.cs", new SourceRange(0, 0, 0, 3), "heuristic",
                SemanticOccurrenceRoles.Reference, NavigationPrecision.Heuristic)] : []);
    }
}
