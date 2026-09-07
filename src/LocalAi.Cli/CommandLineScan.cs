using System.Text;
using System.Text.RegularExpressions;

namespace LocalAi.Cli;

/// <summary>
/// Reads a command line far enough to answer one question: which programs does this line run?
///
/// It replaces a regular expression that looked for a search tool at the start of a command, after
/// a separator, or immediately behind a word that runs another command. That shape had two
/// failures and neither could be patched out of it, because both are about structure the pattern
/// could not see.
///
/// It did not know a quote. `(` and `|` are separators in a shell and ordinary punctuation in a
/// sentence, so `git commit -m 'text search (grep) is refused'` was refused — a commit message
/// this repository would plausibly write about this very feature. And it did not know that a shell
/// runs what it is handed, so `sh -c 'grep -r foo .'` was allowed, as were `sudo -u root grep` and
/// `xargs -0 grep`, because a runner had to be followed by the tool immediately and any flag at
/// all broke the adjacency.
///
/// So: quotes decide what is text, separators decide where a command starts, and a shell handed a
/// script is followed into it. What this is not is a shell. It does not expand variables, resolve
/// aliases or know what is on the PATH; it reads the words as written, which is all that is needed
/// to tell a search from a sentence about one.
/// </summary>
internal static class CommandLineScan
{
    /// <summary>
    /// How far a script inside a script inside a script is followed. Four is past anything written
    /// by hand, and the bound is what stops a pathological line from costing the hook its budget.
    /// </summary>
    private const int MaximumDepth = 4;

