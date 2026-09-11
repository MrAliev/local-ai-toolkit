using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAi.Installer.Core.Agents;

/// <summary>
/// Registers this product's hooks in <c>~/.claude/settings.json</c>, and takes them back out
/// (#423).
///
/// Three of them now: the text-search gate on <c>PreToolUse</c>, and the pair on
/// <c>WorktreeCreate</c> and <c>WorktreeRemove</c> that decides where a worktree is made. Each is
/// a <see cref="ClaudeHook"/> — an event, an optional matcher, the signature that recognises ours
/// again, and the time the client allows it.
///
/// This is the fourth file the product writes into somebody else's configuration and the first
/// that a person edits by hand for reasons of their own — a theme, permissions, plugins, and
/// their own hooks, possibly on the very event this wants. So the whole of the contract is: add
/// ours and change nothing else; remove ours and leave the file as it was, byte for byte.
///
/// The document is parsed to decide and spliced as text to write, which is the split
/// <c>ClaudeConfigurationAdapter</c> already uses on <c>.claude.json</c> and the reason
/// <see cref="JsonTextSurgery"/> exists. Reserializing would reformat a file nobody asked us to
/// reformat — and it was tried here first, which is how the two round-trip tests below came to be
/// the ones that failed.
/// </summary>
/// <summary>
/// One hook this product writes into the client's settings.
/// </summary>
/// <param name="Event">
/// The client's event key. An entry lives under exactly one of them, so an effect spanning two
/// events is two entries.
/// </param>
/// <param name="Matcher">
/// The tools the entry answers for, or null where the event has none to match on. Null and an
/// empty string are not the same thing: the client reads an empty matcher as "match nothing".
/// </param>
/// <param name="Signature">
/// How our entry is recognised again later, at another launcher path and after the user has
/// edited the file. The command's own tail rather than a marker key: JSON has nowhere to put a
/// comment, and a separate marker could be dropped by a formatter while the hook stayed.
/// </param>
/// <param name="TimeoutSeconds">
/// What the client allows the command before killing it. Per event, because the two differ by an
/// order of magnitude: the gate answers from a parsed payload, and creating a worktree runs
/// `git worktree add`, which writes out a whole working tree.
/// </param>
public sealed record ClaudeHook(
    string Event,
    string? Matcher,
    string Signature,
    int TimeoutSeconds)
{
    /// <summary>
    /// The text-search gate. <c>Bash</c> and <c>PowerShell</c> are the shells; <c>Grep</c> and
    /// <c>Glob</c> are the client's own search tools, which a subagent reached for after the
    /// shell refused — the rule covered them and the hook did not.
    /// </summary>
    public static ClaudeHook Gate { get; } =
        new("PreToolUse", "Bash|PowerShell|Grep|Glob", "run localai agent gate", 10);

    /// <summary>
    /// Where a worktree is created. The client has no setting for the location, so this hook is
    /// the only way to put one beside the repository instead of inside it.
    /// </summary>
    public static ClaudeHook WorktreeCreate { get; } =
        new("WorktreeCreate", null, WorktreeSignature, WorktreeTimeoutSeconds);

    /// <summary>The other half: without it a worktree is made and never taken away.</summary>
    public static ClaudeHook WorktreeRemove { get; } =
        new("WorktreeRemove", null, WorktreeSignature, WorktreeTimeoutSeconds);

    /// <summary>
    /// One signature for both worktree events rather than the exact tail of each. A line copied
    /// from one event to the other is still ours, and still goes when ours goes — which is the
    /// case `Remove` taking every entry of ours rather than the first was written for.
    /// </summary>
    private const string WorktreeSignature = "run localai agent worktree";

    /// <summary>
    /// Three minutes. The command allows two for a single git call, and the slowest of those is
    /// `git worktree add` on a cold tree; the gate's ten seconds would kill this one mid-checkout.
    ///
    /// What a timeout costs decides how much headroom is enough, and the client refuses to open
    /// the worktree rather than falling back to a place of its own (owner, 2026-09-11). So the
    /// failure is a refusal somebody sees and can retry, not a worktree quietly created somewhere
    /// the client is not looking — which is the failure that would have argued for ten minutes.
    /// </summary>
    private const int WorktreeTimeoutSeconds = 180;
}

