using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// The check a route asks for, performed on the answer the model gave.
///
/// The catalogue has carried a <c>Validator</c> on every route since the routing table existed,
/// and nothing read it: every member of <see cref="LocalOutputValidator"/> had exactly one
/// reference, its own declaration. The machinery around it was already built — the coordinator
/// runs a check after each attempt, records a structural failure against an experiment, and falls
/// back to another model — and the broker's own composition root passed no check at all, so the
/// default stood and everything passed.
///
/// This is where those checks live. It dispatches on the field the catalogue already carries, so
/// a route naming a check gets it and a route naming <c>None</c> is explicit about wanting none.
/// </summary>
public static partial class OutputValidation
{
    /// <summary>
    /// How many lines an answer needs before its variety says anything about it. Below this a
    /// legitimate reading can be one word repeated -- a label, an error toast, a column of
    /// identical cells -- and nothing tells that apart from a loop.
    /// </summary>
    private const int LinesBeforeVarietyMeansAnything = 40;

    /// <summary>
    /// The share of distinct lines below which an answer is a loop rather than a transcript,
    /// written as a divisor: one line in twenty. The measured failure was two distinct lines in
    /// 727.
    /// </summary>
    private const int DegenerateVarietyDivisor = 20;

    public static ModelValidationResult For(
        ModelRoutingCatalog catalog,
        LocalJobRequest request,
        ModelSelection selection,
        BrokerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(result);
        var validator = catalog.Route(selection.Profile).Validator;
        return validator switch
        {
            LocalOutputValidator.CodeReferences => CodeReferences(request, result),
            LocalOutputValidator.OcrCompleteness => OcrCompleteness(result),

            // Named rather than folded into a default, so a validator added to the enum without a
            // check here is a compile-time hole rather than a silent pass. The code carries the
            // name, so a receipt says which check was asked for even when none was performed.
            LocalOutputValidator.None or
            LocalOutputValidator.TranslationStructure or
            LocalOutputValidator.StructuredOutput =>
                ModelValidationResult.Pass(validator.ToString()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(selection),
                validator,
                "No check is defined for this validator."),
        };
    }

    /// <summary>
    /// Whether the file and line an answer cites exist in the material the prompt carried.
    ///
    /// Against the prompt, because that is what the broker has. It is a service with no repository
    /// and no working directory: it cannot open <c>PaymentService.cs</c> to count its lines, and a
    /// path in an answer may name a file that was never sent or no file at all.
    ///
    /// So the check is narrow on purpose. A reference to a file the prompt did not carry passes,
    /// because it was not established rather than because it was found sound — the two are
    /// different, and the code says which. What it does catch is the failure every model measured
    /// on this machine made: the right line of code quoted under a line number the file does not
    /// have.
    /// </summary>
    private static ModelValidationResult CodeReferences(
        LocalJobRequest request,
        BrokerExecutionResult result)
    {
        if (request.Payload is not ChatJobPayload { Prompt: { Length: > 0 } prompt })
        {
            return ModelValidationResult.Pass("code_references_no_material");
        }

        var bundled = BundledLineCounts(prompt);
        if (bundled.Count == 0)
        {
            return ModelValidationResult.Pass("code_references_no_material");
        }

        if (Answer(result) is not { Length: > 0 } answer)
        {
            return ModelValidationResult.Pass("code_references_no_answer");
        }

        foreach (var reference in Reference().Matches(answer).Cast<Match>())
        {
            var name = Path.GetFileName(reference.Groups["file"].Value);
            if (!bundled.TryGetValue(name, out var available) ||
                !int.TryParse(reference.Groups["line"].Value, out var line))
            {
                continue;
            }

            if (line < 1 || line > available)
            {
                return ModelValidationResult.Fail(
                    ModelExecutionOutcome.StructuralFailure,
                    "code_reference_out_of_range");
            }
        }

        return ModelValidationResult.Pass("code_references");
    }

