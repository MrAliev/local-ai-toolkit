using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

/// <summary>What <c>localai ask</c> was asked to do.</summary>
public sealed record AskRequest(
    string Prompt,
    IReadOnlyList<string> Files,
    LocalTaskProfile Profile,
    string? Model,
    string? Language = null,
    string? SchemaPath = null);

/// <summary>
/// A mechanical task over known files, run on a local model: summarise this, list every method
/// that does X, collect the TODOs.
///
/// The same <c>LocalTasks.AskAsync</c> the <c>ask_local</c> MCP tool calls. This is a second
/// entry point to it, for a person at a prompt and for an agent whose MCP server is not running —
/// the fallback this product's own instruction block tells every machine to use.
/// </summary>
public static class AskCommand
{
    /// <summary>
    /// The instruction is positional because it is required and cannot be defaulted; the files
    /// are positional and variadic because a shell glob is how anybody names sixty of them, and
    /// sixty-four is the limit <c>LocalTasks</c> enforces.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out AskRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? prompt = null;
        var files = new List<string>();
        var profile = LocalTaskProfile.ShortSummary;
        string? model = null;
        string? language = null;
        string? schemaPath = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--profile":
                case "--model":
                case "--language":
                case "--schema":
                    if (index + 1 >= arguments.Count)
                    {
                        refusal = new CommandRefusal(
                            argument.TrimStart('-') + "_value_missing",
                            CliText.OptionValueMissing("ask", argument, CliUsage.Ask));
                        return false;
                    }

                    var value = arguments[++index];
                    if (argument == "--schema")
                    {
                        if (schemaPath is not null || string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                        {
                            refusal = new CommandRefusal("schema_invalid_argument", "Provide --schema once with a schema file path.");
                            return false;
                        }
                        schemaPath = value;
                        break;
                    }
                    if (argument == "--model")
                    {
                        model = value;
                        break;
                    }

                    if (argument == "--language")
                    {
                        // Checked before it is used, because OutputCulture.Resolve answers an
                        // unknown value with English rather than refusing — so a caller who asked
                        // for German would be handed the very failure this option exists to fix.
                        if (!LanguageOption.IsSupported(value))
                        {
                            refusal = new CommandRefusal(
                                "language_not_supported",
                                CliText.LanguageNotSupported(
                                    "ask",
                                    value,
                                    LanguageOption.Supported));
                            return false;
                        }

                        language = value;
                        break;
                    }

                    if (!Enum.TryParse<LocalTaskProfile>(value, ignoreCase: true, out var parsed) ||
                        !Enum.IsDefined(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_unknown",
                            CliText.ProfileUnknown("ask", value, TextChatProfiles));
                        return false;
                    }

                    // A profile that exists but routes to an image or embedding model is a
                    // different mistake from a word that is not a profile at all, and the reader
                    // who made each one needs a different sentence.
                    if (!LocalTasks.IsTextChatProfile(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_not_supported",
                            CliText.AskProfileNotSupported(value, TextChatProfiles));
                        return false;
                    }

                    // A third mistake, and its own code. profile_not_supported tells a program to
                    // retry with another profile from the list, and retrying a log under
                    // CodeAnalysis is the defect this refusal exists to stop: the same one prompt,
                    // a different model, and nothing on screen saying the triage never ran.
                    if (LocalTasks.HasOwnPipeline(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_elsewhere",
                            CliText.AskProfileHasOwnCommand(value, CliUsage.Triage));
                        return false;
                    }

                    profile = parsed;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument("ask", argument, CliUsage.Ask));
                        return false;
                    }

                    if (prompt is null)
                    {
                        prompt = argument;
                        break;
                    }

                    files.Add(argument);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            refusal = new CommandRefusal(
                "prompt_missing",
                CliText.AskPromptMissing(CliUsage.Ask));
            return false;
        }

        if (schemaPath is not null && profile is not (LocalTaskProfile.Extraction or LocalTaskProfile.Classification))
        {
            refusal = new CommandRefusal("schema_profile_not_supported", "--schema requires --profile Extraction or Classification.");
            return false;
        }
        request = new AskRequest(prompt, files, profile, model, language, schemaPath);
        return true;
    }

    public static async Task<StructuredOutputSchema?> ReadSchemaAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path is null) return null;
        using var reader = new StreamReader(Path.GetFullPath(path));
        var buffer = new char[StructuredOutputSchema.MaximumSchemaCharacters + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return StructuredOutputSchema.Parse(new string(buffer, 0, length));
    }

    /// <summary>
    /// Computed from the enum through the same predicates the call itself checks, so this cannot
    /// name a profile the call would reject. Both of them: a list that offered LogTriage while
    /// LogTriage refused would send a reader from one refusal straight into another, which is the
    /// failure the first predicate's own comment says it exists to prevent.
    /// </summary>
    private static string TextChatProfiles =>
        string.Join(
            "|",
            Enum.GetValues<LocalTaskProfile>()
                .Where(profile =>
                    LocalTasks.IsTextChatProfile(profile) &&
                    !LocalTasks.HasOwnPipeline(profile)));
}
