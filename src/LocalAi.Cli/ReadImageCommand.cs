using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Core.Resources;

namespace LocalAi.Cli;

/// <summary>What <c>localai read-image</c> was asked to look at.</summary>
/// <param name="Together">
/// Ask about the images as a set rather than one at a time. Off by default, because one at a time
/// is what makes the progress a count; on, there is one request and nothing to count inside it.
/// </param>
public sealed record ReadImageRequest(
    string Question,
    IReadOnlyList<string> Images,
    LocalTaskProfile Profile,
    string? Model,
    bool Together = false,
    string? Language = null,
    string? PdfPages = null,
    string? PdfOutputPath = null,
    string? PdfCheckpointDirectory = null);

/// <summary>
/// Images on disk, read by a local vision model: a screenshot, a scanned page, a photographed
/// table, a diagram.
///
/// Same grammar as <c>ask</c> — the instruction first because it is required, the files after it
/// because a glob is how anybody names eight of them. The question has no default and does not
/// gain one here: "transcribe the error text" and "list every row" produce different answers, and
/// a default would quietly pick one.
/// </summary>
public static class ReadImageCommand
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReadImageRequest? request,
        out CommandRefusal? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        refusal = null;

        string? question = null;
        var images = new List<string>();
        var profile = LocalTaskProfile.VisualAnalysis;
        string? model = null;
        string? language = null;
        string? pdfPages = null;
        string? pdfOutputPath = null;
        string? pdfCheckpointDirectory = null;
        var together = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                // A flag rather than an option: it takes no value, and the reader either wants
                // the images read as a set or does not.
                case "--together":
                    together = true;
                    break;

                case "--profile":
                case "--model":
                case "--language":
                case "--pages":
                case "--out":
                case "--checkpoint":
                    if (index + 1 >= arguments.Count)
                    {
                        refusal = new CommandRefusal(
                            argument.TrimStart('-') + "_value_missing",
                            CliText.OptionValueMissing(
                                "read-image",
                                argument,
                                CliUsage.ReadImage));
                        return false;
                    }

                    var value = arguments[++index];
                    if (argument is "--out" or "--checkpoint")
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            refusal = new CommandRefusal(argument.TrimStart('-') + "_value_missing", CliText.OptionValueMissing("read-image", argument, CliUsage.ReadImage));
                            return false;
                        }
                        if (argument == "--out") pdfOutputPath = value;
                        else pdfCheckpointDirectory = value;
                        break;
                    }
                    if (argument == "--pages")
                    {
                        try { _ = PdfPageSelection.Parse(value); }
                        catch (ArgumentException error)
                        {
                            refusal = new CommandRefusal("pdf_pages_invalid", error.Message);
                            return false;
                        }
                        pdfPages = value;
                        break;
                    }
                    if (argument == "--model")
                    {
                        model = value;
                        break;
                    }

                    if (argument == "--language")
                    {
                        // Checked before it is used: OutputCulture.Resolve answers an unknown
                        // value with English rather than refusing, so a caller who asked for
                        // German would be handed the very failure this option exists to fix.
                        if (!LanguageOption.IsSupported(value))
                        {
                            refusal = new CommandRefusal(
                                "language_not_supported",
                                CliText.LanguageNotSupported(
                                    "read-image",
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
                            CliText.ProfileUnknown("read-image", value, ImageProfiles));
                        return false;
                    }

                    if (!LocalTasks.IsImageProfile(parsed))
                    {
                        refusal = new CommandRefusal(
                            "profile_not_supported",
                            CliText.ReadImageProfileNotSupported(value, ImageProfiles));
                        return false;
                    }

                    profile = parsed;
                    break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        refusal = new CommandRefusal(
                            "argument_unknown",
                            CliText.CommandUnknownArgument(
                                "read-image",
                                argument,
                                CliUsage.ReadImage));
                        return false;
                    }

                    // A first argument that is a bare image path is a forgotten question
                    // rather than a question about a file: `localai read-image shot.png` used to
                    // be told it needed an image, which is the one thing it had. A question that
                    // merely ends in a file name — "what is in shot.png" — has a space in it and
                    // stays a question.
                    if (question is null &&
                        !((LocalTasks.IsImageFile(argument) || LocalTasks.IsPdfFile(argument)) &&
                          !argument.Contains(' ', StringComparison.Ordinal)))
                    {
                        question = argument;
                        break;
                    }

                    if (!LocalTasks.IsImageFile(argument) && !LocalTasks.IsPdfFile(argument))
                    {
                        refusal = new CommandRefusal(
                            "file_not_image",
                            LocalLmText.NotAnImage(argument));
                        return false;
                    }

                    images.Add(argument);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            refusal = new CommandRefusal(
                "prompt_missing",
                CliText.ReadImageQuestionMissing(CliUsage.ReadImage));
            return false;
        }

        if (images.Count == 0)
        {
            refusal = new CommandRefusal(
                "source_missing",
                CliText.ReadImageNoImages(CliUsage.ReadImage));
            return false;
        }

        if (images.Any(LocalTasks.IsPdfFile))
        {
            if (images.Count != 1)
            {
                refusal = new CommandRefusal("pdf_single_input_required", LocalLmText.PdfOnlyInput);
                return false;
            }
            if (together)
            {
                refusal = new CommandRefusal("pdf_together_unsupported", LocalLmText.PdfTogetherUnsupported);
                return false;
            }
        }

        else if (pdfPages is not null)
        {
            refusal = new CommandRefusal("pdf_pages_requires_pdf", LocalLmText.PdfPagesOnly);
            return false;
        }

        if (pdfOutputPath is not null && (!images.Any(LocalTasks.IsPdfFile) || profile != LocalTaskProfile.Ocr))
        {
            refusal = new CommandRefusal("pdf_output_requires_ocr", LocalLmText.PdfOutputOnly);
            return false;
        }
        if (pdfCheckpointDirectory is not null && (!images.Any(LocalTasks.IsPdfFile) || profile != LocalTaskProfile.Ocr))
        {
            refusal = new CommandRefusal("pdf_checkpoint_requires_ocr", LocalLmText.PdfCheckpointOnly);
            return false;
        }
        request = new ReadImageRequest(question, images, profile, model, together, language, pdfPages, pdfOutputPath, pdfCheckpointDirectory);
        return true;
    }

    /// <summary>
    /// Computed from the enum through the predicate the task itself checks, so this cannot name a
    /// profile the call would reject.
    /// </summary>
    private static string ImageProfiles =>
        string.Join(
            "|",
            Enum.GetValues<LocalTaskProfile>().Where(LocalTasks.IsImageProfile));
}
