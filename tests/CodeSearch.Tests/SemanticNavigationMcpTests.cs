using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using System.IO.Pipelines;
using System.Text.Json;
using LocalAi.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeSearch.Tests;

public class SemanticNavigationMcpTests
{
    private static SemanticImpactExplanation NoExplanation() => new(() => throw new InvalidOperationException("must not start broker"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_sdk_binds_opt_in_explanations_and_rejects_unbound_model_claims(bool reject)
    {
        var broker = new SemanticImpactExplanationTests.ReplyBroker(payload => reject ? "{\"invented\":\"a confirmed bug\"}" :
            JsonSerializer.Serialize(payload.OutputSchema!.Fields.ToDictionary(field => field.Name, _ => "test-coverage")));
        var incoming = new Pipe(); var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(Gateway());
        builder.Services.AddSingleton(NoBuildRunning.Notice);
        builder.Services.AddSingleton(new SemanticImpactExplanation(() => broker));
        builder.Services.AddMcpServer().WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithToolsFromAssembly(typeof(CodeSearchTools).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var request = new CallToolRequestParams
            {
                Name = "analyze_impact", Arguments = new Dictionary<string, JsonElement>
                { ["path"] = JsonSerializer.SerializeToElement("Src/A.cs"), ["line"] = JsonSerializer.SerializeToElement(3) },
            };
            var plain = await client.CallToolAsync(request, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("explanation", string.Join("\n", plain.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
            Assert.Empty(broker.Requests);
            request.Arguments["explain"] = JsonSerializer.SerializeToElement(true);
            var result = await client.CallToolAsync(request, TestContext.Current.CancellationToken);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.Contains("<untrusted-content", text, StringComparison.Ordinal);
            Assert.Single(broker.Requests);
            if (reject)
            {
                Assert.StartsWith("result_rejected:", text, StringComparison.Ordinal);
                Assert.Contains("unexpected_field", text, StringComparison.Ordinal);
                Assert.DoesNotContain("a confirmed bug", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("\"questionVerification\":\"heuristic\"", text, StringComparison.Ordinal);
                Assert.Contains("\"evidenceCount\":2", text, StringComparison.Ordinal);
            }
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }
    [Fact]
    public async Task Impact_wraps_one_based_evidence_with_snapshot_and_hashes()
    {
        var response = await CodeSearchTools.AnalyzeImpact(Gateway(), NoBuildRunning.Notice, NoExplanation(), "Src/A.cs", 3,
            limit: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("<untrusted-content", response, StringComparison.Ordinal);
        Assert.Contains("origin=\"analyze_impact\"", response, StringComparison.Ordinal);
        using var json = System.Text.Json.JsonDocument.Parse(response[response.IndexOf('{')..(response.LastIndexOf('}') + 1)]);
        var data = json.RootElement;
        Assert.Equal(1, data.GetProperty("coordinateBase").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, data.GetProperty("totalEvidence").GetInt32());
        var edge = Assert.Single(data.GetProperty("evidence").EnumerateArray());
        Assert.Equal("implementation", edge.GetProperty("kind").GetString());
        Assert.Equal(4, edge.GetProperty("source").GetProperty("startLine").GetInt32());
        Assert.Equal(17, edge.GetProperty("source").GetProperty("startColumn").GetInt32());
        Assert.Equal(string.Concat(Enumerable.Repeat("03", 32)), edge.GetProperty("source").GetProperty("documentSha256").GetString());
        Assert.Equal("tree", data.GetProperty("snapshot").GetProperty("gitTree").GetString());
    }

    [Theory]
    [InlineData("../outside.cs", 1, 1, 10, "invalid_path:")]
    [InlineData("Src/A.cs", 0, 1, 10, "invalid_position:")]
    [InlineData("Src/A.cs", 1, 0, 10, "invalid_position:")]
    [InlineData("Src/A.cs", 1, 1, 0, "invalid_limit:")]
    [InlineData("Src/A.cs", 1, 1, 10001, "invalid_limit:")]
    public async Task Impact_rejects_invalid_inputs_before_loading(string path, int line, int column, int limit, string code)
    {
        var gateway = new SemanticNavigationGateway(_ => throw new InvalidOperationException("must not load"));
        Assert.StartsWith(code, await CodeSearchTools.AnalyzeImpact(gateway, NoBuildRunning.Notice, NoExplanation(), path, line, column, limit,
            cancellationToken: TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impact_cancellation_propagates_and_readiness_is_not_a_graph()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gateway = new SemanticNavigationGateway(_ => throw new SemanticNavigationNotReadyException("stale snapshot"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodeSearchTools.AnalyzeImpact(gateway, NoBuildRunning.Notice, NoExplanation(),
            "Src/A.cs", 3, cancellationToken: cancellation.Token));
        var refused = await CodeSearchTools.AnalyzeImpact(gateway, NoBuildRunning.Notice, NoExplanation(), "Src/A.cs", 3,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("semantic_navigation_not_ready:", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// The navigation tools blocked on an awaitable and carried no cancellation token, so the
    /// gateway was called with <see cref="CancellationToken.None"/> whatever the client sent: a
    /// call could not be cancelled at all (#395).
    ///
    /// The token here is cancelled before the call, so nothing waits for anything. What is under
    /// test is that the token is carried, never how quickly a cancellation lands — a test that
    /// timed a cancellation would be a statement about the machine.
    /// </summary>
    [Theory]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("implementations")]
    public async Task A_cancelled_navigation_call_stops(string operation)
    {
        var gateway = Gateway();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "definition" => CodeSearchTools.GoToDefinition(
                gateway, NoBuildRunning.Notice, "Src/Use.cs", 5, 13,
                cancellationToken: cancelled.Token),
            "references" => CodeSearchTools.FindReferences(
                gateway, NoBuildRunning.Notice, "Src/Use.cs", 5, 13,
                cancellationToken: cancelled.Token),
            "implementations" => CodeSearchTools.FindImplementations(
                gateway, NoBuildRunning.Notice, "Src/Use.cs", 5, 13,
                cancellationToken: cancelled.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
    }

    [Fact]
    public async Task Go_to_definition_wraps_each_source_derived_location()
    {
        var gateway = Gateway();

        var response = await CodeSearchTools.GoToDefinition(
            gateway,
            NoBuildRunning.Notice,
            "Src/Use.cs",
            line: 5,
            column: 13,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("Definitions: 1", response, StringComparison.Ordinal);
        Assert.Contains("<untrusted-content", response, StringComparison.Ordinal);
        Assert.Contains("origin=\"go_to_definition:Src/A.cs\"", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
        Assert.Contains(MethodId, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_references_can_exclude_the_definition()
    {
        var gateway = Gateway();

        var response = await CodeSearchTools.FindReferences(
            gateway,
            NoBuildRunning.Notice,
            "Src/Use.cs",
            line: 5,
            column: 13,
            includeDefinition: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("References: 1", response, StringComparison.Ordinal);
        Assert.Contains("Src/Use.cs:5:11-5:15", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_implementations_wraps_precise_relationship_results()
    {
        var response = await CodeSearchTools.FindImplementations(
            Gateway(),
            NoBuildRunning.Notice,
            "Src/A.cs",
            line: 3,
            column: 18,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("Implementations: 1", response, StringComparison.Ordinal);
        Assert.Contains(
            "origin=\"find_implementations:Src/Impl.cs\"",
            response,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Find_relationships_reports_kind_and_direction()
    {
        var response = CodeSearchTools.FindRelationships(
            Gateway(),
            NoBuildRunning.Notice,
            "Src/Impl.cs",
            line: 4,
            column: 18,
            direction: "outgoing",
            kind: "implementation");

        Assert.StartsWith("Relationships: 1", response, StringComparison.Ordinal);
        Assert.Contains("relationship: Implementation", response, StringComparison.Ordinal);
        Assert.Contains("direction: Outgoing", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Navigation_readiness_errors_remain_trusted_and_unwrapped()
    {
        var gateway = new SemanticNavigationGateway(
            _ => throw new SemanticNavigationNotReadyException("semantic.sidx is absent"));

        var response = await CodeSearchTools.GoToDefinition(
            gateway,
            NoBuildRunning.Notice,
            "Src/Use.cs",
            line: 5,
            column: 13,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("semantic_navigation_not_ready:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The promise the tool descriptions make, asserted rather than described: the start line a
    /// hit is printed with is the line that navigates, and no column has to be worked out.
    ///
    /// `search_code` prints one-based lines and the navigation tools took zero-based ones, with
    /// nothing converting between them. Pasting a printed line landed one line into the body —
    /// a refusal when nothing was there, and a confident `Precise` answer about the wrong symbol
    /// when an identifier happened to be.
    /// </summary>
    [Fact]
    public async Task A_hit_navigates_at_the_line_it_is_printed_with()
    {
        // What search_code would print for the declaration this fixture puts at zero-based 2.
        const int printedStartLine = 3;

        var response = await CodeSearchTools.GoToDefinition(
            Gateway(),
            NoBuildRunning.Notice,
            "Src/A.cs",
            line: printedStartLine,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("Definitions: 1", response, StringComparison.Ordinal);
        Assert.Contains("Src/A.cs:3:17-3:19", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero can only be a position somebody counted from zero, so it is refused by name rather
    /// than passed to a core that would answer about the line above.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task A_position_counted_from_zero_is_refused_by_name(int line, int column)
    {
        var response = await CodeSearchTools.GoToDefinition(
            Gateway(), NoBuildRunning.Notice, "Src/A.cs", line, column,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("invalid_position:", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path search_code would never have printed is refused by name, the way a zero position
    /// is. Passed on, it came back as the core's ArgumentException — English, with a framework
    /// parameter suffix glued on — inside an answer that otherwise follows the reader.
    /// </summary>
    [Theory]
    [InlineData(@"R:\LocalAi\Src\A.cs")]
    [InlineData("/Src/A.cs")]
    [InlineData("../Src/A.cs")]
    [InlineData("Src/./A.cs")]
    public async Task A_path_that_is_not_repository_relative_is_refused_by_name(string path)
    {
        var response = await CodeSearchTools.GoToDefinition(
            Gateway(), NoBuildRunning.Notice, path, line: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("invalid_path:", response, StringComparison.Ordinal);
        Assert.Contains(path, response, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", response, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_navigation_tool_refuses_such_a_path_with_the_same_words()
    {
        const string path = @"R:\LocalAi\Src\A.cs";
        var gateway = Gateway();
        var expected = await CodeSearchTools.GoToDefinition(
            gateway, NoBuildRunning.Notice, path, line: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(
            new[]
            {
                await CodeSearchTools.FindReferences(gateway, NoBuildRunning.Notice, path, line: 5, cancellationToken: TestContext.Current.CancellationToken),
                await CodeSearchTools.FindImplementations(
                    gateway, NoBuildRunning.Notice, path, line: 3,
                    cancellationToken: TestContext.Current.CancellationToken),
                CodeSearchTools.FindRelationships(gateway, NoBuildRunning.Notice, path, line: 4),
            },
            answer => Assert.Equal(expected, answer));
    }

    private const string MethodId = "scip-dotnet pkg MyApp 1.0.0 A#Go().";
    private const string ImplementationId = "scip-dotnet pkg MyApp 1.0.0 Impl#Go().";

    private static SemanticNavigationGateway Gateway()
    {
        var index = new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                Document("Src/A.cs", 1),
                Document("Src/Impl.cs", 3),
                Document("Src/Use.cs", 2),
            ],
            Symbols =
            [
                new SemanticSymbol
                {
                    Id = MethodId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void A.Go()",
                },
                new SemanticSymbol
                {
                    Id = ImplementationId,
                    DisplayName = "Go",
                    Kind = SemanticSymbolKind.Method,
                    Signature = "void Impl.Go()",
                },
            ],
            Occurrences =
            [
                Occurrence("Src/A.cs", new SourceRange(2, 16, 2, 18),
                    SemanticOccurrenceRoles.Definition),
                Occurrence("Src/Use.cs", new SourceRange(4, 10, 4, 14),
                    SemanticOccurrenceRoles.Reference | SemanticOccurrenceRoles.Read),
                new SemanticOccurrence
                {
                    DocumentPath = "Src/Impl.cs",
                    Range = new SourceRange(3, 16, 3, 18),
                    SymbolId = ImplementationId,
                    Roles = SemanticOccurrenceRoles.Definition,
                    Precision = NavigationPrecision.Precise,
                },
            ],
            Relationships =
            [
                new SemanticRelationship
                {
                    SourceSymbolId = ImplementationId,
                    TargetSymbolId = MethodId,
                    Kind = SemanticRelationshipKind.Implementation,
                },
            ],
        };
        var snapshot = new SemanticSnapshotIdentity("repository", "generation", "tree", null);
        return new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(new SemanticNavigationService(index), snapshot));
    }

    private static SemanticDocument Document(string path, byte hashByte) =>
        new()
        {
            RelPath = path,
            Hash = Enumerable.Repeat(hashByte, 32).ToArray(),
        };

    private static SemanticOccurrence Occurrence(
        string path,
        SourceRange range,
        SemanticOccurrenceRoles roles) =>
        new()
        {
            DocumentPath = path,
            Range = range,
            SymbolId = MethodId,
            Roles = roles,
            Precision = NavigationPrecision.Precise,
        };
}
