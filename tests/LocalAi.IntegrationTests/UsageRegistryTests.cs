using LocalAi.Contracts;
using LocalAi.Repository;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalAi.IntegrationTests;

public sealed class UsageRegistryTests
{
    [Fact]
    public void Actual_cli_dispatch_routes_have_explicit_policies()
    {
        // Read syntax, not usage prose: adding a dispatch branch must fail even when its author
        // forgets both the registry and the help text. No CLI command is executed by this test.
        var localai = Source("LocalAi.Cli/Program.cs");
        var routes = ListRoutes(localai).ToHashSet(StringComparer.Ordinal);
        Add(routes, "policy", ListRoutes(Source("LocalAi.Cli/PolicyCommand.cs")));
        Add(routes, "agent worktree", ListRoutes(Source("LocalAi.Cli/AgentWorktreeCommand.cs")));
        Add(routes, "model", SwitchRoutes(Source("LocalAi.Cli/ModelCommand.cs"), "request.Operation"));
        Add(routes, "native", Enum.GetNames<NativeOllamaOperation>().Select(name => name.ToLowerInvariant()));
        Add(routes, "docs", OperationTests(Source("LocalAi.Cli/DocumentationCommand.cs")));
        var semantic = Source("LocalAi.Cli/SemanticNavigationCommand.cs");
        var execute = semantic.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == "Execute");
        Add(routes, "semantic", OperationTests(execute));
        var navigation = semantic.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(variable => variable.Identifier.Text == "NavigationOperations");
        Add(routes, "semantic", Strings(navigation));
        foreach (var (method, prefix) in new[] { ("Configure", "config"), ("ConfigureLanguageServers", "lsp-config"), ("ConfigureFallback", "fallback-config") })
        {
            var body = semantic.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == method);
            Add(routes, "semantic " + prefix, OperationTests(body));
        }
        Assert.NotEmpty(routes);
        UsageReportingRegistry.RequireCoverage(UsageSurface.Cli, "localai", routes);
        var searchRoutes = SwitchRoutes(Source("CodeSearch.Cli/Program.cs"), "command").ToArray();
        Assert.NotEmpty(searchRoutes);
        UsageReportingRegistry.RequireCoverage(UsageSurface.Cli, "codesearch", searchRoutes);
    }

    [Fact]
    public void A_new_dispatch_route_cannot_inherit_a_group_policy_silently()
    {
        var syntax = CSharpSyntaxTree.ParseText("if (args is [\"model\", \"new-operation\", ..]) return 0;",
            cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UsageReportingRegistry.RequireCoverage(UsageSurface.Cli, "localai", ListRoutes(syntax)));
        Assert.Contains("localai/model new-operation", exception.Message);
    }

    [Fact]
    public void Names_are_unique_and_mcp_entries_come_from_the_installer_inventory()
    {
        var entries = UsageReportingRegistry.All;
        Assert.Equal(entries.Count, entries.Select(entry => (entry.Surface, entry.Component, entry.Operation)).Distinct().Count());
        Assert.Equal(McpToolNames.LocalLm.Count + McpToolNames.CodeSearch.Count, entries.Count(entry => entry.Surface == UsageSurface.Mcp));
        Assert.All(entries, entry => Assert.NotNull(entry.Policy));
        Assert.Throws<InvalidOperationException>(() => UsageReportingRegistry.RequireCoverage(UsageSurface.Mcp, "locallm", ["future_tool"]));
    }

    [Fact]
    public void Unknown_operations_cannot_create_a_plausible_savings_report()
    {
        Assert.Throws<InvalidOperationException>(() => UsageReportingRegistry.CreateReport(UsageSurface.Cli, "localai", "made-up", Guid.NewGuid()));
    }

    [Fact]
    public void Every_git_hook_event_has_its_own_policy()
    {
        UsageReportingRegistry.RequireCoverage(UsageSurface.Cli, "localai",
            GitHookLayout.Events.Select(name => "hook " + name));
    }

    [Theory]
    [InlineData("localai", "repo status:schema,doctor:schema,diagnostics:schema,hardware plan:schema,offline bundle:schema,offline verify:schema,offline plan:schema,offline install-npm:schema,offline execute:schema,offline install-dependency:schema,offline import-models:schema,ask:schema:ask_local,triage:schema:triage_log,read-image:schema:read_image,translate:schema:translate_local,capabilities:schema,docs build:schema,docs apply:schema,docs prune:schema,docs pin:schema,docs unpin:schema,semantic impact:schema:analyze_impact,documents index:schema:index_documents,documents search:schema:search_documents,model status:schemaVersion:local_models_status,model pull:schemaVersion,model preflight:schemaVersion:local_model_preflight")]
    [InlineData("codesearch", "search:schema:search_code,get-chunk:schema:get_code_chunk,status:schema:index_status,capabilities:schema,evaluate:schemaVersion")]
    public void Existing_capability_names_order_shapes_and_correspondence_are_preserved(string binary, string expected)
    {
        var actual = UsageReportingRegistry.Capabilities(binary).Commands
            .Select(command => command.Name + ":" + command.Shape + (command.Tool is null ? "" : ":" + command.Tool));
        Assert.Equal(expected, string.Join(',', actual));
    }

    [Theory]
    [InlineData("localai")]
    [InlineData("codesearch")]
    public void Every_machine_command_has_syntax_in_the_shared_description(string binary)
    {
        foreach (var capability in UsageReportingRegistry.Capabilities(binary).Commands)
        {
            var description = UsageReportingRegistry.Find(UsageSurface.Cli, binary, capability.Name);
            Assert.NotNull(description.Syntax);
            Assert.Contains(description.Syntax, CliHelp.Render(binary));
        }
    }

    [Theory]
    [InlineData("localai", "ask question --model --help")]
    [InlineData("localai", "translate --in --help")]
    [InlineData("codesearch", "search --query --help")]
    [InlineData("codesearch", "search --query help")]
    public void Help_words_in_option_values_are_not_help_requests(string binary, string arguments)
    {
        Assert.Null(CliHelp.Resolve(binary, arguments.Split(' ')));
    }

    private static void Add(HashSet<string> routes, string prefix, IEnumerable<string> operations)
    {
        foreach (var operation in operations) routes.Add(prefix + " " + operation);
    }

    private static IEnumerable<string> ListRoutes(SyntaxNode source) => source.DescendantNodes()
        .OfType<ListPatternSyntax>()
        .Select(pattern => string.Join(" ", pattern.Patterns.TakeWhile(node => node is ConstantPatternSyntax constant
                && constant.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                && !literal.Token.ValueText.StartsWith('-'))
            .SelectMany(Strings)))
        .Where(route => route.Length > 0).Distinct();

    private static IEnumerable<string> SwitchRoutes(SyntaxNode source, string expression) => source.DescendantNodes()
        .OfType<SwitchExpressionSyntax>().Where(node => node.GoverningExpression.ToString() == expression)
        .SelectMany(node => node.Arms.SelectMany(arm => Strings(arm.Pattern)));

    private static IEnumerable<string> OperationTests(SyntaxNode source)
    {
        foreach (var node in source.DescendantNodes())
        {
            if (node is IsPatternExpressionSyntax pattern && pattern.Expression.ToString() == "operation")
                foreach (var value in Strings(pattern.Pattern)) yield return value;
            if (node is BinaryExpressionSyntax binary && binary.Left.ToString() == "operation")
                foreach (var value in Strings(binary.Right)) yield return value;
            if (node is InvocationExpressionSyntax call && call.Expression.ToString() == "string.Equals"
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == "operation")
                foreach (var value in Strings(call.ArgumentList.Arguments[1])) yield return value;
        }
        foreach (var value in SwitchRoutes(source, "operation")) yield return value;
    }

    private static IEnumerable<string> Strings(SyntaxNode node) => node.DescendantNodesAndSelf()
        .OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
        .Select(literal => literal.Token.ValueText);

    private static SyntaxNode Source(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(directory.FullName, "src", relativePath)),
            cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
    }
}
