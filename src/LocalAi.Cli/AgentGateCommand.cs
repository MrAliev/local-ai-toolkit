using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>
/// The gate an agent's tool call passes through: it reads a Claude Code <c>PreToolUse</c> payload
/// on standard input and answers on standard output in that client's own schema.
///
/// It exists because an instruction was not enough. The product already tells an assistant to
/// reach for the local tools instead of sweeping the tree, and the rule was read, agreed to,
/// written down again and broken about a dozen times in one session — the reach for <c>grep</c>
/// happens before the rule is recalled. A hook refuses the call before it runs, and recall stops
/// being involved (#423).
///
/// Nothing about it is meant to be typed. The installer registers the line, the client runs it,
/// and a person meets it only when they open <c>settings.json</c> to see what it does — which is
/// the one case the catalogued sentence below is written for.
/// </summary>
internal static class AgentGateCommand
{
    /// <param name="input">
    /// The payload. Passed rather than read from the console so a test can hand it a document of
    /// its own and read what was written back, which is the shape this repository prefers over a
    /// rule everybody has to remember.
    /// </param>
    /// <param name="inputRedirected">
    /// False when a person is at the console. The only reason this matters: without it, a hand-run
    /// command waits for input that will never come and looks hung.
    /// </param>
    public static int Execute(
        string[] args,
        CommandStreams streams,
        TextReader input,
        bool inputRedirected)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);

        if (args is [var unknown, ..])
        {
            streams.Error.WriteLine(
                CliText.CommandUnknownArgument("agent gate", unknown, CliUsage.AgentGate));
            return 2;
        }

        if (!inputRedirected)
        {
            streams.Error.WriteLine(CliText.AgentGateNeedsPayload);
            return 2;
        }

        // Exit 0 either way. A non-zero code in the PreToolUse contract means the hook itself
        // failed, not that it refused — a refusal is the document below and nothing else.
        if (TextSearchRefusal.DecisionFor(input.ReadToEnd()) is { } decision)
        {
            streams.Out.Write(decision);
        }

        return 0;
    }
}
