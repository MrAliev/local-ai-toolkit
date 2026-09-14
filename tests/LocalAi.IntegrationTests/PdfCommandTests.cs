using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class PdfCommandTests
{
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
