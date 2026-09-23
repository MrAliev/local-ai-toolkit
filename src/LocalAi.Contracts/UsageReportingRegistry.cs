namespace LocalAi.Contracts;

public enum UsageSurface { Mcp, Cli }
public enum UsageComparisonKind { ContextReduction, ReplacedGeneration, NotApplicable }
public enum UsageResponseShape { Text, Json, Envelope, LegacyJson }

/// <summary>Applicability is a policy, never a claim that any tokens have been saved.</summary>
public sealed record UsagePolicy(UsageComparisonKind Comparison, UsageResponseShape Response)
{
    public static UsagePolicy Generation { get; } = new(UsageComparisonKind.ReplacedGeneration, UsageResponseShape.Text);
    public static UsagePolicy Context { get; } = new(UsageComparisonKind.ContextReduction, UsageResponseShape.Text);
    public static UsagePolicy Administration { get; } = new(UsageComparisonKind.NotApplicable, UsageResponseShape.Text);
    public static UsagePolicy AdministrationJson { get; } = Administration with { Response = UsageResponseShape.Json };
}

public sealed record UsageOperation(UsageSurface Surface, string Component, string Operation, UsagePolicy Policy, string? McpTool = null)
{
    public string? Syntax { get; init; }
}

/// <summary>
/// Inventory of the two MCP servers and the localai/codesearch command surfaces, including
/// group entry points (invalid arguments or default operations). Options and positional values
/// are not separate operations. The launcher delegates to these binaries; broker serve is a
/// service lifetime, not a tool call. Source-dispatch and MCP reflection tests enforce coverage.
/// </summary>
public static class UsageReportingRegistry
{
    public static IReadOnlyList<UsageOperation> All { get; } = Array.AsReadOnly(Build()
        .Select(entry => entry.Surface == UsageSurface.Cli
            ? entry with { Syntax = CliCommandSyntax.For(entry.Component, entry.Operation) } : entry).ToArray());

    public static UsageOperation Find(UsageSurface surface, string component, string operation) =>
        All.SingleOrDefault(entry => entry.Surface == surface && entry.Component == component && entry.Operation == operation)
        ?? throw new InvalidOperationException($"No usage-reporting policy for {surface} '{component}/{operation}'.");

    public static void RequireCoverage(UsageSurface surface, string component, IEnumerable<string> operations)
    {
        foreach (var operation in operations) _ = Find(surface, component, operation);
    }

    public static UsageReport CreateReport(UsageSurface surface, string component, string operation, Guid callId) =>
        UsageReport.Create(Find(surface, component, operation), callId);

    public static IReadOnlyList<string> EnvelopeCommands(string binary) => Commands(binary, UsageResponseShape.Envelope);

    public static CapabilityData Capabilities(string binary)
    {
        var tools = All.Where(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary && entry.McpTool is not null)
            .ToDictionary(entry => entry.Operation, entry => entry.McpTool!, StringComparer.Ordinal);
        return CommandCapabilities.Describe(binary, EnvelopeCommands(binary), Commands(binary, UsageResponseShape.LegacyJson), tools);
    }

    private static string[] Commands(string binary, UsageResponseShape shape) => All
        .Where(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary && entry.Policy.Response == shape)
        .Select(entry => entry.Operation).ToArray();

