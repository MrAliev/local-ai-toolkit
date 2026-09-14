using System.Text.Json;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationGeneratorTests
{
    [Fact]
    public async Task Invalid_claim_is_repaired_with_validation_feedback_before_publication()
    {
        var fake = new Model("{\"Claims\":[{\"FactIds\":[\"invented\"],\"English\":\"Invented\",\"Russian\":\"\"}],\"Examples\":[],\"DiagramSvg\":\"\"}", Answer);
        var draft = await new DocumentationGenerator(fake).GenerateAsync(DocumentationValidationTests.Input, TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Prompts.Count);
        Assert.Contains("unknown_fact", fake.Prompts[1]);
        Assert.All(draft.Claims, c => Assert.Equal("Перевод.", c.Russian));
        Assert.Empty(DocumentationValidator.Validate(DocumentationValidationTests.Input, draft));
    }

    [Fact]
    public async Task Two_failed_primary_attempts_use_the_named_fallback_and_then_stop()
    {
        var fake = new Model("not json", "{}", "not json");
        await Assert.ThrowsAsync<DocumentationValidationException>(() =>
            new DocumentationGenerator(fake).GenerateAsync(DocumentationValidationTests.Input, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "gpt-oss:20b", "gpt-oss:20b", "qwen3.5:9b" }, fake.Models);
    }

    [Fact]
    public async Task Translation_failure_never_publishes_half_a_pair()
    {
        var fake = new Model(Answer, Answer, Answer) { Translation = "" };
        await Assert.ThrowsAsync<DocumentationValidationException>(() =>
            new DocumentationGenerator(fake).GenerateAsync(DocumentationValidationTests.Input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_stops_before_the_first_model_call()
    {
        var fake = new Model(Answer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DocumentationGenerator(fake).GenerateAsync(DocumentationValidationTests.Input, new CancellationToken(true)));
        Assert.Empty(fake.Models);
    }

    [Fact]
    public async Task Prompt_binds_facts_as_data_and_does_not_ask_the_model_to_invent_history()
    {
        var fake = new Model(Answer);
        await new DocumentationGenerator(fake).GenerateAsync(DocumentationValidationTests.Input, TestContext.Current.CancellationToken);
        Assert.Contains("untrusted-content", Assert.Single(fake.Prompts));
        Assert.Contains("f0", fake.Prompts[0]);
        Assert.Contains("Do not infer intent", fake.Prompts[0]);
        Assert.Contains("\"GraphSvg\":", fake.Prompts[0]);
    }

    private static string Answer => JsonSerializer.Serialize(DocumentationValidationTests.Draft);
    private sealed class Model(params string[] answers) : IDocumentationModel
    {
        public List<string> Prompts { get; } = [];
        public List<string> Models { get; } = [];
        public string Translation { get; init; } = "Перевод.";
        public Task<string> CompleteAsync(string model, string prompt, CancellationToken cancellationToken)
        {
            Models.Add(model); Prompts.Add(prompt);
            return Task.FromResult(answers[Models.Count - 1]);
        }
        public Task<string> TranslateAsync(string english, CancellationToken cancellationToken) => Task.FromResult(Translation);
    }
}
