using System.Text;
using LocalAi.Documentation;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationRendererTests
{
    [Fact]
    public void Both_languages_have_all_three_formats_and_a_readable_diagram()
    {
        var pack = DocumentationRenderer.Render(DocumentationValidationTests.Input, DocumentationValidationTests.Draft);
        Assert.Equal(9, pack.Count);
        Assert.Contains("SIL OPEN FONT LICENSE", Encoding.UTF8.GetString(pack["LICENSES.txt"]));
        foreach (var language in new[] { "en", "ru" })
        {
            var markdown = Encoding.UTF8.GetString(pack[$"page.{language}.md"]);
            Assert.Contains(language == "en" ? "page.ru.md" : "page.en.md", markdown);
            Assert.Contains("type:A", markdown);
            Assert.Contains("src/A.cs", markdown);
            Assert.Contains("diagram.svg", markdown);
            Assert.Contains("public class A : B", markdown);
            using var stream = new MemoryStream(pack[$"page.{language}.pdf"]);
            using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            Assert.True(pdf.PageCount > 0);
        }
        using var bitmap = SKBitmap.Decode(pack["diagram.png"]);
        Assert.NotNull(bitmap);
        Assert.InRange(bitmap.Width, 1, 4096);
        Assert.InRange(bitmap.Height, 1, 4096);
        Assert.Contains("Example.A", Encoding.UTF8.GetString(pack["diagram.svg"]));
    }

    [Fact]
    public void Untrusted_prose_is_text_in_html_and_history_is_preserved_as_evidence()
    {
        var input = DocumentationValidationTests.Input with { History = [new("abc123", "Fix <unsafe> retention")] };
        var draft = DocumentationValidationTests.Draft with { Claims = [new(["f0"], "<script>alert(1)</script>", "Описание.")] };
        var html = Encoding.UTF8.GetString(DocumentationRenderer.Render(input, draft)["page.en.html"]);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("abc123", html);
        Assert.Contains("Fix &lt;unsafe&gt; retention", html);
    }

    [Fact]
    public void Invalid_output_is_refused_before_rendering() => Assert.Throws<DocumentationValidationException>(() =>
        DocumentationRenderer.Render(DocumentationValidationTests.Input, DocumentationValidationTests.Draft with { Claims = [] }));

    [Fact]
    public void Rendering_the_same_graph_is_stable_and_uses_paths_for_glyphs()
    {
        var first = DocumentationRenderer.Render(DocumentationValidationTests.Input, DocumentationValidationTests.Draft);
        var second = DocumentationRenderer.Render(DocumentationValidationTests.Input, DocumentationValidationTests.Draft);
        Assert.Equal(first["diagram.svg"], second["diagram.svg"]);
        Assert.Equal(first["diagram.png"], second["diagram.png"]);
        Assert.DoesNotContain("<text", Encoding.UTF8.GetString(first["diagram.svg"]));
    }

    [Fact]
    public void Code_cannot_close_its_fence_and_insert_an_external_image()
    {
        var example = "var value = \"\"\"\n````\n![tracking](https://example.invalid/pixel)\n\"\"\";";
        var draft = DocumentationValidationTests.Draft with { Examples = [example] };
        var html = Encoding.UTF8.GetString(DocumentationRenderer.Render(DocumentationValidationTests.Input, draft)["page.en.html"]);
        Assert.DoesNotContain("src=\"https://example.invalid", html);
    }

    [Fact]
    public void Missing_font_glyph_is_a_failure_instead_of_a_changed_label()
    {
        var input = DocumentationValidationTests.Input with { Nodes = [new("n0", "Example.\U00010000"), new("n1", "Example.B")] };
        var draft = DocumentationValidationTests.Draft with { DiagramSvg = DocumentationValidationTests.Svg.Replace("Example.A", "Example.\U00010000", StringComparison.Ordinal) };
        Assert.Throws<DocumentationValidationException>(() => DocumentationRenderer.Render(input, draft));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Long_symbol_ids_stay_inside_the_pdf_page_margins(string language)
    {
        var input = DocumentationValidationTests.Input with
        {
            Facts = [.. DocumentationValidationTests.Input.Facts, new("long", "dotnet M:" + new string('W', 180), "Method: " + new string('W', 180))],
        };
        var bytes = DocumentationRenderer.Render(input, DocumentationValidationTests.Draft)[$"page.{language}.pdf"];
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            const int dpi = 72;
            // Renderer margins are 15 mm; allow three pixels for glyph antialiasing/rounding.
            var margin = (int)(15 / 25.4 * dpi) - 3;
            var pages = PDFtoImage.Conversion.GetPageCount(bytes);
            for (var page = 0; page < pages; page++)
            {
                using var bitmap = PDFtoImage.Conversion.ToImage(bytes, page, options: new PDFtoImage.RenderOptions(Dpi: dpi));
                var pixels = bitmap.Pixels;
                for (var y = 0; y < bitmap.Height; y++)
                for (var x = bitmap.Width - margin; x < bitmap.Width; x++)
                {
                    var pixel = pixels[y * bitmap.Width + x];
                    Assert.True(pixel.Red >= 180 && pixel.Green >= 180 && pixel.Blue >= 180, $"Text outside right margin: page {page}, x={x}, y={y}");
                }
            }
        }
        else Assert.Skip("PDFium is unavailable on this platform.");
    }
}
