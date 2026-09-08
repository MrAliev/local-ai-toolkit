using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

/// <summary>What <c>localai triage</c> was asked to read.</summary>
public sealed record TriageRequest(
    string? Path,
    bool FromStandardInput,
    string? Question,
    string? Model,
    string? Language = null);

/// <summary>
/// Machine output of any length, read by a local model that says what failed and why.
///
/// <c>dotnet build | localai triage</c> is the reason this belongs in a console at all, so a
/// piped log needs no argument. There is deliberately no <c>--text</c>: a log passed as an
/// argument meets the Windows command-line limit at about 32K, which is a short log, and standard
/// input is the console's spelling of that parameter.
/// </summary>
public static class TriageCommand
{
    /// <summary>
    /// <paramref name="piped"/> is <c>Console.IsInputRedirected</c> at the entry point, passed in
    /// so this can be tested without a console.
    ///
    /// The polarity is the safe one. False means definitely interactive, where reading standard
    /// input would hang a person's terminal with nothing on screen saying why — so it refuses.
    /// True means possibly piped, including the redirected-but-empty handle every agent, hook and
    /// MCP host gives a child process, so it reads and lets the emptiness be its own answer.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        bool piped,
        out TriageRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? path = null;
        var fromStandardInput = false;
        string? question = null;
        string? model = null;
        string? language = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            // Before the option test below: a lone dash is a filename to a parser and the
            // spelling of "standard input" to everybody else.
            if (argument == "-")
            {
                fromStandardInput = true;
                continue;
            }

            switch (argument)
            {
                case "--question":
                case "--model":
                case "--language":
                    if (index + 1 >= arguments.Count)
                    {
                        refusal = new CommandRefusal(
                            argument.TrimStart('-') + "_value_missing",
                            CliText.OptionValueMissing(
                                "triage",
                                argument,
                                CliUsage.Triage));
                        return false;
                    }

                    if (argument == "--question")
                    {
                        question = arguments[++index];
                    }
                    else if (argument == "--language")
                    {
                        // Checked before it is used: OutputCulture.Resolve answers an unknown
                        // value with English rather than refusing, so a caller who asked for
                        // German would be handed the very failure this option exists to fix.
                        var wanted = arguments[++index];
                        if (!LanguageOption.IsSupported(wanted))
                        {
                            refusal = new CommandRefusal(
                                "language_not_supported",
                                CliText.LanguageNotSupported(
                                    "triage",
                                    wanted,
                                    LanguageOption.Supported));
                            return false;
                        }

                        language = wanted;
                    }
                    else
                    {
                        model = arguments[++index];
                    }

                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument("triage", argument, CliUsage.Triage));
                        return false;
                    }

                    if (path is not null)
                    {
                        refusal = new CommandRefusal(
                            "source_ambiguous",
                            CliText.TriageOneLog(CliUsage.Triage));
                        return false;
                    }

                    path = argument;
                    break;
            }
        }

        // A named file means standard input is neither read nor claimed to be, so the MCP tool's
        // "exactly one source" refusal cannot arise here.
        if (path is not null)
        {
            request = new TriageRequest(path, FromStandardInput: false, question, model, language);
            return true;
        }

        if (!fromStandardInput && !piped)
        {
            refusal = new CommandRefusal(
                "source_missing",
                CliText.TriageNoSource(CliUsage.Triage));
            return false;
        }

        request = new TriageRequest(null, FromStandardInput: true, question, model, language);
        return true;
    }
}