    /// <summary>
    /// Words that run another program, so the words after them are still command position. Once
    /// one is seen every remaining plain word in that command is a candidate, rather than only the
    /// next one: modelling which flags of `sudo` and `xargs` take a value is a second thing to keep
    /// correct, and getting it wrong is how `sudo -u root grep` was allowed.
    /// </summary>
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "xargs", "time", "timeout", "command", "env",
        "nice", "nohup", "setsid", "stdbuf", "git",
    };

    /// <summary>
    /// Shells that run a script given to them, whether as an argument or on their input.
    /// </summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "dash", "ksh", "ash", "busybox", "pwsh", "powershell", "cmd",
    };

    /// <summary>The flags that say "what follows is a script", not an argument.</summary>
    private static readonly HashSet<string> ScriptFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "-command", "--command", "/c", "/k", "-file", "-f",
    };

    /// <summary>
    /// `-exec` hands the rest of the line to another program, which is how `find . -exec grep -l
    /// foo {} +` sweeps a tree without ever putting a search tool in command position.
    /// </summary>
    private static readonly HashSet<string> ExecFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-exec", "-execdir", "-ok", "-okdir",
    };

    private static readonly Regex Heredoc = new(
        @"<<-?\s*(['""]?)(\w+)\1(?<body>.*?)^[ \t]*\2[ \t]*$",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Every word this line could be running a program by, normalised to a bare lowercase name:
    /// the directory dropped and any `.exe` with it, so `/usr/bin/grep` and `C:\tools\rg.exe` are
    /// the words they name.
    /// </summary>
    public static IReadOnlyList<string> ProgramWords(string script)
    {
        var found = new List<string>();
        Collect(script, 0, found);
        return found;
    }

    private static void Collect(string script, int depth, List<string> found)
    {
        if (depth > MaximumDepth || string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        var body = StripHeredocs(script, depth, found);
        foreach (var command in SplitCommands(body))
        {
            CollectFromOneCommand(command, depth, found);
        }
    }

    /// <summary>
    /// Takes heredoc bodies out of the line, and follows the ones that are scripts.
    ///
    /// The rule this replaces said a heredoc body is "data, not commands", which is true of `cat
    /// &lt;&lt;EOF &gt; notes.md` and false of `bash &lt;&lt;EOF` — feeding commands to an
    /// interpreter is what a heredoc into a shell is for. So which it is depends on what is being
    /// fed, and that is the program of the command the heredoc opens on.
    /// </summary>
    private static string StripHeredocs(string script, int depth, List<string> found)
    {
        return Heredoc.Replace(script, match =>
        {
            var owner = script[..match.Index];
            var start = owner.LastIndexOfAny(['\n', ';', '|', '&']);
            var opening = start < 0 ? owner : owner[(start + 1)..];
            var programs = new List<string>();
            CollectFromOneCommand(opening, depth, programs);
            if (programs.Any(Interpreters.Contains))
            {
                Collect(match.Groups["body"].Value, depth + 1, found);
            }

            return " ";
        });
    }

    private static void CollectFromOneCommand(string command, int depth, List<string> found)
    {
        var tokens = Tokenize(command);
        var expectingProgram = true;
        var interpreter = false;
        var scriptFollows = false;
        // Sticky, not "the next word". `sudo -u root grep` puts a flag's value between the runner
        // and the program, and modelling which flags of which runner take one is a second thing to
        // keep correct — the version that tried it allowed exactly this line.
        var afterRunner = false;

        foreach (var (text, quoted) in tokens)
        {
            if (scriptFollows)
            {
                // The argument of `-c` is a script whether it was quoted or not, and it is the
                // one place a quoted word is read as commands rather than as text.
                Collect(text, depth + 1, found);
                scriptFollows = false;
                continue;
            }

            if (quoted)
            {
                // Text. The one thing read out of it is a `$(...)` substitution, which is a
                // command written inside a string rather than a string containing brackets.
                foreach (var nested in Substitutions(text))
                {
                    Collect(nested, depth + 1, found);
                }

                expectingProgram = false;
                continue;
            }

            if (IsSwitch(text))
            {
                if (interpreter && ScriptFlags.Contains(text))
                {
                    scriptFollows = true;
                }
                else if (ExecFlags.Contains(text))
                {
                    expectingProgram = true;
                }

                continue;
            }

            if (text.Length == 0 || IsAssignment(text))
            {
                continue;
            }

            if (!expectingProgram)
            {
                continue;
            }

            var name = Normalize(text);
            found.Add(name);
            interpreter |= Interpreters.Contains(name);
            afterRunner |= Runners.Contains(name);
            expectingProgram = afterRunner || interpreter;
        }
    }

    /// <summary>
    /// Splits on the separators that end a command, outside quotes. The backtick is deliberately
    /// not one: it is a substitution in a shell and also how prose quotes a command name, and the
    /// second reading is the common one in this repository's own documents.
    /// </summary>
    private static List<string> SplitCommands(string script)
    {
        var commands = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        for (var index = 0; index < script.Length; index++)
        {
            var character = script[index];
            if (quote != '\0')
            {
                current.Append(character);
                if (character == quote && (quote == '\'' || script[index - 1] != '\\'))
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                current.Append(character);
                continue;
            }

            if (character is '|' or ';' or '&' or '\n' or '(' or ')' or '{' or '}')
            {
                commands.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        commands.Add(current.ToString());
        return commands;
    }

    /// <summary>
    /// The words of one command, each marked by whether it arrived inside quotes. A quoted word is
    /// text: that is the whole of why a sentence mentioning `grep` is not a search.
    /// </summary>
    private static List<(string Text, bool Quoted)> Tokenize(string command)
    {
        var tokens = new List<(string, bool)>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        var quote = '\0';

        void Flush()
        {
            if (started)
            {
                tokens.Add((current.ToString(), quoted));
            }

            current.Clear();
            quoted = false;
            started = false;
        }

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote != '\0')
            {
                if (character == quote && (quote == '\'' || command[index - 1] != '\\'))
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                quoted = true;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                Flush();
                continue;
            }

            started = true;
            current.Append(character);
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// `$(...)` inside a double-quoted string. Nothing else is read out of quoted text.
    /// </summary>
    private static IEnumerable<string> Substitutions(string text)
    {
        var index = 0;
        while ((index = text.IndexOf("$(", index, StringComparison.Ordinal)) >= 0)
        {
            var close = text.IndexOf(')', index + 2);
            if (close < 0)
            {
                yield break;
            }

            yield return text[(index + 2)..close];
            index = close + 1;
        }
    }

    /// <summary>
    /// A switch, not a program. `-` is unambiguous; `/` is a switch in `cmd` and the root of a
    /// path everywhere else, so only a short one with nothing further in it counts — which keeps
    /// `/usr/bin/grep` a program.
    /// </summary>
    private static bool IsSwitch(string token) =>
        token.StartsWith('-') ||
        (token.StartsWith('/') && token.Length <= 3 && token.LastIndexOf('/') == 0);

    private static bool IsAssignment(string token)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 && token[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

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
}
