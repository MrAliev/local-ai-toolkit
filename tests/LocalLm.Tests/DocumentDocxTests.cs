using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed partial class PdfReadingTests
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string PackageNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    [Fact]
    public async Task Docx_with_only_unsupported_blocks_has_bounded_notices_and_no_ocr_claim()
    {
        WriteDocx(string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent><w:p><w:r><w:t>omitted</w:t></w:r></w:p></w:sdtContent></w:sdt>", 100)));
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(64, summary.ExtractionNotices.Length);
            Assert.Equal(36, summary.AdditionalNotices);
            Assert.Equal(0, summary.Paragraphs);
            var found = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "omitted", 10, TestContext.Current.CancellationToken);
            Assert.Empty(found.Hits);
            Assert.Equal("literal-phrase", found.SearchKind);
            Assert.Equal(64, found.ExtractionNotices.Length);
            Assert.Equal(36, found.AdditionalNotices);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Docx_source_coordinates_are_covered_by_snapshot_integrity_validation()
    {
        WriteDocx("<w:p><w:r><w:t>needle</w:t></w:r></w:p>");
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var path = Path.Combine(DocumentIndexPath, "snapshot.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["documents"]![0]!["pages"]![0]!["docxSource"]!["paragraph"] = 2;
            File.WriteAllText(path, json.ToJsonString());
            var error = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.SearchAsync(DocumentIndexPath, "needle", 10, TestContext.Current.CancellationToken));
            Assert.Equal("index_corrupt", error.Code);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Theory]
    [InlineData("word/document.xml")]
    [InlineData("custom/main.xml")]
    public async Task Docx_search_returns_exact_paragraph_and_cell_references_without_inference(string part)
    {
        var body = """
            <w:p><w:r><w:t>First paragraph</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Привет 😀 invoice </w:t></w:r><w:r><w:t>ALPHA</w:t><w:tab/><w:t>7319</w:t><w:br/><w:t>Last</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Heading</w:t></w:r></w:p><w:p><w:r><w:t>invoice ALPHA</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """;
        var path = WriteDocx(body, part: part);
        var client = new RecordingClient();
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, summary.Documents);
            Assert.Equal(0, summary.Pages);
            var result = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "invoice ALPHA", 10, TestContext.Current.CancellationToken);
            Assert.Equal(2, result.Hits.Length);
            Assert.Equal("Привет 😀 invoice ALPHA\t7319\nLast", result.Hits[0].Text);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            using var package = ZipFile.OpenRead(path);
            using var stream = package.GetEntry(part)!.Open();
            var xml = XDocument.Load(stream);
            var namespaces = new XmlNamespaceManager(new NameTable());
            namespaces.AddNamespace("w", WordNamespace);
            for (var i = 0; i < result.Hits.Length; i++)
            {
                var hit = result.Hits[i];
                Assert.Equal(hash, hit.Sha256);
                Assert.Equal("docx-text", hit.TextKind);
                Assert.Equal("layout-unverified", hit.TextVerification);
                Assert.Null(hit.Page);
                Assert.Null(hit.Region);
                Assert.Null(hit.Model);
                Assert.Null(hit.OcrOffset);
                Assert.Null(hit.StartLine);
                var source = JsonSerializer.SerializeToElement(hit, DocumentSearchIndex.JsonOptions).GetProperty("docxSource");
                Assert.Equal(part, source.GetProperty("part").GetString());
                Assert.Equal(WordNamespace, source.GetProperty("namespaceUri").GetString());
                Assert.Equal(2, source.GetProperty("paragraph").GetInt32());
                var target = Assert.IsType<XElement>(xml.XPathSelectElement(source.GetProperty("xpath").GetString()!, namespaces));
                Assert.Equal("p", target.Name.LocalName);
                Assert.Equal(i == 0 ? JsonValueKind.Null : JsonValueKind.Number, source.GetProperty("table").ValueKind);
                if (i == 1)
                {
                    Assert.Equal(1, source.GetProperty("table").GetInt32());
                    Assert.Equal(1, source.GetProperty("row").GetInt32());
                    Assert.Equal(1, source.GetProperty("cell").GetInt32());
                    Assert.Equal("invoice ALPHA", string.Concat(target.Descendants(XName.Get("t", WordNamespace)).Select(node => node.Value)));
                }
                Assert.Equal("invoice ALPHA", hit.Text.Substring(hit.MatchOffset - hit.TextOffset, hit.MatchLength));
            }
            Assert.Empty(client.Calls);
            var again = await DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, again.ReusedDocuments);
            Assert.Equal(result.Hits.Select(hit => hit.Id), (await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "invoice ALPHA", 10, TestContext.Current.CancellationToken)).Hits.Select(hit => hit.Id));
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Docx_omissions_are_explicit_and_never_join_text_across_unsupported_content()
    {
        var body = """
            <w:p><w:r><w:t>safe</w:t></w:r><w:hyperlink><w:r><w:t xml:space="preserve"> link</w:t></w:r></w:hyperlink></w:p>
            <w:p><w:r><w:t>before</w:t><w:drawing/><w:t>after</w:t></w:r></w:p>
            <w:p><w:r><w:fldChar w:fldCharType="begin"/><w:t>field result</w:t></w:r></w:p>
            <w:ins><w:p><w:r><w:t>revision</w:t></w:r></w:p></w:ins>
            <w:tbl><w:tr><w:tc><w:p/><w:tbl><w:tr><w:tc><w:p><w:r><w:t>nested</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:tc></w:tr></w:tbl>
            """;
        WriteDocx(body, extra: [("word/header1.xml", "<header>omitted header</header>"),
            ("word/_rels/document.xml.rels", $"<Relationships xmlns=\"{PackageNamespace}\"><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"http://127.0.0.1:1/never-fetch\" TargetMode=\"External\"/></Relationships>")]);
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, summary.Documents);
            var notices = JsonSerializer.SerializeToElement(summary, DocumentSearchIndex.JsonOptions).GetProperty("extractionNotices").EnumerateArray().ToArray();
            Assert.Contains(notices, notice => notice.GetProperty("reason").GetString() == "unindexed_part" && notice.GetProperty("part").GetString() == "word/header1.xml");
            Assert.Contains(notices, notice => notice.GetProperty("reason").GetString() == "external_relationship_not_loaded");
            Assert.Contains(notices, notice => notice.GetProperty("reason").GetString() == "unsupported_paragraph");
            foreach (var query in new[] { "beforeafter", "before", "field result", "revision", "nested", "omitted header" })
                Assert.Empty((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, query, 10, TestContext.Current.CancellationToken)).Hits);
            var found = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "safe link", 10, TestContext.Current.CancellationToken);
            Assert.Single(found.Hits);
            Assert.Equal(notices.Length, JsonSerializer.SerializeToElement(found, DocumentSearchIndex.JsonOptions).GetProperty("extractionNotices").GetArrayLength());
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Theory]
    [InlineData("not-zip", "docx_unreadable")]
    [InlineData("duplicate", "docx_unsupported")]
    [InlineData("dtd", "docx_unreadable")]
    [InlineData("external-main", "docx_unsupported")]
    [InlineData("traversal", "docx_unsupported")]
    [InlineData("large-part", "docx_limit")]
    [InlineData("expanded-total", "docx_limit")]
    [InlineData("many-paragraphs", "docx_limit")]
    [InlineData("many-parts", "docx_limit")]
    [InlineData("deep-xml", "docx_limit")]
    public async Task Unsafe_docx_refresh_is_refused_and_preserves_the_previous_snapshot(string mutation, string code)
    {
        File.WriteAllText(Path.Combine(root, "safe.txt"), "retained source");
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var before = File.ReadAllBytes(Path.Combine(DocumentIndexPath, "snapshot.json"));
            var path = WriteDocx("<w:p><w:r><w:t>new</w:t></w:r></w:p>",
                mainTarget: mutation == "external-main" ? "https://127.0.0.1:1/document.xml" : "word/document.xml",
                externalMain: mutation == "external-main");
            if (mutation == "not-zip") File.WriteAllText(path, "not a ZIP archive");
            else if (mutation != "external-main")
            {
                using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
                if (mutation is "duplicate" or "traversal") AddDocxPart(zip, mutation == "duplicate" ? "word/document.xml" : "../outside.txt", "bad");
                if (mutation == "large-part") AddDocxPart(zip, "word/large.xml", new string('x', 8 * 1024 * 1024 + 1));
                if (mutation == "expanded-total") for (var i = 0; i < 5; i++) AddDocxPart(zip, "part" + i, new string('x', 7 * 1024 * 1024));
                if (mutation == "many-parts") for (var i = 0; i < 257; i++) AddDocxPart(zip, "part" + i, "x");
                if (mutation == "many-paragraphs")
                {
                    zip.GetEntry("word/document.xml")!.Delete();
                    AddDocxPart(zip, "word/document.xml", $"<w:document xmlns:w=\"{WordNamespace}\"><w:body>" + string.Concat(Enumerable.Repeat("<w:p/>", 4097)) + "</w:body></w:document>");
                }
                if (mutation is "dtd" or "deep-xml")
                {
                    zip.GetEntry("word/document.xml")!.Delete();
                    AddDocxPart(zip, "word/document.xml", mutation == "dtd"
                        ? "<!DOCTYPE document [<!ENTITY x SYSTEM 'file:///never-read'>]><document>&x;</document>"
                        : string.Concat(Enumerable.Repeat("<x>", 80)) + string.Concat(Enumerable.Repeat("</x>", 80)));
                }
            }
            var error = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken));
            Assert.Equal(code, error.Code);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(DocumentIndexPath, "snapshot.json")));
            Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "retained", 10, TestContext.Current.CancellationToken)).Hits);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Docx_changes_deletion_and_result_limits_follow_source_freshness()
    {
        var path = WriteDocx("<w:p><w:r><w:t>needle one</w:t></w:r></w:p><w:p><w:r><w:t>needle two</w:t></w:r></w:p>");
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var limited = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "needle", 1, TestContext.Current.CancellationToken);
            Assert.Single(limited.Hits);
            Assert.True(limited.Truncated);
            WriteDocx("<w:p><w:r><w:t>replacement</w:t></w:r></w:p>");
            var stale = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "needle", 10, TestContext.Current.CancellationToken);
            Assert.Empty(stale.Hits);
            Assert.Equal("changed", Assert.Single(stale.ExcludedSources).Reason);
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "replacement", 10, TestContext.Current.CancellationToken)).Hits);
            File.Delete(path);
            Assert.Equal("deleted", Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "replacement", 10, TestContext.Current.CancellationToken)).ExcludedSources).Reason);
            Assert.Equal(1, (await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken)).RemovedDocuments);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    private string WriteDocx(string body, string part = "word/document.xml", (string Name, string Text)[]? extra = null,
        string? mainTarget = null, bool externalMain = false)
    {
        var path = Path.Combine(root, "document.docx");
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        AddDocxPart(zip, "[Content_Types].xml", $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/{part}\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        AddDocxPart(zip, "_rels/.rels", $"<Relationships xmlns=\"{PackageNamespace}\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{mainTarget ?? part}\"{(externalMain ? " TargetMode=\"External\"" : "")}/></Relationships>");
        AddDocxPart(zip, part, $"<w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}<w:sectPr/></w:body></w:document>");
        foreach (var item in extra ?? []) AddDocxPart(zip, item.Name, item.Text);
        return path;
    }

    private static void AddDocxPart(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }
}
