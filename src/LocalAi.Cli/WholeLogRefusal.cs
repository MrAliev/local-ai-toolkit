using System.Globalization;

namespace LocalAi.Cli;

/// <summary>
/// Decides whether a tool call is about to read a whole log into the assistant's reply.
///
/// The block already routes by size — a snippet to the tool, a whole log to `localai triage` — and
/// routing advice was not enough, for the same reason the text-search rule was not: the reach for
/// `awk` happens before the rule is recalled. It is the more expensive half, too. A search returns
/// matching lines; a log read returns the file, and a 32 MB server log read three times over is
/// three times its size in cloud tokens for an answer a local model gives for nothing.
///
/// Narrow on purpose, and narrower than the sentence it enforces. What is refused is a reader
/// pointed at something log-shaped with no bound on how much of it comes back. A bounded look is
/// not refused and must not be: the block asks for exactly that while a long run is going, and a
/// refusal that swallowed `tail -20` would contradict a live instruction.
///
/// What it does not catch, said plainly rather than left to be discovered: a script that opens the
/// file itself. `python read-it.py` names no path on the command line, and nothing here can see
/// inside it. The rule covers that; this gate does not, the way the search gate covers `grep` and
/// not a program somebody wrote to walk the tree.
/// </summary>
internal static class WholeLogRefusal
{
    /// <summary>
    /// Readers with no bounded form. Whatever they are pointed at arrives whole, so the only
    /// question is what they were pointed at.
    ///
    /// `awk` is here because it is what actually happened: a 32 MB log summarised into a reply
    /// several times over while the rule about logs sat unenforced two sections away. `strings`
    /// earns its place the same way — it is a reader of machine output that happens not to be
    /// text.
    /// </summary>
    private static readonly HashSet<string> WholeFileReaders = new(StringComparer.Ordinal)
    {
        "cat", "type", "more", "less", "awk", "strings",
    };

    /// <summary>
    /// Readers that take a count. Refused without one, allowed with a small one — which is the
    /// whole of the distinction this class draws.
    /// </summary>
    private static readonly HashSet<string> BoundedReaders = new(StringComparer.Ordinal)
    {
        "tail", "head", "get-content", "gc",
    };

    /// <summary>
    /// Where a look stops being a look. Two hundred lines is a couple of screens and answers "is
    /// this still moving"; nothing that answers "what went wrong in 32 MB" fits inside it, so the
    /// number separates the two questions rather than rationing one of them.
    /// </summary>
    private const int BoundedLines = 200;

    /// <summary>Bytes, for the readers that count in them. Twenty kilobytes is the same look.</summary>
    private const int BoundedBytes = 20_000;

    /// <summary>
    /// Extensions that say the file is machine output. `.json` is deliberately not one: a
    /// configuration file is read the ordinary way, and `.jsonl` — a line per record — is the
    /// shape a log takes when it is structured.
    /// </summary>
    private static readonly string[] LogExtensions = [".log", ".jsonl", ".ndjson"];

