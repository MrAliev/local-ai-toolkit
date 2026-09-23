using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed partial class LocalTasksTests
{
    [Fact]
    public async Task Checkpoint_loading_revalidates_copied_prose_even_with_a_matching_text_hash()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-content-load-" + Guid.NewGuid().ToString("N"));
        const string source = "Measured results stay unchanged.";
        try
        {
            await new LocalTasks(new FakeLocalModelClient { Answer = "Результаты измерений не меняются." }).TranslateAsync(
                source, "English", "Russian", true, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            var path = Path.Combine(checkpoint, "segment-0.json");
            var segment = System.Text.Json.JsonSerializer.Deserialize<TranslationCheckpoint.Segment>(File.ReadAllText(path), LocalAiJson.Strict)!;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(segment with
                { Text = source, TextHash = TranslationCheckpoint.Hash(source) }, LocalAiJson.Strict));
            var client = new FakeLocalModelClient();
            await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).TranslateAsync(source,
                "English", "Russian", true, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint));
            Assert.Empty(client.Calls);
        }
        finally { if (Directory.Exists(checkpoint)) Directory.Delete(checkpoint, true); }
    }

    [Fact]
    public async Task A_rejected_translation_does_not_credit_experimental_cloud_token_savings()
    {
        const string source = "The measured output is unchanged and must be translated.";
        using var catalog = new TranslationCatalog(true, ["translategemma:12b"]);
        var client = new FakeLocalModelClient { Answer = source };
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(source,
            "English", "Russian", true, TranslationDelivery.ToFile, TestContext.Current.CancellationToken));
        var metrics = Assert.Single(client.ExperimentCompletions).Metrics;
        Assert.True(metrics.InputTokens > 0);
        Assert.True(metrics.OutputTokens > 0);
        Assert.Equal(0, metrics.EstimatedNetCloudContextTokensSaved);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "\n\nПеревод выполнен локальной моделью.")]
    [InlineData(true, "\n\nПеревод выполнен локальной моделью.")]
    public async Task Different_target_language_rejects_copied_prose_even_with_an_added_footer(bool markdown, string footer)
    {
        const string source = "Cold start remains unchanged. Read the measured results.";
        using var catalog = new TranslationCatalog(markdown, ["translategemma:12b"]);
        var client = new FakeLocalModelClient { Answer = source + footer };
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(source,
            "English", "Russian", markdown, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken));
        Assert.Single(client.Calls);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, Assert.Single(client.ExperimentCompletions).Outcome);
    }

    [Fact]
    public async Task A_copied_fragment_is_not_hidden_by_another_translated_fragment()
    {
        const string source = "First paragraph.\n\n```cs\nvar x = 42;\n```\n\nSecond paragraph.";
        using var catalog = new TranslationCatalog(true, ["translategemma:12b"]);
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("Первый абзац.");
        client.Answers.Enqueue("Second paragraph.");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(source,
            "English", "Russian", true, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken));
        Assert.Equal(2, client.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_corruption_uses_the_catalog_fallback_and_counts_rejected_work(bool markdown)
    {
        const string source = "Cold start: 5199.121 ms; repeat: 5552.463 ms.";
        const string invalid = "Холодный запуск: 342.123 ms; повтор: 567.890 ms.";
        const string valid = "Холодный запуск: 5199.121 ms; повтор: 5552.463 ms.";
        using var catalog = new TranslationCatalog(markdown, ["qwen3.5:9b"]);
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(invalid);
        client.Answers.Enqueue(valid);
        var result = await catalog.Tasks(client).TranslateAsync(source, "English", "Russian", markdown,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);
        Assert.StartsWith(valid, result.Answer);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(client.Calls.Sum(call => TokenEstimator.ForText(call.Prompt)) + TokenEstimator.ForText(invalid) + TokenEstimator.ForText(valid), result.LocalTokensProcessed);
    }

    [Fact]
    public async Task A_copied_fragment_never_poisons_the_translation_checkpoint()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-content-guard-" + Guid.NewGuid().ToString("N"));
        const string source = "Measured results stay unchanged.";
        using var catalog = new TranslationCatalog(true, ["translategemma:12b"]);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(new FakeLocalModelClient { Answer = source }).TranslateAsync(
                source, "English", "Russian", true, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint));
            Assert.Empty(Directory.GetFiles(checkpoint, "segment-*.json"));
            var corrected = new FakeLocalModelClient { Answer = "Результаты измерений не меняются." };
            var result = await catalog.Tasks(corrected).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.Single(corrected.Calls);
            Assert.True(result.Validation.Passed);
        }
        finally { if (Directory.Exists(checkpoint)) Directory.Delete(checkpoint, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_requested_language_does_not_reject_unchanged_text(bool markdown)
    {
        const string source = "Measured results stay unchanged.";
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = source }).TranslateAsync(source,
            "English", " english ", markdown, TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);
        Assert.True(result.Validation.Passed);
    }
}
