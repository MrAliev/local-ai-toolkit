using LocalAi.Contracts;

namespace LocalAi.Cli;

internal static class CliUsage
{
    public const string ModelStatus = LocalAiCommandSyntax.ModelStatus;
    public const string ModelPull = LocalAiCommandSyntax.ModelPull;
    public const string ModelPreflight = LocalAiCommandSyntax.ModelPreflight;
    public const string Hook = LocalAiCommandSyntax.Hook;
    public const string HookEvents = LocalAiCommandSyntax.HookEvents;
    public const string RepoStatus = LocalAiCommandSyntax.RepoStatus;
    public const string Prune = LocalAiCommandSyntax.Prune;
    public const string RepoPrune = LocalAiCommandSyntax.RepoPrune;
    public const string Doctor = LocalAiCommandSyntax.Doctor;
    public const string Telemetry = LocalAiCommandSyntax.Telemetry;
    public const string Ask = LocalAiCommandSyntax.Ask;
    public const string Triage = LocalAiCommandSyntax.Triage;
    public const string Translate = LocalAiCommandSyntax.Translate;
    public const string ReadImage = LocalAiCommandSyntax.ReadImage;
    public const string Capabilities = LocalAiCommandSyntax.Capabilities;
    public const string DocsBuild = LocalAiCommandSyntax.DocsBuild;
    public const string DocsApply = LocalAiCommandSyntax.DocsApply;
    public const string AgentGate = LocalAiCommandSyntax.AgentGate;
    public const string AgentWorktreeCreate = LocalAiCommandSyntax.AgentWorktreeCreate;
    public const string AgentWorktreeRemove = LocalAiCommandSyntax.AgentWorktreeRemove;
    public const string AgentWorktree = LocalAiCommandSyntax.AgentWorktree;
    public static string Text => CliHelp.Render("localai");
}
