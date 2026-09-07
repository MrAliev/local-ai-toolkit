using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAi.Installer.Core.Agents;

/// <summary>
/// Registers the text-search gate as a <c>PreToolUse</c> hook in <c>~/.claude/settings.json</c>,
/// and takes it back out (#423).
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
public static class ClaudeHookRegistration
{
    /// <summary>
    /// The events the gate answers for. <c>Bash</c> and <c>PowerShell</c> are the shells;
    /// <c>Grep</c> and <c>Glob</c> are the client's own search tools, which a subagent reached for
    /// after the shell refused — the rule covered them and the hook did not.
    /// </summary>
    private const string Matcher = "Bash|PowerShell|Grep|Glob";

    /// <summary>
    /// How our entry is recognised again later, at another launcher path and after the user has
    /// edited the file. The command's own tail rather than a marker key: JSON has nowhere to put a
    /// comment, and a separate marker could be dropped by a formatter while the hook stayed.
    /// </summary>
    private const string Signature = "run localai agent gate";

    /// <summary>Seconds. Long enough for a cold process start, short enough to notice.</summary>
    private const int TimeoutSeconds = 10;

    public static bool IsRegistered(string settings, string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Groups(Parse(settings)) is { } groups && groups.Any(group => Names(group, command));
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
        Groups(Parse(settings)) is { } groups && groups.Any(Ours);

    /// <summary>
    /// How many entries on the same event belong to somebody else — the ones an uninstall leaves
    /// exactly where they are.
    ///
    /// Worth counting because of what their presence does to the person afterwards: a tool call
    /// still refused once LocalAi is gone reads as an uninstall that failed, and they go looking
    /// for what it left behind. There is such an entry on the author's own machine.
    /// </summary>
    public static int OtherEntries(string settings) =>
        Groups(Parse(settings)) is { } groups ? groups.Count(group => !Ours(group)) : 0;

    public static string Add(string settings, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var text = Remove(settings);
        if (Parse(text) is null)
        {
            return Fresh(command);
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
                "\"hooks\": {\n    \"PreToolUse\": [\n" + Group("      ", command) + "\n    ]\n  }",
                "  ");
        }

        var hooksStart = ValueStart(text, hooks, "hooks", '{');
        var hooksEnd = JsonTextSurgery.FindMatching(text, hooksStart, '{', '}');
        var array = JsonTextSurgery.FindTopLevelProperty(
            text,
            hooksStart + 1,
            hooksEnd,
            "PreToolUse");
        if (array < 0)
        {
            return Insert(
                text,
                hooksStart + 1,
                hooksEnd,
                "\"PreToolUse\": [\n" + Group("      ", command) + "\n    ]",
                "    ");
        }

        var arrayStart = ValueStart(text, array, "PreToolUse", '[');
        var arrayEnd = JsonTextSurgery.FindMatching(text, arrayStart, '[', ']');
        return Insert(text, arrayStart + 1, arrayEnd, Group(string.Empty, command).Trim(), "      ");
    }

    public static string Remove(string settings)
    {
        var root = Parse(settings);
        if (root is null || Groups(root) is not { } parsed || !parsed.Any(Ours))
        {
            return settings;
        }

        var text = CutOurGroup(settings);

        // What is left decides how far to keep going, and the parsed document is what says so —
        // the containers this product created go with the entry, and the user's do not. Leaving
        // `"PreToolUse": []` behind is the difference between an uninstall that undoes an install
        // and one that leaves a scar.
        if (Groups(Parse(text)) is { Count: > 0 })
        {
            return text;
        }

        // HooksBody, not ArrayBody: the body wanted is the object that *holds* the property, and
        // passing the array's own body looked for "PreToolUse" inside itself and found nothing.
        text = CutProperty(text, "PreToolUse", '[', HooksBody);
        return Parse(text)?["hooks"] is JsonObject hooks && hooks.Count > 0
            ? text
            : CutProperty(text, "hooks", '{', RootBody);
    }

    private static string CutOurGroup(string settings)
    {
        var (bodyStart, bodyEnd) = ArrayBody(settings);
        var span = OurGroup(settings, bodyStart, bodyEnd)
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

    private static (int Start, int End) ArrayBody(string text)
    {
        var (hooksStart, hooksEnd) = HooksBody(text);
        var array = JsonTextSurgery.FindTopLevelProperty(text, hooksStart, hooksEnd, "PreToolUse");
        var start = ValueStart(text, array, "PreToolUse", '[');
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
    private static (int Start, int End)? OurGroup(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] != '{')
            {
                continue;
            }

            var close = JsonTextSurgery.FindMatching(text, index, '{', '}') + 1;
            if (Ours(JsonNode.Parse(text[index..close])))
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
        while (start > 0 && char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        if (end < text.Length && text[end] == ',')
        {
            end++;
        }
        else if (start > 0)
        {
            var comma = start - 1;
            while (comma >= 0 && char.IsWhiteSpace(text[comma]))
            {
                comma--;
            }

            if (comma >= 0 && text[comma] == ',')
            {
                start = comma;
            }
        }

        return text[..start] + text[end..];
    }

    private static string Insert(string text, int bodyStart, int bodyEnd, string addition, string indent)
    {
        var hasContent = JsonTextSurgery.NextNonWhitespace(text[bodyStart..bodyEnd], 0) >= 0;
        var separator = hasContent ? "," : string.Empty;
        return text[..bodyStart] + "\n" + indent + addition + separator + text[bodyStart..];
    }

    private static string Group(string indent, string command) =>
        indent + "{ \"matcher\": \"" + Matcher + "\", \"hooks\": [ { \"type\": \"command\", " +
        "\"command\": " + JsonSerializer.Serialize(command) + ", \"timeout\": " +
        TimeoutSeconds + " } ] }";

    private static string Fresh(string command) =>
        "{\n  \"hooks\": {\n    \"PreToolUse\": [\n" + Group("      ", command) + "\n    ]\n  }\n}";

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

    private static JsonArray? Groups(JsonObject? root)
    {
        if (root?["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is null)
        {
            return null;
        }

        return hooks["PreToolUse"] as JsonArray
            ?? throw new InvalidOperationException("Unsupported Claude settings layout.");
    }

    private static bool Ours(JsonNode? group) => Names(group, Signature);

    private static bool Names(JsonNode? group, string fragment) =>
        group is JsonObject entry &&
        entry["hooks"] is JsonArray hooks &&
        hooks.Any(hook =>
            hook is JsonObject command &&
            command["command"]?.GetValue<string>() is { } text &&
            text.Contains(fragment, StringComparison.Ordinal));
}
