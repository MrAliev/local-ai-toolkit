using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAi.Cli;

/// <summary>
/// Decides whether a tool call Claude Code is about to make is a text search over the tree.
///
/// The product already tells an assistant to reach for the local tools instead, and that was not
/// enough: the rule was read, agreed to and broken about a dozen times in one session, because the
/// reach for <c>grep</c> happens before the rule is recalled. A <c>PreToolUse</c> hook refuses the
/// call before it runs, and recall stops being involved (#423).
///
/// Deliberately narrow. What is forbidden is sweeping the tree to find code, because
/// <c>search_code</c> and the navigation tools answer the same question by position rather than by
/// string match. Searching the commit history by message is not that, and no local tool replaces
/// it; neither is the word appearing as an argument or inside a document.
/// </summary>
internal static class TextSearchRefusal
{
    /// <summary>
    /// A search tool is refused when it starts a command: at the beginning, after a pipe, a
    /// separator, a subshell, or behind something that runs another command. Anywhere else the
    /// word is an argument or text.
    ///
    /// <c>git</c> is in the list of runners for one reason: <c>git grep</c> is a sweep of the tree
    /// and got through the hand-installed script, which knew only <c>sudo</c> and its kind. It
    /// does not take <c>git log --grep</c> with it, because there the word follows <c>--</c>
    /// rather than a runner.
    ///
    /// The backtick is deliberately not a separator. It is a substitution in shell, but it is also
    /// how prose quotes a command name, and this hook's first false positive was a
    /// <c>grep</c> written in backticks inside the body of an issue about grep. <c>$(...)</c>
    /// covers substitution in every script written this decade.
    /// </summary>
    private static readonly Regex SearchCommand = new(
        """
        (?:^|[|;&(\n]|\$\(|\|\||&&)             # start of a command
        \s*
        (?:(?:sudo|xargs|time|command|env|git)\s+)*  # things that run another command
        (grep|egrep|fgrep|rg|ripgrep|ag|ack|ack-grep|findstr|Select-String)\b
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    /// <summary>
    /// Text fed to a command through a heredoc is data, not commands. Without this, writing a
    /// document that mentions <c>grep</c> — an issue about this very rule, say — is refused as
    /// though it were a search. A search after the heredoc has closed is still refused, which is
    /// why the body is excised rather than the whole command abandoned.
    /// </summary>
    private static readonly Regex Heredoc = new(
        @"<<-?\s*(['""]?)(\w+)\1.*?^\s*\2\s*$",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The shell is not the only way to sweep a tree. A subagent asked for <c>grep</c>, was
    /// refused by the hook, and reached the same result through the built-in search tools — which
    /// the rule covers and the hook did not. These two are a search whatever their arguments say,
    /// so the call itself is refused rather than anything inside it.
    /// </summary>
    private static readonly HashSet<string> SearchTools =
        new(StringComparer.Ordinal) { "Grep", "Glob" };

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
    /// The hole is filled with the name of what was actually refused — the matched command or the
    /// tool — so the sentence can never name something that was allowed.
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

        var match = SearchCommand.Match(Heredoc.Replace(command, "<<HEREDOC"));
        return match.Success
            ? string.Format(CultureInfo.InvariantCulture, ContentRefused, match.Groups[1].Value)
            : null;
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
        "`{0}` searches this tree for a string, which LocalAi routes to the codesearch MCP "
        + "tools: search_code for \"where does X live\", find_references / go_to_definition / "
        + "find_implementations for a symbol, get_code_chunk to fetch a hit. They answer by "
        + "position rather than by string match, so they do not also match the same name on "
        + "another type or in a comment. The call is refused whatever its arguments say, so a "
        + "narrower pattern is refused too. If a literal sweep is genuinely the only way, ask "
        + "first and say what for.";

    private const string PathsRefused =
        "`{0}` matches file paths across this tree, which LocalAi routes to search_code: it finds "
        + "code by meaning and by symbol name and prints `path:start-end` for each hit, so there "
        + "is no list of files to open first. What is simply on disk is answered by listing the "
        + "directory, which is not refused. The call is refused whatever its pattern says. If "
        + "matching paths is genuinely the only way, ask first and say what for.";
}
