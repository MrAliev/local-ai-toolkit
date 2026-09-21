using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Markdig;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using SkiaSharp;

namespace LocalAi.Documentation;

public static class DocumentationRenderer
{
    public const string CacheVersion = "documentation-renderer-2";
    private static readonly Lock PdfGate = new();
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().DisableHtml().Build();
    private sealed record Block(string Text, int Heading = 0, bool Code = false, bool Diagram = false);

    public static IReadOnlyDictionary<string, byte[]> Render(DocumentationInput input, DocumentationDraft draft)
    {
        var errors = DocumentationValidator.Validate(input, draft);
        if (errors.Count != 0) throw new DocumentationValidationException(errors);
        return RenderValidated(input, draft);
    }

    // Both callers validate immediately before this method; the store also validates cached text.
    internal static IReadOnlyDictionary<string, byte[]> RenderValidated(DocumentationInput input, DocumentationDraft draft,
        DocumentationHtmlMode htmlMode = DocumentationHtmlMode.Standalone)
    {
        if (!Enum.IsDefined(htmlMode)) throw new ArgumentOutOfRangeException(nameof(htmlMode));
        var (svg, png) = DocumentationDiagram.Render(input);
        var pack = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["diagram.svg"] = svg, ["diagram.png"] = png,
            ["LICENSES.txt"] = DocumentationFonts.Read("NOTICES.txt") };
        foreach (var language in new[] { "en", "ru" })
        {
            var blocks = Content(input, draft, language);
            var opposite = language == "en" ? "ru" : "en";
            var markdown = $"[{(language == "en" ? "Русская версия" : "English version")}](page.{opposite}.md)\n\n" +
                string.Join("\n\n", blocks.Select(b => b.Diagram ? "![" + Plain(input.Name) + "](diagram.svg)" :
                    b.Code ? FencedCode(b.Text) : new string('#', b.Heading) + (b.Heading > 0 ? " " : "") + Plain(b.Text))) +
                "\n\n[LICENSES.txt](LICENSES.txt)\n";
            pack[$"page.{language}.md"] = Encoding.UTF8.GetBytes(markdown);
            var htmlBody = Markdig.Markdown.ToHtml(markdown.Replace($"page.{opposite}.md", $"page.{opposite}.html", StringComparison.Ordinal), Markdown);
            if (htmlMode == DocumentationHtmlMode.Standalone)
            {
                htmlBody = htmlBody.Replace("src=\"diagram.svg\"", "src=\"data:image/png;base64," + Convert.ToBase64String(png) + "\"", StringComparison.Ordinal);
                htmlBody = htmlBody.Replace("<p><a href=\"LICENSES.txt\">LICENSES.txt</a></p>",
                    "<details><summary>LICENSES.txt</summary><pre>" + WebUtility.HtmlEncode(Encoding.UTF8.GetString(pack["LICENSES.txt"])) + "</pre></details>", StringComparison.Ordinal);
            }
            var html = "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>" + WebUtility.HtmlEncode(input.Name) + "</title>" +
                (htmlMode == DocumentationHtmlMode.Shared ? "<link rel=\"stylesheet\" href=\"../_assets/" + DocumentationHtmlResources.Name + "\">" : "") +
                "<style>" + (htmlMode == DocumentationHtmlMode.Standalone ? DocumentationHtmlResources.Css : "") +
                "body{font:16px/1.6 Doc,sans-serif;color:#13233a;background:#fff;max-width:960px;margin:48px auto;padding:0 24px;overflow-wrap:anywhere}" +
                "h1,h2{line-height:1.2;margin-top:1.8em}pre{white-space:pre-wrap;background:#eef3fa;padding:18px}code{font-family:DocMono,monospace}img{max-width:100%;height:auto}a{color:#164b94}" +
                "</style></head><body>" + htmlBody + "</body></html>";
            pack[$"page.{language}.html"] = Encoding.UTF8.GetBytes(html);
            pack[$"page.{language}.pdf"] = Pdf(input, draft, blocks, png, language);
        }
        return pack;
    }

    private static List<Block> Content(DocumentationInput input, DocumentationDraft draft, string language)
    {
        string Text(string key) => DocumentationText.Get(key, language);
        var blocks = new List<Block>
        {
            new(input.Name, 1), new(Text("Source") + ": " + input.SourcePath + ":" + input.SourceLine + " [" + input.SymbolId + "]"),
            new(Text("Snapshot") + ": " + input.Snapshot), new(Text("ReviewNotice")), new(Text("Overview"), 2),
        };
        foreach (var claim in draft.Claims)
            blocks.Add(new((language == "en" ? claim.English : claim.Russian) + " [" + string.Join(", ", claim.FactIds) + "]"));
        blocks.Add(new(Text("Facts"), 2));
        foreach (var fact in input.Facts) blocks.Add(new($"[{fact.Id}] [{fact.SymbolId}] {fact.Text}"));
        blocks.Add(new(Text("Relationships"), 2));
        blocks.Add(new("", Diagram: true));
        foreach (var edge in input.Edges)
            blocks.Add(new(input.Nodes.Single(n => n.Id == edge.From).Label + " → " + input.Nodes.Single(n => n.Id == edge.To).Label + ": " + edge.Kind));
        blocks.Add(new(Text("Why"), 2));
        // A commit message is evidence the reader can interpret, not an invented explanation.
        if (input.History.Count == 0) blocks.Add(new(Text("HistoryLimit")));
        if (input.History.Count > 0)
        {
            blocks.Add(new(Text("CommitMessages"), 3));
            foreach (var history in input.History) blocks.Add(new(history.Commit + "\n" + history.Message));
        }
        if (draft.Examples.Length > 0)
        {
            blocks.Add(new(Text("Examples"), 2));
            foreach (var example in draft.Examples) blocks.Add(new(example, Code: true));
        }
        return blocks;
    }

    private static string Plain(string text)
    {
        var encoded = WebUtility.HtmlEncode(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var character in new[] { "\\", "`", "*", "_", "[", "]", "#", "!", "|" })
            encoded = encoded.Replace(character, "\\" + character, StringComparison.Ordinal);
        return encoded;
    }

    private static string FencedCode(string code)
    {
        var longest = 0; var current = 0;
        foreach (var character in code)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + "csharp\n" + code + "\n" + fence;
    }

    private static byte[] Pdf(DocumentationInput input, DocumentationDraft draft, List<Block> blocks, byte[] png, string language)
    {
        lock (PdfGate)
        {
            GlobalFontSettings.FontResolver ??= new DocumentationFonts();
            if (GlobalFontSettings.FontResolver is not DocumentationFonts) throw new InvalidOperationException("documentation_font_resolver_conflict");
            var document = new Document();
            document.Info.Title = input.Name;
            document.Styles[StyleNames.Normal]!.Font.Name = "Noto Sans";
            document.Styles[StyleNames.Normal]!.Font.Size = 10;
            document.Styles[StyleNames.Normal]!.ParagraphFormat.SpaceAfter = Unit.FromPoint(8);
            var section = document.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
            using var fontData = SKData.CreateCopy(DocumentationFonts.Read("NotoSans-Regular.ttf"));
            using var typeface = SKTypeface.FromData(fontData);
            using var normalFont = new SKFont(typeface, 10);
            using var headingFont = new SKFont(typeface, 14);
            using var titleFont = new SKFont(typeface, 21);
            foreach (var block in blocks)
            {
                if (block.Diagram)
                {
                    using var bitmap = SKBitmap.Decode(png);
                    for (var top = 0; top < bitmap.Height; top += 1000)
                    {
                        using var slice = new SKBitmap();
                        if (!bitmap.ExtractSubset(slice, new SKRectI(0, top, bitmap.Width, Math.Min(top + 1000, bitmap.Height))))
                            throw new InvalidOperationException("diagram_slice_failed");
                        using var image = SKImage.FromBitmap(slice);
                        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                        var added = section.AddImage("base64:" + Convert.ToBase64String(encoded.ToArray()));
                        added.Width = Unit.FromCentimeter(18);
                        added.LockAspectRatio = true;
                    }
                    continue;
                }
                var paragraph = section.AddParagraph(block.Code ? WrapCode(block.Text) :
                    WrapLongWords(block.Text, block.Heading == 1 ? titleFont : block.Heading > 1 ? headingFont : normalFont));
                if (block.Code) { paragraph.Format.Font.Name = "Noto Sans Mono"; paragraph.Format.Font.Size = 8; }
                if (block.Heading > 0)
                {
                    paragraph.Format.Font.Size = block.Heading == 1 ? 21 : 14;
                    paragraph.Format.SpaceBefore = Unit.FromPoint(12);
                    paragraph.Format.KeepWithNext = true;
                }
            }
            var renderer = new PdfDocumentRenderer { Document = document };
            renderer.RenderDocument();
            var pdf = renderer.PdfDocument;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { input, draft, language })));
            var id = Encoding.Latin1.GetString(digest, 0, 16);
            pdf.Internals.FirstDocumentID = id; pdf.Internals.SecondDocumentID = id;
            pdf.Info.CreationDate = pdf.Info.ModificationDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var output = new MemoryStream();
            pdf.Save(output, closeStream: false);
            pdf.Dispose();
            return output.ToArray();
        }
    }

    private static string WrapCode(string code) => string.Join("\n", code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
        .SelectMany(line => line.Length == 0 ? [""] : Enumerable.Range(0, (line.Length + 89) / 90).Select(i => line.Substring(i * 90, Math.Min(90, line.Length - i * 90)))));

    private static string WrapLongWords(string text, SKFont font)
    {
        // A4 minus the two 15 mm margins is 510 pt. Leave 10 pt for differences in
        // glyph metrics between the line measurer and the PDF renderer. MigraDoc wraps
        // prose, but a documentation symbol ID may otherwise be a single 200-character word.
        const float width = 500;
        var result = new StringBuilder(); var word = new StringBuilder();
        void Flush()
        {
            var value = word.ToString(); word.Clear();
            if (font.MeasureText(value) <= width) { result.Append(value); return; }
            var line = "";
            foreach (var rune in value.EnumerateRunes())
            {
                var next = line + rune;
                if (font.MeasureText(next) > width && line.Length > 0) { result.Append(line).Append('\n'); line = rune.ToString(); }
                else line = next;
            }
            result.Append(line);
        }
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { Flush(); result.Append(rune); }
            else word.Append(rune);
        }
        Flush();
        return result.ToString();
    }
}
