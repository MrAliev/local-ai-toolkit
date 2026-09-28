using System.Globalization;
using System.Text.Json;

namespace LocalAi.Cli;

/// <summary>
/// Decides what the agent gate refuses: a text search over the tree, which is this class, or a
/// whole log poured into a reply, which is <see cref="WholeLogRefusal"/> and is asked first.
///
/// The product already tells an assistant to reach for the local tools instead, and that was not
/// enough: the rule was read, agreed to and broken about a dozen times in one session, because the
/// reach for <c>grep</c> happens before the rule is recalled. A <c>PreToolUse</c> hook refuses the
/// call before it runs, and recall stops being involved (#423). The log half was added for the
/// same reason after the same thing happened to it, with <c>awk</c> in place of <c>grep</c>.
///
/// The name is now narrower than what the type does, and the rename is deliberately not in the
/// change that made it so: it touches every call site and every test row and fixes nothing but a
/// word.
///
/// Deliberately narrow, the search half. What is forbidden is sweeping the tree to find code,
/// because <c>search_code</c> and the navigation tools answer the same question by position rather
/// than by string match. Searching the commit history by message is not that, and no local tool
/// replaces it; neither is the word appearing as an argument or inside a document.
/// </summary>
internal static class TextSearchRefusal
{
    /// <summary>
    /// The tools that sweep a tree for a string, by the names they are actually typed under.
    ///
    /// <c>sls</c> is here because it is how <c>Select-String</c> is written in practice, and
    /// PowerShell is one of the two shells this hook answers for — spelling only the full name
    /// left the common form allowed on the platform this product is built for. <c>egrep</c> and
    /// <c>fgrep</c> are the same argument one generation earlier.
    ///
    /// What is not here: <c>find</c> and <c>Get-ChildItem -Recurse</c>. They sweep paths rather
    /// than contents, which is the <c>Glob</c> half of the rule, and refusing every use of
    /// <c>find</c> would refuse a great deal that has nothing to do with searching. Widening to
    /// them is a decision about what the product forbids, not a hole in how it reads a command.
    /// </summary>
    private static readonly HashSet<string> SearchPrograms = new(StringComparer.Ordinal)
    {
        "grep", "egrep", "fgrep", "rg", "ripgrep", "ag", "ack", "ack-grep", "findstr",
        "select-string", "sls",
    };

    public static bool Refuses(string? toolName, string? command) =>
        ReasonFor(toolName, command) is not null;

    /// <summary>
    /// What the reader is told, or null to let the call through.
    ///
    /// Two sentences rather than one, split by what is being searched rather than by which tool
    /// asked. The single text this replaces was false on <c>Glob</c>: that tool matches paths and
    /// does no string matching at all, so telling its caller that the local tools "answer by
    /// position rather than by string match" answers a question nobody asked, and pointing at
    /// <c>search_code</c> for "what is in this directory" points at the wrong tool — listing the
    /// directory answers that, and is not refused.
    ///
    /// The hole is filled with the name of what was actually refused — the program the line runs
    /// or the tool that asked — so the sentence can never name something that was allowed.
    ///
    /// <c>Grep</c> and <c>Glob</c> are the client's own search tools, and they are a search
    /// whatever their arguments say: a subagent refused at the shell reached the same result
    /// through them, which the rule covered and the hook did not.
    /// </summary>
    public static string? ReasonFor(string? toolName, string? command)
    {
        if (string.Equals(toolName, "Glob", StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.InvariantCulture, PathsRefused, toolName);
        }

        if (string.Equals(toolName, "Grep", StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.InvariantCulture, ContentRefused, toolName);
        }

        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        // The log first, because a line can be both and they are not equally expensive.
        // `cat build.log | grep error` sweeps and pours; answering it as a search names
        // `search_code`, which is the wrong tool for a log, and leaves the costly half unsaid.
        if (WholeLogRefusal.ReasonFor(command) is { } log)
        {
            return log;
        }

        var found = CommandLineScan.ProgramWords(command)
            .FirstOrDefault(SearchPrograms.Contains);
        return found is null
            ? null
            : string.Format(CultureInfo.InvariantCulture, ContentRefused, found);
    }

    /// <summary>
    /// The answer a <c>PreToolUse</c> hook is read for, or null to say nothing and let the call
    /// through. Anything unreadable is a call this command never inspected, and a hook that cannot
    /// read its input must not block work.
    /// </summary>
    public static string? DecisionFor(string payload)
    {
        string? toolName;
        string? command;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            toolName = root.TryGetProperty("tool_name", out var name) &&
                name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
            command = root.TryGetProperty("tool_input", out var input) &&
                input.ValueKind == JsonValueKind.Object &&
                input.TryGetProperty("command", out var text) &&
                text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        return ReasonFor(toolName, command) is { } reason ? Decision(reason) : null;
    }

    private static string Decision(string reason) =>
        JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = "deny",
                permissionDecisionReason = reason,
            },
        });

    /// <summary>
    /// English on every machine, and deliberately not in <c>CliText</c>.
    ///
    /// This is the same rule <c>ManagedInstructionBlock</c> installs into every agent
    /// configuration, executed rather than restated — and that block is one English constant with
    /// no translated pair. A Russian rendering here would be a second wording of one rule, free to
    /// drift from the first at the next edit of either. <c>CliText</c>'s own comment on
    /// <c>PathNotRelative</c> makes the same argument: a fallback that refuses in different words
    /// is a trap.
    ///
    /// What is catalogued is this command's own refusals, in <c>CliText</c> — those are read by a
    /// person debugging their <c>settings.json</c>, not by an assistant acting on the rule.
    /// </summary>
    private const string ContentRefused =
        "`{0}` searches this tree for a string, which Local AI routes to the codesearch MCP "
        + "tools: search_code for \"where does X live\", find_references / go_to_definition / "
        + "find_implementations for a symbol, get_code_chunk to fetch a hit. They answer by "
        + "position rather than by string match, so they do not also match the same name on "
        + "another type or in a comment. The call is refused whatever its arguments say, so a "
        + "narrower pattern is refused too. If a literal sweep is genuinely the only way, ask "
        + "first and say what for.";

    private const string PathsRefused =
        "`{0}` matches file paths across this tree, which Local AI routes to search_code: it finds "
        + "code by meaning and by symbol name and prints `path:start-end` for each hit, so there "
        + "is no list of files to open first. What is simply on disk is answered by listing the "
        + "directory, which is not refused. The call is refused whatever its pattern says. If "
        + "matching paths is genuinely the only way, ask first and say what for.";
}
