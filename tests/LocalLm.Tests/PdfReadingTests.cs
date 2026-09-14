using LocalAi.Contracts;
using LocalLm.Core;
using SkiaSharp;

namespace LocalLm.Tests;

public sealed class PdfReadingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-pdf-tests-" + Guid.NewGuid().ToString("N"));

    public PdfReadingTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(100)]
    public async Task Every_page_is_read_once_without_the_image_batch_limit(int count)
    {
        var path = Pdf(count);
        var client = new RecordingClient();
        var observer = new Observer();

        var result = await new LocalTasks(client, observer).ReadImageAsync([path], "Read the page", null, TestContext.Current.CancellationToken);

        Assert.Equal(count, client.Calls.Count(call => call.Images.Count != 0));
        Assert.All(client.Calls.Where(call => call.Images.Count != 0), call => Assert.Single(call.Images));
        Assert.Equal(Enumerable.Range(1, count).Select(index => new ReadingPdfPage(index, count)), observer.Steps.OfType<ReadingPdfPage>());
        Assert.Equal($"reply {client.Calls.Count}", result.Answer);
        Assert.Contains(Path.GetFileName(path), result.Notice, StringComparison.Ordinal);
        if (count == 1) Assert.Single(client.Calls);
        else Assert.Empty(client.Calls[^1].Images);
    }

    [Fact]
    public async Task Rasterization_preserves_page_order_color_and_aspect_ratio()
    {
        var path = Pdf(2, width: 72, height: 144);
        var client = new RecordingClient();

        await new LocalTasks(client).ReadImageAsync([path], "Read the page", null, TestContext.Current.CancellationToken);

        var pages = client.Calls.Where(call => call.Images.Count != 0).ToArray();
        Assert.Equal(2, pages.Length);
        for (var index = 0; index < pages.Length; index++)
        {
            using var bitmap = SKBitmap.Decode(Convert.FromBase64String(Assert.Single(pages[index].Images)));
            Assert.Equal(150, bitmap.Width);
            Assert.Equal(300, bitmap.Height);
            Assert.Equal(index == 0 ? SKColors.Red : SKColors.Blue, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
            Assert.Equal((long)bitmap.Width * bitmap.Height, pages[index].Pixels);
        }
    }

    [Fact]
    public async Task Merge_input_keeps_page_numbers_even_when_a_model_omits_them()
    {
        var client = new RecordingClient();

        await new LocalTasks(client).ReadImageAsync([Pdf(2)], "Read the page", null, TestContext.Current.CancellationToken);

        var merge = Assert.Single(client.Calls, call => call.Images.Count == 0);
        Assert.Contains("PDF page 1", merge.Prompt, StringComparison.Ordinal);
        Assert.Contains("PDF page 2", merge.Prompt, StringComparison.Ordinal);
        Assert.Contains("reply 1", merge.Prompt, StringComparison.Ordinal);
        Assert.Contains("reply 2", merge.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_page_rasters_are_bounded_before_rendering()
    {
        var client = new RecordingClient();

        await new LocalTasks(client).ReadImageAsync([Pdf(1, 14400, 14400)], "Read the page", null, TestContext.Current.CancellationToken);

        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(Assert.Single(Assert.Single(client.Calls).Images)));
        Assert.InRange((long)bitmap.Width * bitmap.Height, 1, 4_000_000);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    [Fact]
    public async Task Hierarchical_reductions_count_every_model_call_in_the_receipt()
    {
        var client = new RecordingClient { LongAnswers = true };

        var result = await new LocalTasks(client).ReadImageAsync([Pdf(12)], "Read the page", null, TestContext.Current.CancellationToken);

        Assert.True(client.Calls.Count(call => call.Images.Count == 0) > 1);
        Assert.Equal(TimeSpan.FromSeconds(client.Calls.Count), result.Receipt.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(2 * client.Calls.Count), result.Receipt.ExecutionDuration);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_page_and_releases_the_pdf()
    {
        var path = Pdf(3);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new RecordingClient { AfterImage = cancelled.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTasks(client).ReadImageAsync([path], "Read the page", null, cancelled.Token));

        Assert.Single(client.Calls);
        using var released = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Already_cancelled_read_opens_no_pdf_and_calls_no_model()
    {
        var client = new RecordingClient();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTasks(client).ReadImageAsync([Pdf(1)], "Read the page", null, cancelled.Token));

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_pdf_cannot_be_combined_with_other_paths()
    {
        var client = new RecordingClient();
        var path = Pdf(1);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync(
            [path, path], "Read the page", null, TestContext.Current.CancellationToken));

        Assert.Contains("one PDF", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_pdf_refuses_together_mode_before_a_model_call()
    {
        var client = new RecordingClient();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync(
            [Pdf(1)], "Read the page", LocalTaskProfile.VisualAnalysis, null, true, TestContext.Current.CancellationToken));

        Assert.Contains("together", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_malformed_pdf_reports_its_path_without_a_model_call()
    {
        var path = Path.Combine(root, "broken.pdf");
        File.WriteAllText(path, "%PDF-1.7\nnot a document");
        var client = new RecordingClient();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync(
            [path], "Read the page", null, TestContext.Current.CancellationToken));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
        using var released = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task An_oversized_pdf_is_refused_before_parsing_or_model_work()
    {
        var path = Path.Combine(root, "large.pdf");
        using (var file = File.Create(path)) file.SetLength(256L * 1024 * 1024 + 1);
        var client = new RecordingClient();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync(
            [path], "Read the page", null, TestContext.Current.CancellationToken));

        Assert.Contains("256 MiB", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    private string Pdf(int count, float width = 72, float height = 72)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pdf");
        using var output = File.Create(path);
        using var document = SKDocument.CreatePdf(output);
        for (var index = 0; index < count; index++)
        {
            document.BeginPage(width, height).Clear(index % 2 == 0 ? SKColors.Red : SKColors.Blue);
            document.EndPage();
        }
        document.Close();
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class Observer : ILocalRunObserver
    {
        public List<LocalRunStep> Steps { get; } = [];
        public void Report(LocalRunStep step) => Steps.Add(step);
    }

    private sealed record Call(string Prompt, IReadOnlyList<string> Images, long Pixels);

    private sealed class RecordingClient : ILocalModelClient
    {
        public List<Call> Calls { get; } = [];
        public Action? AfterImage { get; init; }
        public bool LongAnswers { get; init; }

        public Task<LocalJobResult<string>> RoutedChatAsync(LocalTaskProfile profile, string prompt, string? system,
            IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload, LocalWorkflowHint? workflow,
            string? modelOverride, int? requestedContextTokens, LocalJobPriority priority, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(prompt, imagesBase64?.ToArray() ?? [], workload.TotalImagePixels));
            if (imagesBase64 is { Count: > 0 }) AfterImage?.Invoke();
            return Task.FromResult(new LocalJobResult<string>($"reply {Calls.Count}" + (LongAnswers ? new string('x', 4000) : ""),
                new LocalUsageReceipt(Guid.NewGuid(), "local-lm", "chat", "qwen3.5:9b", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
                    prompt.Length, prompt.Length / 4, null, null, null,
                    new LocalRoutingReceipt(profile, "qwen3.5:9b", requestedContextTokens, WasCold: false, UsedFallback: false,
                        ValidatorResult: "none:pass", EstimatedGrossCloudTokensSaved: 0, EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: 0, IsExperimentalAttempt: false))));
        }

        public Task<LocalJobResult<string>> ChatAsync(string model, string prompt, string? system, IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(string model, int contextTokens, string catalogVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(string model, string catalogVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(LocalTaskProfile profile, string model,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(LocalTaskProfile profile, string model,
            ExperimentOwnerAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
