using LocalAi.Contracts;
using System.Globalization;
using LocalLm.Core;
using SkiaSharp;
using LocalLm.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.IO.Pipelines;
using System.Text.Json;
using System.Runtime.InteropServices;
using LocalAi.Mcp;

namespace LocalLm.Tests;

public sealed class PdfReadingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-pdf-tests-" + Guid.NewGuid().ToString("N"));

    public PdfReadingTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Selected_pdf_ocr_is_written_in_full_without_returning_the_transcript()
    {
        var text = string.Concat(Enumerable.Repeat("Текст 日本語 😀\n", 4000));
        var client = new RecordingClient { Answer = (_, _) => text };
        var target = Path.Combine(root, "полный результат.txt");
        var result = await new LocalTasks(client).ReadImageAsync([Pdf(4)], "Transcribe",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken,
            pdfPages: "4,2", pdfOutputPath: target);

        Assert.True(File.Exists(target), "The complete OCR output file must be published.");
        Assert.Equal($"PDF page 2 of 4\n\n{text}\n\nPDF page 4 of 4\n\n{text}",
            await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Contains(target, result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Текст 日本語", result.Answer, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_binds_page_selection_and_output_path(bool toFile)
    {
        var model = new RecordingClient();
        var incoming = new Pipe();
        var outgoing = new Pipe();
        var outputPath = Path.Combine(root, "mcp-transcript.txt");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new LocalTasks(model));
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithToolsFromAssembly(typeof(LocalLmTools).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "read_image",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["paths"] = JsonSerializer.SerializeToElement(new[] { Pdf(4) }),
                    ["question"] = JsonSerializer.SerializeToElement("Transcribe"),
                    ["mode"] = JsonSerializer.SerializeToElement("Ocr"),
                    ["pages"] = JsonSerializer.SerializeToElement("4,2,2"),
                    ["outPath"] = JsonSerializer.SerializeToElement(toFile ? outputPath : null),
                },
            }, TestContext.Current.CancellationToken);
            Assert.NotEqual(true, result.IsError);
            Assert.Equal(2, model.Calls.Count);
            var answer = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            const string expected = "PDF page 2 of 4\n\nreply 1\n\nPDF page 4 of 4\n\nreply 2";
            if (toFile)
            {
                Assert.Equal(expected, await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
                Assert.Contains(outputPath, answer, StringComparison.Ordinal);
                Assert.DoesNotContain("reply 1", answer, StringComparison.Ordinal);
            }
            else Assert.Contains(expected, answer, StringComparison.Ordinal);
            Assert.Contains("<untrusted-content", answer, StringComparison.Ordinal);
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Selected_pages_render_only_the_requested_rasters_and_keep_original_numbers()
    {
        var client = new RecordingClient();
        var result = await new LocalTasks(client).ReadImageAsync([Pdf(4)], "Transcribe",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken, pdfPages: "4,2,2");

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("PDF page 2 of 4\n\nreply 1\n\nPDF page 4 of 4\n\nreply 2", result.Answer);
        foreach (var call in client.Calls)
        {
            using var bitmap = SKBitmap.Decode(Convert.FromBase64String(Assert.Single(call.Images)));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("4-2")]
    [InlineData("2-99")]
    [InlineData("3,,4")]
    [InlineData("2147483648")]
    public async Task Invalid_page_selection_calls_no_model_and_releases_the_pdf(string selection)
    {
        var path = Pdf(4);
        var client = new RecordingClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync([path], "Read",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken, selection));
        Assert.Empty(client.Calls);
        using var released = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(LocalTaskProfile.VisualAnalysis)]
    [InlineData(LocalTaskProfile.ImageTranslation)]
    public async Task A_single_selected_page_keeps_its_original_number_in_the_prompt(LocalTaskProfile profile)
    {
        var client = new RecordingClient();
        await new LocalTasks(client).ReadImageAsync([Pdf(4)], "Read", profile, null, false,
            TestContext.Current.CancellationToken, "2");
        var call = Assert.Single(client.Calls);
        Assert.Contains("PDF page 2 of 4.", call.Prompt, StringComparison.Ordinal);
        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(Assert.Single(call.Images)));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    [Fact]
    public async Task Selected_ranges_use_original_numbers_and_selected_counts_in_progress()
    {
        var observer = new Observer();
        var client = new RecordingClient();
        await new LocalTasks(client, observer).ReadImageAsync([Pdf(5)], "Read", LocalTaskProfile.Ocr,
            null, false, TestContext.Current.CancellationToken, "5,2-3");
        Assert.Equal(new[]
        {
            new ReadingPdfPage(2, 5) { SelectedIndex = 1, SelectedTotal = 3 },
            new ReadingPdfPage(3, 5) { SelectedIndex = 2, SelectedTotal = 3 },
            new ReadingPdfPage(5, 5) { SelectedIndex = 3, SelectedTotal = 3 },
        }, observer.Steps.OfType<ReadingPdfPage>());
    }

    [Fact]
    public async Task Page_selection_for_an_image_is_refused_before_loading_it()
    {
        var client = new RecordingClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync(["missing.png"], "Read",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken, "1"));
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("directory")]
    [InlineData("missing-directory")]
    [InlineData("empty")]
    public async Task An_invalid_output_is_refused_before_inference(string scenario)
    {
        var path = Pdf(1);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var target = scenario switch
        {
            "same" => path,
            "directory" => root,
            "empty" => " ",
            _ => Path.Combine(root, "missing", "result.txt"),
        };
        var client = new RecordingClient();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new LocalTasks(client).ReadImageAsync([path], "Read",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken, pdfOutputPath: target));
        Assert.Empty(client.Calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pdf_source_hard_link_is_refused_before_and_after_inference(bool createdDuringInference)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows file identity regression.");
        var path = Pdf(1);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var target = Path.Combine(root, "source-alias.txt");
        void Link() => Assert.True(CreateHardLink(target, path, IntPtr.Zero));
        if (!createdDuringInference) Link();
        var client = new RecordingClient { AfterImage = createdDuringInference ? Link : null };
        var error = await Record.ExceptionAsync(() => new LocalTasks(client).ReadImageAsync([path], "Read",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken, pdfOutputPath: target));
        Assert.NotNull(error);
        Assert.Contains("must not overwrite the source PDF", error.Message, StringComparison.Ordinal);
        Assert.Equal(createdDuringInference ? 1 : 0, client.Calls.Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task Replacing_an_output_keeps_other_hard_links_unchanged()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows hard-link regression.");
        var other = Path.Combine(root, "previous.txt");
        var target = Path.Combine(root, "result.txt");
        await File.WriteAllTextAsync(other, "previous", TestContext.Current.CancellationToken);
        Assert.True(CreateHardLink(target, other, IntPtr.Zero));
        await new LocalTasks(new RecordingClient()).ReadImageAsync([Pdf(1)], "Read", LocalTaskProfile.Ocr,
            null, false, TestContext.Current.CancellationToken, pdfOutputPath: target);
        Assert.Equal("previous", await File.ReadAllTextAsync(other, TestContext.Current.CancellationToken));
        Assert.Equal("PDF page 1 of 1\n\nreply 1", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_keeps_the_previous_complete_output()
    {
        var target = Path.Combine(root, "result.txt");
        await File.WriteAllTextAsync(target, "previous", TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new RecordingClient { AfterImage = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(client).ReadImageAsync([Pdf(3)], "Read",
            LocalTaskProfile.Ocr, null, false, cancellation.Token, pdfOutputPath: target));
        Assert.Single(client.Calls);
        Assert.Equal("previous", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_ocr_comparison_requires_complete_delivery(bool failDelivery)
    {
        var target = Path.Combine(root, "output.txt");
        var client = new RecordingClient { AfterImage = failDelivery ? () => Directory.CreateDirectory(target) : null };
        var response = await new UsageReporting(root).InvokeAsync("locallm", "read_image", Guid.NewGuid(), async token =>
            new CallToolResult { Content = [new TextContentBlock { Text = await LocalLmTools.ReadImage(new LocalTasks(client),
                [Pdf(1)], "Read", "Ocr", cancellationToken: token, outPath: target) }] },
            TestContext.Current.CancellationToken, requestJson: "{\"outPath\":\"output.txt\"}");
        var report = UsageReportJson.Read(response.Meta!["localai/usage"]!.ToJsonString());
        Assert.DoesNotContain("reply 1", string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
        if (failDelivery)
        {
            Assert.Equal(UsageDelivery.Failed, report.Outcome.Delivery);
            Assert.Equal(UsageValue.Unknown("result_not_delivered"), report.CloudTokens[UsageCloudTokens.InputSaved]);
            Assert.Equal(UsageValue.Unknown("result_not_delivered"), report.CloudTokens[UsageCloudTokens.OutputSaved]);
        }
        else
        {
            Assert.Equal(UsageDelivery.Delivered, report.Outcome.Delivery);
            Assert.Equal(O200kCloudTokenProfile.Instance.CountText(await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)),
                report.CloudTokens[UsageCloudTokens.OutputWithoutDelegation]);
        }
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public async Task Ocr_preserves_both_pages_when_a_text_only_merge_would_invent_a_third()
    {
        var client = new RecordingClient
        {
            Answer = (number, hasImage) => hasImage
                ? number == 1 ? "LocalAi RC18 PDF test\nMarker ALPHA-7319\nRepeated line\nRepeated line"
                    : "Second page\nMarker BETA-4826"
                : "PDF page 3:\nThird page\nMarker CUBE-4826"
        };

        var result = await new LocalTasks(client).ReadImageAsync([Pdf(2)], "Transcribe the text",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken);

        Assert.Equal("PDF page 1 of 2\n\nLocalAi RC18 PDF test\nMarker ALPHA-7319\nRepeated line\nRepeated line\n\n" +
            "PDF page 2 of 2\n\nSecond page\nMarker BETA-4826", result.Answer);
        Assert.Equal(2, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.Single(call.Images));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Ocr_keeps_all_page_readings_even_beyond_the_merge_budget(int count)
    {
        var client = new RecordingClient { LongAnswers = true };
        var observer = new Observer();

        var result = await new LocalTasks(client, observer).ReadImageAsync([Pdf(count)], "Transcribe the text",
            LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken);

        Assert.Equal(count, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.Single(call.Images));
        for (var page = 1; page <= count; page++)
            Assert.Contains($"PDF page {page} of {count}\n\nreply {page}" + new string('x', 4000), result.Answer, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(count), result.Receipt.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(2 * count), result.Receipt.ExecutionDuration);
        Assert.Equal(Enumerable.Range(1, count).Select(page => new ReadingPdfPage(page, count)), observer.Steps.OfType<ReadingPdfPage>());
        Assert.Empty(observer.Steps.OfType<MergingPartials>());
    }

    [Theory]
    [InlineData("en", "PDF page 1 of 1")]
    [InlineData("ru", "Страница PDF 1 из 1")]
    public async Task Ocr_localizes_page_headings_without_translating_the_reading(string language, string heading)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var client = new RecordingClient { Answer = (_, _) => "Исходный текст / original text" };
            var result = await new LocalTasks(client).ReadImageAsync([Pdf(1)], "Transcribe the text",
                LocalTaskProfile.Ocr, null, false, TestContext.Current.CancellationToken);
            Assert.Equal(heading + "\n\nИсходный текст / original text", result.Answer);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

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

    [Theory]
    [InlineData(LocalTaskProfile.VisualAnalysis)]
    [InlineData(LocalTaskProfile.Ocr)]
    public async Task Cancellation_stops_before_the_next_page_and_releases_the_pdf(LocalTaskProfile profile)
    {
        var path = Pdf(3);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new RecordingClient { AfterImage = cancelled.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTasks(client).ReadImageAsync([path], "Read the page", profile, null, false, cancelled.Token));

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
        public Func<int, bool, string>? Answer { get; init; }

        public Task<LocalJobResult<string>> RoutedChatAsync(LocalTaskProfile profile, string prompt, string? system,
            IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload, LocalWorkflowHint? workflow,
            string? modelOverride, int? requestedContextTokens, LocalJobPriority priority, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(prompt, imagesBase64?.ToArray() ?? [], workload.TotalImagePixels));
            if (imagesBase64 is { Count: > 0 }) AfterImage?.Invoke();
            return Task.FromResult(new LocalJobResult<string>(Answer?.Invoke(Calls.Count, imagesBase64 is { Count: > 0 }) ??
                $"reply {Calls.Count}" + (LongAnswers ? new string('x', 4000) : ""),
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
