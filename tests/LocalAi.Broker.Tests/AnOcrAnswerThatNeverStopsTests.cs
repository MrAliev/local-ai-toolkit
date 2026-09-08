using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The `Ocr` route has named `OcrCompleteness` since the routing table existed, and the check was
/// never performed. What it has to catch was measured on this machine on 2026-09-09: given a
/// 1200x300 image carrying the single line "LOCAL AI OCR 2048", `glm-ocr:bf16` answered with that
/// line 727 times and did not stop, taking 22 s. `qwen3-vl:8b-instruct-q8_0` answered once, in
/// 6.5 s.
///
/// The same model on a dense 1920x1080 console screenshot transcribed all 33 lines exactly, in
/// 7.5 s -- better and faster than qwen3-vl, which merged one wrapped line. So the defect belongs
/// to the input rather than to the model or the prompt, and a check that catches it is what lets
/// the faster transcriber stay on the route.
/// </summary>
public sealed class AnOcrAnswerThatNeverStopsTests
{
    private static readonly ModelRoutingCatalog Catalog = ModelRoutingCatalog.LoadEmbedded();

    /// <summary>
    /// The measured shape, at the measured size: one line, 727 times.
    /// </summary>
    [Fact]
    public void One_line_repeated_past_the_end_of_the_image_is_a_structural_failure()
    {
        var result = Validate(Repeat("LOCAL AI OCR 2048", 727));

        Assert.False(result.Passed);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, result.Outcome);
    }

    /// <summary>
    /// The other half of the measurement: a long, faithful transcript must not be mistaken for a
    /// loop because it is long.
    /// </summary>
    [Fact]
    public void A_faithful_transcript_of_a_dense_page_passes()
    {
        var result = Validate(Lines(60, line => $"at Frame {line} in Program.cs:line {line}"));

        Assert.True(result.Passed);
    }

    /// <summary>
    /// A label, an error toast, a column heading: a short answer that repeats itself is the honest
    /// reading of an image that repeats itself, and nothing distinguishes it from a loop. The
    /// check declines to judge rather than guessing, and the floor is what makes that explicit.
    /// </summary>
    [Fact]
    public void An_answer_too_short_for_variety_to_mean_anything_passes()
    {
        var result = Validate(Repeat("SIGN OUT", 39));

        Assert.True(result.Passed);
    }

    /// <summary>
    /// A real transcript repeats: a table column, a log line, a status token. Repetition alone is
    /// not the signal -- the absence of anything else is.
    /// </summary>
    [Fact]
    public void A_transcript_whose_lines_legitimately_repeat_passes()
    {
        var result = Validate(Lines(60, line => $"row {line / 2} PASSED"));

        Assert.True(result.Passed);
    }

    private static string Repeat(string line, int times) =>
        string.Join("\n", Enumerable.Repeat(line, times));

    private static string Lines(int count, Func<int, string> line) =>
        string.Join("\n", Enumerable.Range(1, count).Select(line));

    private static ModelValidationResult Validate(string answer)
    {
        const string prompt = "Transcribe every line of text exactly as it appears.";
        var workload = new LocalWorkloadMetadata(
            prompt.Length,
            4_000,
            0,
            1,
            2_073_600,
            LocalDurationClass.Medium);
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "validation",
            LocalJobPriority.Foreground,
            LocalTaskProfile.Ocr,
            prompt,
            system: null,
            imagesBase64: null,
            workload);
        var selection = new ModelSelection(
            LocalTaskProfile.Ocr,
            "glm-ocr:bf16",
            8192,
            IsExperimentalAttempt: true,
            UsedFallback: false,
            Catalog.CatalogVersion,
            "test",
            workload);
        var body = JsonSerializer.SerializeToElement(new ChatJobOutput(answer));

        return OutputValidation.For(
            Catalog,
            request,
            selection,
            new BrokerExecutionResult(body));
    }
}