    /// <summary>
    /// Following never ends. A count beside it changes nothing: the call does not return, which is
    /// its own reason to refuse it in a tool call, and every line after the first N still arrives.
    /// </summary>
    private static readonly HashSet<string> FollowFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-f", "--follow", "-wait", "--retry", "-follow",
    };

    public static bool Refuses(string? command) => ReasonFor(command) is not null;

    public static string? ReasonFor(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        foreach (var words in CommandLineScan.Commands(command))
        {
            if (ReaderIn(words) is not { } reader)
            {
                continue;
            }

            if (!words.Any(word => IsLogShaped(word.Text)))
            {
                continue;
            }

            if (Bounded(words, reader))
            {
                continue;
            }

            return string.Format(CultureInfo.InvariantCulture, WholeLogRefused, reader);
        }

        return null;
    }

    /// <summary>
    /// The reader this command runs, by the name it was typed under, or null for a command that
    /// reads nothing.
    ///
    /// Every unquoted word is considered rather than only the first, because a runner puts words
    /// between the line's start and the program — `sudo cat`, `xargs cat` — and the search gate
    /// learned the same lesson the same way. A quoted word is text and is never a program.
    /// </summary>
    private static string? ReaderIn(IReadOnlyList<CommandWord> words)
    {
        foreach (var word in words)
        {
            if (word.Quoted || IsSwitch(word.Text))
            {
                continue;
            }

            var name = Normalize(word.Text);
            if (WholeFileReaders.Contains(name) || BoundedReaders.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    private static bool Bounded(IReadOnlyList<CommandWord> words, string reader)
    {
        if (!BoundedReaders.Contains(reader))
        {
            return false;
        }

        if (words.Any(word => !word.Quoted && IsFollow(word.Text)))
        {
            return false;
        }

        for (var index = 0; index < words.Count; index++)
        {
            var text = words[index].Text;
            if (words[index].Quoted || !IsSwitch(text))
            {
                continue;
            }

            var next = index + 1 < words.Count ? words[index + 1].Text : null;
            if (CountAfter(text, next) is { } count)
            {
                return count.Bytes ? count.Value <= BoundedBytes : count.Value <= BoundedLines;
            }
        }

        return false;
    }

    /// <summary>
    /// The count a flag carries, whether it is joined to the flag, follows it as its own word, or
    /// is the flag — `tail -20` is how the bounded look is usually typed, and it has no flag name
    /// at all.
    /// </summary>
    private static (int Value, bool Bytes)? CountAfter(string flag, string? next)
    {
        var bare = flag.TrimStart('-');
        if (bare.Length > 0 && bare.All(char.IsAsciiDigit) && flag.StartsWith('-'))
        {
            return (int.Parse(bare, CultureInfo.InvariantCulture), false);
        }

        var equals = flag.IndexOf('=', StringComparison.Ordinal);
        var name = (equals > 0 ? flag[..equals] : flag).ToLowerInvariant();
        var joined = equals > 0 ? flag[(equals + 1)..] : null;
        var bytes = name is "-c" or "--bytes";
        if (name is not ("-n" or "--lines" or "-c" or "--bytes" or "-totalcount" or "-first"
            or "-tail" or "-head" or "-last"))
        {
            return null;
        }

        var value = joined ?? next;
        if (value is null)
        {
            return null;
        }

        // `tail -n +100` counts from the start of the file, which is the rest of it.
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? (parsed, bytes)
            : null;
    }

    private static bool IsFollow(string token)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        return FollowFlags.Contains(equals > 0 ? token[..equals] : token);
    }

    /// <summary>
    /// Whether a word names machine output: by its extension, or by sitting in a directory that
    /// says so. The directory half is what catches an installer log written as
    /// `LocalAi-installer-logs/install-20260908.txt`, which carries no extension worth reading.
    /// </summary>
    private static bool IsLogShaped(string token)
    {
        if (token.Length == 0 || IsSwitch(token))
        {
            return false;
        }

        var path = token.Replace('\\', '/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (LogExtensions.Any(
                extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var directories = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return directories.Length > 1 &&
               directories[..^1].Any(segment =>
                   segment.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                   segment.EndsWith("-logs", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSwitch(string token) =>
        token.StartsWith('-') ||
        (token.StartsWith('/') && token.Length <= 3 && token.LastIndexOf('/') == 0);

    private static string Normalize(string token)
    {
        var name = token.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4].ToLowerInvariant()
            : name.ToLowerInvariant();
    }

    /// <summary>
    /// English on every machine, for the reason <see cref="TextSearchRefusal"/> gives about its
    /// own: this is the rule <c>ManagedInstructionBlock</c> installs, executed rather than
    /// restated, and that block is one English constant with no translated pair.
    /// </summary>
    private const string WholeLogRefused =
        "`{0}` reads a whole log into this conversation, which Local AI routes to a local "
        + "model: triage_log for a short one, `localai triage <file>` on the command line "
        + "for a whole log. The command line is the one for a big file because it prints "
        + "where it has got to while it runs — an MCP call says nothing between the call "
        + "and the answer, which on a large log is minutes of silence. Every line read "
        + "here is paid for in cloud tokens; the local model reads all of it and spends "
        + "none. A bounded look is not refused: `tail -20`, `head -n 50` or a line range "
        + "still answers whether a run is moving. If the whole file genuinely has to come "
        + "into the reply, ask first and say what for.";
}