    /// <summary>
    /// Whether an OCR answer stopped when the image ran out.
    ///
    /// Measured on this machine on 2026-09-09: a 1200x300 image carrying the single line
    /// "LOCAL AI OCR 2048" produced that line 727 times from <c>glm-ocr:bf16</c>, over 22 s, and
    /// once from <c>qwen3-vl:8b-instruct-q8_0</c>. On a dense 1920x1080 console screenshot the
    /// same model transcribed all 33 lines exactly and faster than qwen3-vl, so the defect is a
    /// property of sparse input rather than of the model or the prompt, and neither a model swap
    /// nor a reworded prompt removes it.
    ///
    /// Repetition on its own is not the signal: a real transcript repeats a table column, a log
    /// line, a status token. The absence of anything else is. So the check reads variety, not
    /// repeats, and it declines to judge an answer too short for variety to mean anything.
    ///
    /// Both numbers are calibrated between the two measured points rather than derived from a
    /// corpus, and deliberately far from each of them. A false failure costs a fallback run
    /// against a model that was going to be second choice anyway; a false pass hands over an
    /// answer in which 726 lines of 727 were noise.
    /// </summary>
    private static ModelValidationResult OcrCompleteness(BrokerExecutionResult result)
    {
        if (Answer(result) is not { Length: > 0 } answer)
        {
            return ModelValidationResult.Pass("ocr_completeness_no_answer");
        }

        var lines = answer
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length < LinesBeforeVarietyMeansAnything)
        {
            return ModelValidationResult.Pass("ocr_completeness_too_short_to_judge");
        }

        var distinct = lines.Distinct(StringComparer.Ordinal).Count();
        return distinct * DegenerateVarietyDivisor <= lines.Length
            ? ModelValidationResult.Fail(
                ModelExecutionOutcome.StructuralFailure,
                "ocr_repeats_without_stopping")
            : ModelValidationResult.Pass("ocr_completeness");
    }

    /// <summary>
    /// How many lines each file the prompt carried actually has, by file name.
    ///
    /// The header is the one <c>LocalTasks.AskAsync</c> writes, and it is matched rather than
    /// reconstructed: a validator that guessed the shape would stop checking the moment that
    /// method changed it, and would do so silently, by passing everything.
    ///
    /// By file name rather than by full path because an answer writes the name it was shown, and
    /// the header carries the resolved absolute path. Two bundled files of the same name in
    /// different directories collapse to one entry — the larger count wins, so the check refuses
    /// to call a line out of range on the strength of a collision it cannot resolve.
    /// </summary>
    private static Dictionary<string, int> BundledLineCounts(string prompt)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headers = FileHeader().Matches(prompt).Cast<Match>().ToArray();
        for (var index = 0; index < headers.Length; index++)
        {
            var header = headers[index];
            var start = header.Index + header.Length;
            var end = index + 1 < headers.Length
                ? headers[index + 1].Index
                : prompt.Length;
            var name = Path.GetFileName(header.Groups["path"].Value.Trim());
            if (string.IsNullOrEmpty(name) || end <= start)
            {
                continue;
            }

            var content = prompt[start..end].TrimEnd('\r', '\n');
            var lines = content.Length == 0
                ? 0
                : content.Count(character => character == '\n') + 1;
            counts[name] = counts.TryGetValue(name, out var known)
                ? Math.Max(known, lines)
                : lines;
        }

        return counts;
    }

    private static string? Answer(BrokerExecutionResult result)
    {
        try
        {
            return result.Body.Deserialize<ChatJobOutput>(LocalAiJson.Strict)?.Content;
        }
        catch (JsonException)
        {
            // Not a chat answer. A body this cannot read is not an answer this can judge, and a
            // check that fails on a shape it does not understand would blame the model for the
            // validator's own narrowness.
            return null;
        }
    }

    /// <summary>
    /// A file name with an extension, a colon, and a line number — the machine-checkable form, and
    /// the one every model measured here used. Prose such as "line 91 of PaymentService.cs" is
    /// deliberately not matched: inferring which number belongs to which name from a sentence is
    /// how a check starts failing answers that were right.
    /// </summary>
    [GeneratedRegex(
        @"(?<file>[^\s`'""(\[\]]+\.[A-Za-z][A-Za-z0-9]{0,7}):(?<line>\d{1,7})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex(
        @"^--- FILE: (?<path>.+?) ---\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FileHeader();
}
