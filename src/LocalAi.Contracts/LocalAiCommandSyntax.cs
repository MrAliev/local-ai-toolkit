namespace LocalAi.Contracts;

public static class LocalAiCommandSyntax
{
    public const string ModelStatus = "localai model status";
    public const string ModelPull =
        "localai model pull --model <model> --catalog-version <version> "  +
        "[--progress-json]";
    public const string ModelPreflight =
        "localai model preflight --model <model> --context <tokens> " +
        "--catalog-version <version>";

    public const string Hook =
        "localai hook <" + HookEvents + "> [--root dir]";
    public const string HookEvents = "post-commit|post-checkout|post-merge|post-rewrite";
    public const string RepoStatus =
        "localai repo status [--root dir | git-common-dir] [--json] " +
        "[--follow [--interval seconds]]";
    public const string Prune = "localai prune [--dry-run]";
    // Beside repo status because the noun already means "one repository, found by --root", and
    // the object here is that repository's finished branches. Deliberately not part of `localai
    // prune`: that command's usage says everything it removes is under the runtime root, and a
    // branch is not.
    public const string RepoPrune = "localai repo prune [--root dir] [--dry-run]";
    public const string Doctor = "localai doctor [--root dir] [--json]";
    public const string Telemetry = "localai telemetry [--call-id <guid>]";
    public const string Ask =
        "localai ask <prompt> [file ...] [--profile name] [--model model] " +
        "[--language en|ru] [--json]";
    public const string Triage =
        "localai triage [log-file|-] [--question text] [--model model] " +
        "[--language en|ru] [--json]";
    // The three ways in are one slot and only one may be filled, which is the fact worth showing;
    // `repo status` above already spells an alternation this way. `--in -` is not advertised
    // here — the bare `-` in the same bracket already stands for standard input.
    public const string Translate =
        "localai translate [text|-|--in file] --from language --to language [--markdown] " +
        "[--out file] [--checkpoint dir] [--json]";
    public const string ReadImage =
        "localai read-image <question> <file> [file ...] [--profile name] "  +
        "[--model model] [--together] [--pages ranges] [--out file] [--checkpoint dir] [--language en|ru] [--json]";
    // No brackets around the flag: here it is required rather than optional. The listing is
    // for a program, and the block this line sits in is the list for a person.
    public const string Capabilities = "localai capabilities --json";
    public const string SemanticImpact = "localai semantic impact --path <relative.cs> --line <1-based> [--column <1-based utf16>] [--limit 1..10000] [--root dir] [--json]";
    public const string DocsBuild = "localai docs build --root <repo> [--path <relative-path>] [--plan] [--limit N | --all] [--standalone-html] [--json]";
    public const string DocsApply = "localai docs apply --root <repo> [--json]";
    public const string DocsPrune = "localai docs prune --root <repo> [--keep N] [--dry-run | --apply] [--json]";
    public const string DocsPin = "localai docs pin --root <repo> --package <package-id> [--json]";
    public const string DocsUnpin = "localai docs unpin --root <repo> --package <package-id> [--json]";

    // No options and no operands: the payload arrives on standard input and the answer goes to
    // standard output, both in the client's own schema. Two words rather than one because `hook`
    // already belongs to Git above, and because the next thing an agent's configuration needs
    // will want to live beside this one rather than take another top-level word.
    //
    // Deliberately absent from MachineOutput.Commands: what this prints is the client's
    // `hookSpecificOutput`, not a MachineEnvelope, so `--json` must keep refusing it.
    public const string AgentGate = "localai agent gate";

    // Two words before the operation for the reason `agent gate` has two: this is the next thing
    // an agent's configuration needs, and the installer registers it in the same file. `hook`
    // above is Git's and its events are Git's; WorktreeCreate and WorktreeRemove are the
    // client's.
    //
    // The branch and the path are bracketed because the hook face reads them from a payload on
    // standard input. A console with neither is a refusal rather than a wait, which is the
    // mistake AgentGateNeedsPayload was written for.
    //
    // Deliberately absent from MachineOutput.Commands, like `agent gate`: what create prints is
    // one path a client reads, not a MachineEnvelope, so `--json` must keep refusing it.
    public const string AgentWorktreeCreate =
        "localai agent worktree create [branch] [--root dir]";
    public const string AgentWorktreeRemove =
        "localai agent worktree remove [path]";

    // The form the top-level listing carries. The two above are what the refusals carry, so a
    // message can never name a form the dispatcher will not accept.
    public const string AgentWorktree =
        "localai agent worktree <create|remove> [branch|path] [--root dir]";

}
