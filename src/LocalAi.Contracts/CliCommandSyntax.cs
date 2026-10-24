namespace LocalAi.Contracts;

/// <summary>Syntax attached to the same descriptions that drive capabilities and usage policy.</summary>
internal static class CliCommandSyntax
{
    public static string? For(string binary, string command) => binary switch
    {
        "localai" => LocalAi.GetValueOrDefault(command),
        "codesearch" => CodeSearch.GetValueOrDefault(command),
        _ => null,
    };

    private static readonly Dictionary<string, string> LocalAi = new(StringComparer.Ordinal)
    {
        ["model status"] = LocalAiCommandSyntax.ModelStatus,
        ["model pull"] = LocalAiCommandSyntax.ModelPull,
        ["model preflight"] = LocalAiCommandSyntax.ModelPreflight,
        ["repo status"] = LocalAiCommandSyntax.RepoStatus,
        ["repo prune"] = LocalAiCommandSyntax.RepoPrune,
        ["doctor"] = LocalAiCommandSyntax.Doctor,
        ["diagnostics"] = LocalAiCommandSyntax.Diagnostics,
        ["hardware plan"] = LocalAiCommandSyntax.HardwarePlan,
        ["offline bundle"] = LocalAiCommandSyntax.OfflineBundle,
        ["offline verify"] = LocalAiCommandSyntax.OfflineVerify,
        ["offline plan"] = LocalAiCommandSyntax.OfflinePlan,
        ["offline install-npm"] = LocalAiCommandSyntax.OfflineInstallNpm,
        ["offline execute"] = LocalAiCommandSyntax.OfflineExecute,
        ["offline install-dependency"] = LocalAiCommandSyntax.OfflineInstallDependency,
        ["offline import-models"] = LocalAiCommandSyntax.OfflineImportModels,
        ["ask"] = LocalAiCommandSyntax.Ask,
        ["triage"] = LocalAiCommandSyntax.Triage,
        ["read-image"] = LocalAiCommandSyntax.ReadImage,
        ["translate"] = LocalAiCommandSyntax.Translate,
        ["capabilities"] = LocalAiCommandSyntax.Capabilities,
        ["docs build"] = LocalAiCommandSyntax.DocsBuild,
        ["documents index"] = LocalAiCommandSyntax.DocumentsIndex,
        ["documents search"] = LocalAiCommandSyntax.DocumentsSearch,
        ["docs apply"] = LocalAiCommandSyntax.DocsApply,
        ["docs prune"] = LocalAiCommandSyntax.DocsPrune,
        ["docs pin"] = LocalAiCommandSyntax.DocsPin,
        ["docs unpin"] = LocalAiCommandSyntax.DocsUnpin,
        ["agent gate"] = LocalAiCommandSyntax.AgentGate,
        ["agent worktree create"] = LocalAiCommandSyntax.AgentWorktreeCreate,
        ["agent worktree remove"] = LocalAiCommandSyntax.AgentWorktreeRemove,
        ["hook"] = LocalAiCommandSyntax.Hook,
        ["prune"] = LocalAiCommandSyntax.Prune,
        ["telemetry"] = LocalAiCommandSyntax.Telemetry,
        ["native"] = "localai native <operation> [--request file]",
        ["policy"] = "localai policy [show|set <option> <value>]",
        ["update"] = "localai update [--wait] [--force]",
        ["semantic"] = "localai semantic [<operation> [options]]",
        ["semantic impact"] = LocalAiCommandSyntax.SemanticImpact,
        ["bootstrap"] = "localai bootstrap --dry-run",
        ["sync"] = "localai sync [--root dir] [--base-only] [--all-worktrees] [--require-semantics] [--max-inline-files n]",
        ["sync cancel"] = "localai sync cancel --root dir --operation operation-id",
        ["hooks install"] = "localai hooks install [--root dir]",
    };

    private static readonly Dictionary<string, string> CodeSearch = new(StringComparer.Ordinal)
    {
        ["index"] = "codesearch index [--root <dir>] [--model <ollama-model>] [--force] [--index <file>]",
        ["overlay"] = "codesearch overlay [--root <dir>] [--index <base>] [--overlay <file>]",
        ["search"] = "codesearch search --query <text> [--root <dir>] [--top N] [--kind Type|Method|Text|File] [--path <substring>] [--per-file N] [--no-instruct] [--json]",
        ["get-chunk"] = "codesearch get-chunk --id <chunk_id> [--root <dir>] [--json]",
        ["evaluate"] = "codesearch evaluate --cases <json> [--root <dir>] [--profile|--no-floor] [--no-instruct]",
        ["status"] = "codesearch status [--root <dir>] [--json]",
        ["scan"] = "codesearch scan [--root <dir>]",
        ["capabilities"] = "codesearch capabilities --json",
    };
}
