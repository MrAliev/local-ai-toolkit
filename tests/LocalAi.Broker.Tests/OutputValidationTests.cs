using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Every model measured on this machine quoted the right line of code under the wrong line
/// number. That is the one failure the three text models share, and the routes that would catch
/// it have named `CodeReferences` all along while nothing read the field.
///
/// The check is against the material the prompt carried, because that is what the broker has: it
/// is a service with no repository and no working directory, and a path in an answer may name a
/// file that was never sent — or no file at all. Where the material is absent the answer passes,
/// because the check could not be made, which is a different thing from passing it.
/// </summary>
public sealed class OutputValidationTests
{
    private static readonly ModelRoutingCatalog Catalog = ModelRoutingCatalog.LoadEmbedded();

    [Theory]
    [InlineData(false, StructuredValidationStatus.NotRequested)]
    [InlineData(true, StructuredValidationStatus.Valid)]
    public void Only_an_explicit_contract_can_report_structured_validation_success(bool withSchema, StructuredValidationStatus status)
    {
        var request = LocalJobRequestFactory.CreateRoutedChat("structured", LocalJobPriority.Foreground,
            LocalTaskProfile.Classification, "Classify", null, null,
            new LocalWorkloadMetadata(10, 10, 0, 0, 0, LocalDurationClass.Short),
            outputSchema: withSchema ? new StructuredOutputSchema(1, [new("label", StructuredFieldType.String)]) : null);
        var selection = new ModelSelection(LocalTaskProfile.Classification, "qwen3.5:9b", 2048, false, false, Catalog.CatalogVersion, "test");
        var result = OutputValidation.For(Catalog, request, selection,
            new BrokerExecutionResult(JsonSerializer.SerializeToElement(new ChatJobOutput("{\"label\":\"bug\"}"))));
        Assert.True(result.Passed);
        Assert.Equal(status, result.StructuredValidation!.Status);
        Assert.Equal(withSchema, result.StructuredValidation.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"label\":17}")]
    [InlineData("{}")]
    [InlineData("Answer: {\"label\":\"bug\"}")]
    public void Explicit_structured_contract_rejects_invalid_model_content(string answer)
    {
        var schema = new StructuredOutputSchema(1, [new("label", StructuredFieldType.String, AllowedValues: ["bug", "question"])]);
        var request = LocalJobRequestFactory.CreateRoutedChat("structured", LocalJobPriority.Foreground,
            LocalTaskProfile.Classification, "Classify this message.", null, null,
            new LocalWorkloadMetadata(22, 100, 0, 0, 0, LocalDurationClass.Short), outputSchema: schema);
        var selection = new ModelSelection(LocalTaskProfile.Classification, "qwen3.5:9b", 2048, false, false, Catalog.CatalogVersion, "test");
        var result = OutputValidation.For(Catalog, request, selection,
            new BrokerExecutionResult(JsonSerializer.SerializeToElement(new { content = answer })));
        Assert.False(result.Passed);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, result.Outcome);
        Assert.NotNull(result.StructuredValidation);
        Assert.False(result.StructuredValidation.IsValid);
    }

    /// <summary>
    /// The measured shape: a file of fifty-eight lines, and an answer confidently citing line 91.
    /// </summary>
    [Fact]
    public void A_line_beyond_the_end_of_the_file_the_prompt_carried_is_a_structural_failure()
    {
        var result = Validate(
            prompt: Bundle("PaymentService.cs", lines: 58),
            answer: "The dereference is at `PaymentService.cs:91`, inside ChargeAsync.");

        Assert.False(result.Passed);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, result.Outcome);
    }

    [Fact]
    public void A_line_the_file_actually_has_passes()
    {
        var result = Validate(
            prompt: Bundle("PaymentService.cs", lines: 58),
            answer: "The dereference is at `PaymentService.cs:23`, inside ChargeAsync.");

        Assert.True(result.Passed);
    }

    /// <summary>
    /// A reference to something the prompt never carried cannot be checked, and an answer is not
    /// wrong for mentioning a file that was not sent. Passing here says the check did not apply,
    /// which is why the code it passes under names the reason.
    /// </summary>
    [Fact]
    public void A_reference_to_a_file_the_prompt_never_carried_is_not_a_failure()
    {
        var result = Validate(
            prompt: Bundle("PaymentService.cs", lines: 58),
            answer: "See `OrdersController.cs:4100` for the mapping.");

        Assert.True(result.Passed);
    }

    [Fact]
    public void An_answer_that_cites_nothing_passes()
    {
        var result = Validate(
            prompt: Bundle("PaymentService.cs", lines: 58),
            answer: "Nothing in the supplied file dereferences a null reference.");

        Assert.True(result.Passed);
    }

    private static ModelValidationResult Validate(string prompt, string answer)
    {
        var request = LocalJobRequestFactory.CreateRoutedChat(
            "validation",
            LocalJobPriority.Foreground,
            LocalTaskProfile.CodeAnalysis,
            prompt,
            system: null,
            imagesBase64: null,
            new LocalWorkloadMetadata(
                prompt.Length,
                4_000,
                1,
                0,
                0,
                LocalDurationClass.Medium));
        var selection = new ModelSelection(
            LocalTaskProfile.CodeAnalysis,
            "gpt-oss:20b",
            8192,
            IsExperimentalAttempt: false,
            UsedFallback: false,
            Catalog.CatalogVersion,
            "test");
        var body = JsonSerializer.SerializeToElement(new ChatJobOutput(answer));

        return OutputValidation.For(
            Catalog,
            request,
            selection,
            new BrokerExecutionResult(body));
    }

    /// <summary>
    /// The shape LocalTasks.AskAsync builds: a header naming the file, then its content. The
    /// validator has to read the same shape, so this fixture is written the way that method
    /// writes it rather than the way a test would find convenient.
    /// </summary>
    private static string Bundle(string name, int lines) =>
        $"list the defects\n\n--- FILE: R:\\repo\\src\\{name} ---\n" +
        string.Join("\n", Enumerable.Range(1, lines).Select(line => $"var line{line} = {line};")) +
        "\n\n";
}