    private static IEnumerable<UsageOperation> Build()
    {
        foreach (var component in new[] { "localai", "codesearch" })
            yield return new(UsageSurface.Cli, component, "unknown_command", UsagePolicy.Administration);
        foreach (var (server, policies) in new[] { ("codesearch", McpToolNames.CodeSearchPolicies), ("locallm", McpToolNames.LocalLmPolicies) })
            foreach (var policy in policies)
                yield return new(UsageSurface.Mcp, server, policy.Name, policy.Usage
                    ?? throw new InvalidOperationException($"MCP tool '{server}/{policy.Name}' has no usage-reporting policy."));

        // Preserve the order and shapes of the existing capabilities responses.
        foreach (var (command, comparison, tool) in new (string, UsageComparisonKind, string?)[]
        {
            ("repo status", UsageComparisonKind.NotApplicable, null), ("doctor", UsageComparisonKind.NotApplicable, null),
            ("diagnostics", UsageComparisonKind.NotApplicable, null),
            ("hardware plan", UsageComparisonKind.NotApplicable, null),
            ("offline bundle", UsageComparisonKind.NotApplicable, null),
            ("offline verify", UsageComparisonKind.NotApplicable, null),
            ("offline plan", UsageComparisonKind.NotApplicable, null),
            ("offline execute", UsageComparisonKind.NotApplicable, null),
            ("offline install-dependency", UsageComparisonKind.NotApplicable, null),
            ("offline import-models", UsageComparisonKind.NotApplicable, null),
            ("ask", UsageComparisonKind.ReplacedGeneration, "ask_local"), ("triage", UsageComparisonKind.ReplacedGeneration, "triage_log"),
            ("read-image", UsageComparisonKind.ReplacedGeneration, "read_image"), ("translate", UsageComparisonKind.ReplacedGeneration, "translate_local"),
            ("capabilities", UsageComparisonKind.NotApplicable, null), ("docs build", UsageComparisonKind.ReplacedGeneration, null),
            ("docs apply", UsageComparisonKind.NotApplicable, null),
            ("docs prune", UsageComparisonKind.NotApplicable, null), ("docs pin", UsageComparisonKind.NotApplicable, null),
            ("docs unpin", UsageComparisonKind.NotApplicable, null),
            ("semantic impact", UsageComparisonKind.ContextReduction, "analyze_impact"),
            ("documents index", UsageComparisonKind.NotApplicable, "index_documents"),
            ("documents search", UsageComparisonKind.ContextReduction, "search_documents"),
        }) yield return new(UsageSurface.Cli, "localai", command, new(comparison, UsageResponseShape.Envelope), tool);

        foreach (var (command, tool) in new (string, string?)[]
        {
            ("model status", "local_models_status"), ("model pull", null), ("model preflight", "local_model_preflight"),
        }) yield return new(UsageSurface.Cli, "localai", command, UsagePolicy.Administration with { Response = UsageResponseShape.LegacyJson }, tool);

        foreach (var command in new[]
        {
            "help", "--help", "-h", "model", "native", "docs", "documents", "hardware", "offline", "policy", "policy show", "policy set", "update", "semantic", "semantic status",
            "semantic config", "semantic config show", "semantic config path", "semantic config init",
            "semantic lsp-config", "semantic lsp-config show", "semantic lsp-config path", "semantic lsp-config init",
            "semantic fallback-config", "semantic fallback-config show", "semantic fallback-config path", "semantic fallback-config init",
            "semantic evaluate", "bootstrap", "sync", "agent gate", "agent worktree", "agent worktree create", "agent worktree remove",
            "repo prune", "hook", "hook post-commit", "hook post-checkout", "hook post-merge", "hook post-rewrite",
            "prune", "telemetry", "hooks install",
        }) yield return new(UsageSurface.Cli, "localai", command, UsagePolicy.Administration);

        foreach (var command in new[] { "definition", "references", "implementations", "relationships" })
            yield return new(UsageSurface.Cli, "localai", "semantic " + command, UsagePolicy.Context);

        // Explicit, not derived from the enum: a new native operation must be classified first.
        foreach (var command in new[] { "chat", "generate" })
            yield return new(UsageSurface.Cli, "localai", "native " + command, UsagePolicy.Generation with { Response = UsageResponseShape.Json });
        foreach (var command in new[] { "embed", "tags", "show", "processes" })
            yield return new(UsageSurface.Cli, "localai", "native " + command, UsagePolicy.AdministrationJson);

        foreach (var (command, policy, tool) in new (string, UsagePolicy, string?)[]
        {
            ("search", UsagePolicy.Context, "search_code"), ("get-chunk", UsagePolicy.Context, "get_code_chunk"),
            ("status", UsagePolicy.Administration, "index_status"), ("capabilities", UsagePolicy.Administration, null),
        }) yield return new(UsageSurface.Cli, "codesearch", command, policy with { Response = UsageResponseShape.Envelope }, tool);
        yield return new(UsageSurface.Cli, "codesearch", "evaluate", UsagePolicy.Administration with { Response = UsageResponseShape.LegacyJson });
        foreach (var command in new[] { "index", "overlay", "scan", "help", "--help", "-h" })
            yield return new(UsageSurface.Cli, "codesearch", command, UsagePolicy.Administration);
    }
}