public static class ClaudeHookRegistration
{
    public static bool IsRegistered(string settings, string command) =>
        IsRegistered(settings, ClaudeHook.Gate, command);

    public static bool IsRegistered(string settings, ClaudeHook hook, string command)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(command);
        return Groups(Parse(settings), hook) is { } groups &&
            groups.Any(group => Names(group, command));
    }

    /// <summary>
    /// Whether an entry this product wrote is present, wherever its launcher was at the time.
    ///
    /// This is the question the installer asks, and it is not the overload above: the launcher
    /// path is part of the command, so an entry left by an install at another directory answers
    /// no there while <see cref="Remove"/> — which recognises ours by <see cref="Signature"/> —
    /// takes it out anyway. The two predicates disagreeing is a review line that says nothing
    /// will be written to a file the run then edits.
    /// </summary>
    public static bool IsRegistered(string settings) =>
        IsRegistered(settings, ClaudeHook.Gate);

    public static bool IsRegistered(string settings, ClaudeHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return Groups(Parse(settings), hook) is { } groups &&
            groups.Any(group => Ours(group, hook));
    }

    /// <summary>
    /// How many entries on the same event belong to somebody else — the ones an uninstall leaves
    /// exactly where they are.
    ///
    /// Worth counting because of what their presence does to the person afterwards: a tool call
    /// still refused once LocalAi is gone reads as an uninstall that failed, and they go looking
    /// for what it left behind. There is such an entry on the author's own machine.
    /// </summary>
    public static int OtherEntries(string settings) =>
        OtherEntries(settings, ClaudeHook.Gate);

    public static int OtherEntries(string settings, ClaudeHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return Groups(Parse(settings), hook) is { } groups
            ? groups.Count(group => !Ours(group, hook))
            : 0;
    }

    public static string Add(string settings, string command) =>
        Add(settings, ClaudeHook.Gate, command);

    public static string Add(string settings, ClaudeHook hook, string command)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var text = Remove(settings, hook);
        if (Parse(text) is null)
        {
            return Fresh(hook, command);
        }

        var rootStart = RootStart(text);
        var rootEnd = JsonTextSurgery.FindMatching(text, rootStart, '{', '}');
        var hooks = JsonTextSurgery.FindTopLevelProperty(text, rootStart + 1, rootEnd, "hooks");
        if (hooks < 0)
        {
            return Insert(
                text,
                rootStart + 1,
                rootEnd,
                "\"hooks\": {\n    \"" + hook.Event + "\": [\n" +
                    Group("      ", hook, command) + "\n    ]\n  }",
                "  ");
        }

        var hooksStart = ValueStart(text, hooks, "hooks", '{');
        var hooksEnd = JsonTextSurgery.FindMatching(text, hooksStart, '{', '}');
        var array = JsonTextSurgery.FindTopLevelProperty(
            text,
            hooksStart + 1,
            hooksEnd,
            hook.Event);
        if (array < 0)
        {
            return Insert(
                text,
                hooksStart + 1,
                hooksEnd,
                "\"" + hook.Event + "\": [\n" + Group("      ", hook, command) + "\n    ]",
                "    ");
        }

        var arrayStart = ValueStart(text, array, hook.Event, '[');
        var arrayEnd = JsonTextSurgery.FindMatching(text, arrayStart, '[', ']');
        return Insert(
            text,
            arrayStart + 1,
            arrayEnd,
            Group(string.Empty, hook, command).Trim(),
            "      ");
    }

    public static string Remove(string settings) => Remove(settings, ClaudeHook.Gate);

    public static string Remove(string settings, ClaudeHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var root = Parse(settings);
        if (root is null ||
            Groups(root, hook) is not { } parsed ||
            !parsed.Any(group => Ours(group, hook)))
        {
            return settings;
        }

        // Every entry of ours, not the first one found. `Add` cannot produce a second — it removes
        // ours before inserting — but a person can, and the hint naming exactly which tools are
        // covered invites copying the line to a second matcher. Leaving one behind would point a
        // deleted launcher at their tool calls while the retained notice blamed their own hooks
        // for the refusal.
        var text = settings;
        while (Groups(Parse(text), hook) is { } remaining &&
            remaining.Any(group => Ours(group, hook)))
        {
            text = CutOurGroup(text, hook);
        }

        // What is left decides how far to keep going, and the parsed document is what says so —
        // the containers this product created go with the entry, and the user's do not. Leaving
        // `"PreToolUse": []` behind is the difference between an uninstall that undoes an install
        // and one that leaves a scar.
        if (Groups(Parse(text), hook) is { Count: > 0 })
        {
            return text;
        }

        // HooksBody, not ArrayBody: the body wanted is the object that *holds* the property, and
        // passing the array's own body looked for "PreToolUse" inside itself and found nothing.
        text = CutProperty(text, hook.Event, '[', HooksBody);
        return Parse(text)?["hooks"] is JsonObject hooks && hooks.Count > 0
            ? text
            : CutProperty(text, "hooks", '{', RootBody);
    }

    private static string CutOurGroup(string settings, ClaudeHook hook)
    {
        var (bodyStart, bodyEnd) = ArrayBody(settings, hook.Event);
        var span = OurGroup(settings, bodyStart, bodyEnd, hook)
            ?? throw new InvalidOperationException("Unsupported Claude settings layout.");
        return Cut(settings, span.Start, span.End);
    }

    private static string CutProperty(
        string text,
        string name,
        char opening,
        Func<string, (int Start, int End)> body)
    {
        var (bodyStart, bodyEnd) = body(text);
        var property = JsonTextSurgery.FindTopLevelProperty(text, bodyStart, bodyEnd, name);
        if (property < 0)
        {
            return text;
        }

        var valueStart = ValueStart(text, property, name, opening);
        var valueEnd = JsonTextSurgery.FindMatching(
            text,
            valueStart,
            opening,
            opening == '[' ? ']' : '}');
        return Cut(text, property, valueEnd + 1);
    }

    private static (int Start, int End) RootBody(string text)
    {
        var start = RootStart(text);
        return (start + 1, JsonTextSurgery.FindMatching(text, start, '{', '}'));
    }

    private static (int Start, int End) HooksBody(string text)
    {
        var (rootStart, rootEnd) = RootBody(text);
        var hooks = JsonTextSurgery.FindTopLevelProperty(text, rootStart, rootEnd, "hooks");
        var start = ValueStart(text, hooks, "hooks", '{');
        return (start + 1, JsonTextSurgery.FindMatching(text, start, '{', '}'));
    }

    private static (int Start, int End) ArrayBody(string text, string name)
    {
        var (hooksStart, hooksEnd) = HooksBody(text);
        var array = JsonTextSurgery.FindTopLevelProperty(text, hooksStart, hooksEnd, name);
        var start = ValueStart(text, array, name, '[');
        return (start + 1, JsonTextSurgery.FindMatching(text, start, '[', ']'));
    }

    /// <summary>
    /// Where the value of a property begins, given the index of its opening quote.
    /// </summary>
    private static int ValueStart(string text, int property, string name, char opening)
    {
        var colon = text.IndexOf(':', property + name.Length + 2);
        var start = colon < 0 ? -1 : JsonTextSurgery.NextNonWhitespace(text, colon + 1);
        return start >= 0 && start < text.Length && text[start] == opening
            ? start
            : throw new InvalidOperationException("Unsupported Claude settings layout.");
    }

    /// <summary>
    /// The text span of the entry this product wrote, found by parsing each element rather than by
    /// matching on the text: an element's formatting is the user's, and only the parsed document
    /// says which one is ours.
    /// </summary>
    private static (int Start, int End)? OurGroup(string text, int start, int end, ClaudeHook hook)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] != '{')
            {
                continue;
            }

            var close = JsonTextSurgery.FindMatching(text, index, '{', '}') + 1;
            if (Ours(JsonNode.Parse(text[index..close]), hook))
            {
                return (index, close);
            }

            index = close - 1;
        }

        return null;
    }

    /// <summary>
    /// Takes a span out along with the separator that joined it to its neighbours, in the shape
    /// <c>ClaudeConfigurationAdapter.RemoveProperty</c> uses: the whitespace before it, the comma
    /// after it — or, when it was last, the comma before it.
    /// </summary>
    private static string Cut(string text, int start, int end)
    {
        var leading = start;
        while (leading > 0 && char.IsWhiteSpace(text[leading - 1]))
        {
            leading--;
        }

        var trailing = end;
        while (trailing < text.Length && char.IsWhiteSpace(text[trailing]))
        {
            trailing++;
        }

        if (trailing < text.Length && text[trailing] == ',')
        {
            // Not the last entry: ours, the indentation that introduced us, and the separator
            // that joined us to the next one. The next one's own indentation is not ours to take.
            return text[..leading] + text[(trailing + 1)..];
        }

        if (leading > 0 && text[leading - 1] == ',')
        {
            // The last of several. The separator to remove is the one *before* us, and the
            // whitespace after us belongs to the closing bracket rather than to us — consuming it
            // is how a removal that promises the file back byte for byte reflowed `\n    ]` to
            // `]`, silently, on every layout where ours was not first.
            return text[..(leading - 1)] + text[end..];
        }

        // The only one. No separator either side, and nothing else in the container needs the
        // whitespace around us, so an emptied container closes up rather than keeping the shape
        // of what used to be in it.
        return text[..leading] + text[trailing..];
    }

    private static string Insert(string text, int bodyStart, int bodyEnd, string addition, string indent)
    {
        var hasContent = JsonTextSurgery.NextNonWhitespace(text[bodyStart..bodyEnd], 0) >= 0;
        var separator = hasContent ? "," : string.Empty;
        return text[..bodyStart] + "\n" + indent + addition + separator + text[bodyStart..];
    }

    private static string Group(string indent, ClaudeHook hook, string command) =>
        indent + "{ " +
        // Omitted entirely rather than written empty: an event with nothing to match on has no
        // matcher, and `"matcher": ""` is the client's spelling for "match nothing".
        (hook.Matcher is null ? string.Empty : "\"matcher\": \"" + hook.Matcher + "\", ") +
        "\"hooks\": [ { \"type\": \"command\", " +
        "\"command\": " + JsonSerializer.Serialize(command) + ", \"timeout\": " +
        hook.TimeoutSeconds + " } ] }";

    private static string Fresh(ClaudeHook hook, string command) =>
        "{\n  \"hooks\": {\n    \"" + hook.Event + "\": [\n" +
        Group("      ", hook, command) + "\n    ]\n  }\n}";

    private static JsonObject? Parse(string settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return null;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(settings) as JsonObject
                ?? throw new InvalidOperationException("Unsupported Claude settings layout.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Malformed Claude settings layout.", exception);
        }

        if (root["hooks"] is not (null or JsonObject))
        {
            throw new InvalidOperationException("Unsupported Claude settings layout.");
        }

        return root;
    }

    private static int RootStart(string text)
    {
        var start = JsonTextSurgery.NextNonWhitespace(text, 0);
        return start >= 0 && text[start] == '{'
            ? start
            : throw new InvalidOperationException("Unsupported Claude settings layout.");
    }

    private static JsonArray? Groups(JsonObject? root, ClaudeHook hook)
    {
        if (root?["hooks"] is not JsonObject hooks || hooks[hook.Event] is null)
        {
            return null;
        }

        return hooks[hook.Event] as JsonArray
            ?? throw new InvalidOperationException("Unsupported Claude settings layout.");
    }

    private static bool Ours(JsonNode? group, ClaudeHook hook) => Names(group, hook.Signature);

    private static bool Names(JsonNode? group, string fragment) =>
        group is JsonObject entry &&
        entry["hooks"] is JsonArray hooks &&
        hooks.Any(hook =>
            hook is JsonObject command &&
            command["command"]?.GetValue<string>() is { } text &&
            text.Contains(fragment, StringComparison.Ordinal));
}
