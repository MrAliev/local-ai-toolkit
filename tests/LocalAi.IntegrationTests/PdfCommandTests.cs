using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;
using LocalLm.Core;
using System.Text.Json;

namespace LocalAi.IntegrationTests;

public sealed class PdfCommandTests
{
    [Fact]
    public void Pdf_ocr_accepts_a_checkpoint_directory()
    {
        Assert.True(ReadImageCommand.TryParse(["Read", "manual.pdf", "--profile", "Ocr", "--checkpoint", "progress"], out var request, out _));
        Assert.Equal("progress", request!.PdfCheckpointDirectory);
    }

    [Theory]
    [InlineData("image.png", "Ocr")]
    [InlineData("manual.pdf", "VisualAnalysis")]
    public void Checkpoints_require_pdf_ocr(string path, string profile)
    {
        Assert.False(ReadImageCommand.TryParse(["Read", path, "--profile", profile, "--checkpoint", "progress"], out _, out var refusal));
        Assert.Equal("pdf_checkpoint_requires_ocr", refusal!.Code);
    }

    [Fact]
    public void Pdf_checkpoint_requires_a_value()
    {
        Assert.False(ReadImageCommand.TryParse(["Read", "manual.pdf", "--profile", "Ocr", "--checkpoint"], out _, out var refusal));
        Assert.Equal("checkpoint_value_missing", refusal!.Code);
    }

    [Fact]
    public async Task Failed_pdf_output_has_a_delivery_error_and_cannot_create_exit_code()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var usage = new UsageCall(UsageSurface.Cli, "localai", "read-image");
        var exit = await LocalModelRun.ExecuteAsync("read-image", "fixture.pdf", LocalTaskProfile.Ocr,
            (_, _) => Task.FromException<LocalResult>(new PdfOutputWriteException("output.txt", new IOException("locked"))),
            true, TestContext.Current.CancellationToken, new CommandStreams(output, error));
        Assert.Equal(73, exit);
        Assert.Contains("output_not_written", output.ToString(), StringComparison.Ordinal);
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(UsageDelivery.Failed, usage.Complete().Outcome.Delivery);
    }

    [Fact]
    public void Pdf_ocr_accepts_a_complete_output_file()
    {
        Assert.True(ReadImageCommand.TryParse(["Read", "manual.pdf", "--profile", "Ocr", "--out", "result.txt"], out var request, out _));
        Assert.Equal("result.txt", request!.PdfOutputPath);
    }

    [Theory]
    [InlineData("image.png", "Ocr")]
    [InlineData("manual.pdf", "VisualAnalysis")]
    public void Output_file_requires_pdf_ocr(string path, string profile)
    {
        Assert.False(ReadImageCommand.TryParse(["Read", path, "--profile", profile, "--out", "result.txt"], out _, out var refusal));
        Assert.Equal("pdf_output_requires_ocr", refusal!.Code);
    }

    [Fact]
    public void Pdf_output_requires_a_value()
    {
        Assert.False(ReadImageCommand.TryParse(["Read", "manual.pdf", "--profile", "Ocr", "--out"], out _, out var refusal));
        Assert.Equal("out_value_missing", refusal!.Code);
    }

    [Fact]
    public void Pdf_page_selection_is_accepted_without_renumbering_the_request()
    {
        Assert.True(ReadImageCommand.TryParse(["Read", "manual.pdf", "--pages", "4,2-3"], out var request, out _));
        Assert.Equal("4,2-3", request!.PdfPages);
    }

    [Theory]
    [InlineData("image.png", "1", "pdf_pages_requires_pdf")]
    [InlineData("manual.pdf", "0", "pdf_pages_invalid")]
    [InlineData("manual.pdf", "3-1", "pdf_pages_invalid")]
    public void Invalid_page_options_are_refused(string path, string pages, string code)
    {
        Assert.False(ReadImageCommand.TryParse(["Read", path, "--pages", pages], out _, out var refusal));
        Assert.Equal(code, refusal!.Code);
    }

    [Fact]
    public void Pdf_page_option_requires_a_value()
    {
        Assert.False(ReadImageCommand.TryParse(["Read", "manual.pdf", "--pages"], out _, out var refusal));
        Assert.Equal("pages_value_missing", refusal!.Code);
    }

    [Fact]
    public void Sparse_page_progress_estimates_remaining_selected_pages()
    {
        using var culture = TestCulture.Reading("en");
        using var output = new StringWriter();
        var now = DateTimeOffset.UnixEpoch;
        var progress = new LocalRunProgress(output, () => now);
        progress.Report(new ReadingPdfPage(2, 100) { SelectedIndex = 1, SelectedTotal = 2 });
        now = now.AddMinutes(1);
        progress.Report(new ReadingPdfPage(90, 100) { SelectedIndex = 2, SelectedTotal = 2 });
        Assert.Equal("Reading PDF page 2 of 100." + Environment.NewLine +
            "Reading PDF page 90 of 100. About 1.0 min left." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Read_image_accepts_a_pdf_with_an_image_task_profile()
    {
        var accepted = ReadImageCommand.TryParse(["Read the tables", "manual.PDF", "--profile", "Ocr"], out var request, out var refusal);

        Assert.True(accepted);
        Assert.Null(refusal);
        Assert.Equal(["manual.PDF"], request!.Images);
        Assert.Equal(LocalTaskProfile.Ocr, request.Profile);
    }

    [Theory]
    [InlineData("together", "pdf_together_unsupported")]
    [InlineData("mixed", "pdf_single_input_required")]
    [InlineData("question", "prompt_missing")]
    public void Unsupported_pdf_requests_have_specific_refusals(string scenario, string code)
    {
        string[] arguments = scenario switch
        {
            "together" => ["Read", "manual.pdf", "--together"],
            "mixed" => ["Read", "manual.pdf", "shot.png"],
            _ => ["manual.pdf"],
        };

        Assert.False(ReadImageCommand.TryParse(arguments, out _, out var refusal));
        Assert.Equal(code, refusal!.Code);
    }

    [Theory]
    [InlineData("en", "Reading PDF page 1 of 100.", "Reading PDF page 2 of 100. About 99.0 min left.")]
    [InlineData("ru", "Читается страница PDF 1 из 100.", "Читается страница PDF 2 из 100. Осталось примерно 99.0 мин.")]
    public void Each_pdf_page_prints_its_number_in_the_readers_language(string language, string first, string second)
    {
        using var culture = TestCulture.Reading(language);
        using var output = new StringWriter();
        var now = DateTimeOffset.UnixEpoch;
        var progress = new LocalRunProgress(output, () => now);

        progress.Report(new ReadingPdfPage(1, 100));
        now = now.AddMinutes(1);
        progress.Report(new ReadingPdfPage(2, 100));

        Assert.Equal(first + Environment.NewLine + second + Environment.NewLine, output.ToString());
    }
}
