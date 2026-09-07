namespace LocalAi.Installer.Core.Agents;

/// <summary>
/// Finding one's way around a JSON document as text, so an edit can be spliced into it instead of
/// the whole thing being reserialized.
///
/// Every file this product writes into somebody else's configuration is a file they edit by hand,
/// and reserializing one reformats it: their indentation, their key order, their arrays folded
/// onto one line unfolded onto five. A diff like that is one nobody reads, so nobody sees what
/// actually changed in it. `ClaudeConfigurationAdapter` has answered that with text splicing since
/// it was written, and these are the three scanners it did it with — shared rather than copied,
/// because a second brace matcher is a second set of quoting bugs.
///
/// All three are string-aware: a brace inside a string literal is a character, and a backslash
/// before a quote is an escape rather than the end of one.
/// </summary>
internal static class JsonTextSurgery
{
    public static int NextNonWhitespace(string value, int start)
    {
        for (var index = start; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The index of the bracket closing the one at <paramref name="start"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document ends before the bracket closes. Thrown rather than returned as -1 because
    /// every caller here is about to slice with the result, and a -1 slices silently.
    /// </exception>
    public static int FindMatching(string value, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == open)
            {
                depth++;
            }
            else if (character == close)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw new InvalidOperationException("Unsupported JSON layout.");
    }

    /// <summary>
    /// Where a property of the object between <paramref name="start"/> and <paramref name="end"/>
    /// begins — at its opening quote — or -1.
    ///
    /// Top level of that range only: a nested object may carry a property of the same name, and
    /// splicing into that one instead is the bug this depth count exists to stop.
    /// </summary>
    public static int FindTopLevelProperty(string value, int start, int end, string name)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < end; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                var quoted = "\"" + name + "\"";
                if (depth == 0 &&
                    index + quoted.Length <= end &&
                    string.Equals(
                        value.Substring(index, quoted.Length),
                        quoted,
                        StringComparison.Ordinal))
                {
                    var afterName = NextNonWhitespace(value, index + quoted.Length);
                    if (afterName >= 0 && afterName < end && value[afterName] == ':')
                    {
                        return index;
                    }
                }

                inString = true;
            }
            else if (character is '{' or '[')
            {
                depth++;
            }
            else if (character is '}' or ']')
            {
                depth--;
            }
        }

        return -1;
    }
}
