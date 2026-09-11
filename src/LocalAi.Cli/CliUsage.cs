namespace LocalAi.Cli;

internal static class CliUsage
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
        "localai repo status [--root dir | git-common-dir] [--json]";
    public const string Prune = "localai prune [--dry-run]";
    // Beside repo status because the noun already means "one repository, found by --root", and
    // the object here is that repository's finished branches. Deliberately not part of `localai
    // prune`: that command's usage says everything it removes is under the runtime root, and a
    // branch is not.
    public const string RepoPrune = "localai repo prune [--root dir] [--dry-run]";
    public const string Doctor = "localai doctor [--root dir] [--json]";
    public const string Telemetry = "localai telemetry";
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
        "[--out file] [--json]";
    public const string ReadImage =
        "localai read-image <question> <image> [image ...] [--profile name] "  +
        "[--model model] [--together] [--language en|ru] [--json]";
    // No brackets around the flag: here it is required rather than optional. The listing is
    // for a program, and the block this line sits in is the list for a person.
    public const string Capabilities = "localai capabilities --json";

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

    // Every command the binary actually answers to belongs here. The hook command was missing
    // from this list while every installed Git hook invoked it, so anyone debugging a hook was
    // told by the tool itself that the command it had just run did not exist.
    public const string Text =
        "Usage: localai native <operation> [--request file] | " +
        ModelStatus + " | " + ModelPull + " | " + ModelPreflight + " | " +
        RepoStatus + " | " + RepoPrune + " | " +
        "localai policy <show|set> [options] | " +
        "localai update [--wait] [--force] | " +
        "localai semantic <operation> [options] | localai bootstrap --dry-run | " +
        "localai sync [--root dir] [--base-only] [--require-semantics] [--max-inline-files n] | " + Hook + " | " +
        "localai hooks install [--root dir] | " + Prune + " | " + Doctor + " | " +
        Telemetry + " | " + Ask + " | " + Triage + " | " + ReadImage + " | " + Translate +
        " | " + Capabilities + " | " + AgentGate + " | " + AgentWorktree;
}
