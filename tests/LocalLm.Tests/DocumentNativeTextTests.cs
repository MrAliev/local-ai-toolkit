using LocalAi.Contracts;
using LocalLm.Core;
using SkiaSharp;

namespace LocalLm.Tests;

public sealed partial class PdfReadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_text_bytes_and_binary_content_are_not_silently_replaced(bool binary)
    {
        File.WriteAllBytes(Path.Combine(root, "bad.txt"), binary ? [65, 0, 66] : [0xff, 0x61]);
        try
        {
            var error = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.RefreshAsync(
                new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken));
            Assert.Equal(binary ? "binary_text" : "invalid_text_encoding", error.Code);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unicode_bom_text_preserves_the_original_line_endings(bool bigEndian)
    {
        File.WriteAllText(Path.Combine(root, "unicode.txt"), "Привет\r\n日本語 😀", new System.Text.UnicodeEncoding(bigEndian, true, true));
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var hit = Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "日本語", 10, TestContext.Current.CancellationToken)).Hits);
            Assert.Equal("Привет\r\n日本語 😀", hit.Text);
            Assert.Equal(2, hit.MatchStartLine);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task A_scan_in_text_layer_mode_reports_empty_pages_and_never_falls_back_to_ocr()
    {
        var path = Pdf(2);
        var client = new RecordingClient();
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken, "TextLayer");
            Assert.Equal([1, 2], summary.EmptyPages.Select(page => page.Page));
            Assert.All(summary.EmptyPages, page => { Assert.Equal(path, page.Path); Assert.Equal("no_text_extracted", page.Reason); });
            Assert.Empty(client.Calls);
            Assert.Empty((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "anything", 10, TestContext.Current.CancellationToken)).Hits);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Old_ocr_snapshot_is_searchable_and_refresh_migrates_it_atomically(int version)
    {
        _ = Pdf(1);
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var path = Path.Combine(DocumentIndexPath, "snapshot.json");
            var old = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            old["schemaVersion"] = version;
            old.AsObject().Remove("pdfMode");
            old["documents"]![0]!["pages"]![0]!.AsObject().Remove("textKind");
            File.WriteAllText(path, old.ToJsonString());
            Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "reply", 10, TestContext.Current.CancellationToken)).Hits);
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(3, summary.IndexSchemaVersion);
            Assert.Equal(3, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"]!.GetValue<int>());
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".markdown")]
    public async Task Plain_document_search_preserves_text_and_line_coordinates_without_ocr(string extension)
    {
        var path = Path.Combine(root, "notes" + extension);
        var text = "First\r\nПривет 😀\r\nInvoice ALPHA-7319\nLast";
        File.WriteAllText(path, text);
        var client = new RecordingClient();
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, summary.Documents);
            Assert.Equal(0, summary.Pages);
            var result = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "ALPHA-7319", 10, TestContext.Current.CancellationToken);
            var hit = Assert.Single(result.Hits);
            Assert.Equal("plain-text", hit.TextKind);
            Assert.Equal(text, hit.Text);
            Assert.Equal(1, hit.StartLine);
            Assert.Equal(4, hit.EndLine);
            Assert.Null(hit.Model);
            Assert.Empty(client.Calls);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Explicit_pdf_text_layer_mode_extracts_text_without_model_work()
    {
        _ = TextPdf();
        var client = new RecordingClient();
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken, "TextLayer");
            Assert.Empty(client.Calls);
            var hit = Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "ALPHA-7319", 10, TestContext.Current.CancellationToken)).Hits);
            Assert.Equal("pdf-text", hit.TextKind);
            Assert.Equal(1, hit.Page);
            Assert.Null(hit.Model);
            Assert.Null(hit.StartLine);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    private string TextPdf()
    {
        var path = Path.Combine(root, "text-layer.pdf");
        using var output = File.Create(path);
        using var document = SKDocument.CreatePdf(output);
        var canvas = document.BeginPage(400, 240);
        canvas.Clear(SKColors.White);
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 18);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawText("Invoice ALPHA-7319", 24, 40, SKTextAlign.Left, font, paint);
        document.EndPage();
        document.Close();
        return path;
    }
}
