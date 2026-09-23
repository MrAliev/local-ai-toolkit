using System.Security.Cryptography;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.IO.Pipelines;
using System.Text.Json;

namespace LocalLm.Tests;

public sealed partial class PdfReadingTests
{
    [Theory]
    [InlineData("Ocr", "reply", "unverified", 1)]
    [InlineData("TextLayer", "ALPHA-7319", "layout-unverified", 0)]
    public async Task Document_tools_bind_through_the_real_mcp_sdk_and_wrap_source_text(string mode, string query, string verification, int calls)
    {
        _ = mode == "Ocr" ? Pdf(1) : TextPdf();
        var incoming = new Pipe();
        var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        var model = new RecordingClient();
        builder.Services.AddSingleton(new LocalTasks(model));
        builder.Services.AddMcpServer().WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithToolsFromAssembly(typeof(LocalLmTools).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var indexed = await client.CallToolAsync("index_documents", new Dictionary<string, object?>
            { ["sourceDirectory"] = root, ["indexDirectory"] = DocumentIndexPath, ["pdfMode"] = mode }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEqual(true, indexed.IsError);
            var result = await client.CallToolAsync("search_documents", new Dictionary<string, object?>
            { ["indexDirectory"] = DocumentIndexPath, ["query"] = query, ["limit"] = 1 }, cancellationToken: TestContext.Current.CancellationToken);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text));
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("<untrusted-content", text);
            Assert.Contains(query, text);
            Assert.Contains("\"textVerification\":\"" + verification + "\"", text);
            Assert.Equal(calls, model.Calls.Count);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true);
        }
    }

    private string DocumentIndexPath => Path.Combine(root, "..", Path.GetFileName(root) + "-index");

    [Theory]
    [InlineData("schema", "index_schema")]
    [InlineData("text", "index_corrupt")]
    [InlineData("page", "index_corrupt")]
    [InlineData("path", "index_corrupt")]
    public async Task Document_search_refuses_corrupted_or_unsupported_snapshots(string change, string code)
    {
        _ = Pdf(1);
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var path = Path.Combine(DocumentIndexPath, "snapshot.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            if (change == "schema") json["schemaVersion"] = 999;
            if (change == "text") json["documents"]![0]!["pages"]![0]!["text"] = "forged text";
            if (change == "page") json["documents"]![0]!["pages"]![0]!["number"] = 100;
            if (change == "path") json["documents"]![0]!["name"] = "../outside.pdf";
            File.WriteAllText(path, json.ToJsonString());
            var error = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.SearchAsync(DocumentIndexPath, "reply", 10, TestContext.Current.CancellationToken));
            Assert.Equal(code, error.Code);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Document_refresh_refuses_another_writer_and_an_index_inside_the_source()
    {
        var client = new RecordingClient();
        try
        {
            var wrongPath = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, Path.Combine(root, "index"), null, TestContext.Current.CancellationToken));
            Assert.Equal("invalid_path", wrongPath.Code);
            Directory.CreateDirectory(DocumentIndexPath);
            using var lease = new FileStream(Path.Combine(DocumentIndexPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var busy = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken));
            Assert.Equal("index_busy", busy.Code);
            Assert.Empty(client.Calls);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Document_search_limits_page_hits_without_splitting_unicode_and_does_not_repeat_model_usage()
    {
        _ = Pdf(3);
        var text = new string('x', 1999) + "😀 needle " + new string('x', 1000);
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient { Answer = (_, _) => text }), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            using var usage = new UsageCall(UsageSurface.Mcp, "locallm", "search_documents");
            var result = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "needle", 1, TestContext.Current.CancellationToken);
            Assert.True(result.Truncated);
            var hit = Assert.Single(result.Hits);
            Assert.Equal(text.Substring(hit.TextOffset, hit.Text.Length), hit.Text);
            Assert.Equal("needle", text.Substring(hit.OcrOffset!.Value, hit.MatchLength));
            Assert.Equal(UsageModelState.NotCalled, usage.Complete().Models.State);
            Assert.NotNull(usage.Comparison.Scenario("search"));
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Document_index_checks_page_limits_before_any_ocr()
    {
        _ = Pdf(129);
        var client = new RecordingClient();
        try
        {
            var error = await Assert.ThrowsAsync<DocumentIndexException>(() => DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, TestContext.Current.CancellationToken));
            Assert.Equal("page_limit", error.Code);
            Assert.Empty(client.Calls);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Document_search_returns_ocr_evidence_and_refresh_reuses_unchanged_sources()
    {
        var path = Pdf(2);
        var model = new RecordingClient { Answer = (page, _) => page == 2 ? "Счёт 123 😀" : "Other text" };
        try
        {
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(model), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(2, summary.Pages);
            var found = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "СЧЁТ", 10, TestContext.Current.CancellationToken);
            var hit = Assert.Single(found.Hits);
            Assert.Equal(path, hit.Path);
            Assert.Equal(2, hit.Page);
            Assert.Equal("ocr", hit.TextKind);
            Assert.Equal("Счёт 123 😀", hit.Text);
            Assert.Equal(new DocumentRegion("page", "normalized", 0, 0, 1, 1), hit.Region);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), hit.Sha256);
            Assert.Equal("qwen3.5:9b", hit.Model);
            Assert.Equal(0, hit.OcrOffset);
            Assert.Equal(4, hit.MatchLength);
            var again = await DocumentSearchIndex.RefreshAsync(new LocalTasks(model), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, again.ReusedDocuments);
            Assert.Equal(2, model.Calls.Count);
            Assert.Equal(hit.Id, Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "счёт", 10, TestContext.Current.CancellationToken)).Hits).Id);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Changed_and_deleted_sources_never_return_stale_hits_and_refresh_reconciles_them()
    {
        var changed = Pdf(1);
        var deleted = Pdf(1);
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            File.AppendAllText(changed, "\n% new version\n");
            File.Delete(deleted);
            var stale = await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "reply", 10, TestContext.Current.CancellationToken);
            Assert.Empty(stale.Hits);
            Assert.Equal(["changed", "deleted"], stale.ExcludedSources.Select(source => source.Reason).Order());
            var next = new RecordingClient { Answer = (_, _) => "New reading" };
            var summary = await DocumentSearchIndex.RefreshAsync(new LocalTasks(next), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, summary.RemovedDocuments);
            Assert.Single(next.Calls);
            Assert.Single((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "New", 10, TestContext.Current.CancellationToken)).Hits);
            Assert.Empty((await DocumentSearchIndex.SearchAsync(DocumentIndexPath, "reply", 10, TestContext.Current.CancellationToken)).Hits);
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Cancelled_refresh_keeps_the_previous_atomic_snapshot()
    {
        _ = Pdf(1);
        try
        {
            await DocumentSearchIndex.RefreshAsync(new LocalTasks(new RecordingClient()), root, DocumentIndexPath, null, TestContext.Current.CancellationToken);
            var before = File.ReadAllBytes(Path.Combine(DocumentIndexPath, "snapshot.json"));
            _ = Pdf(1);
            using var cancelled = new CancellationTokenSource();
            var client = new RecordingClient { AfterImage = cancelled.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentSearchIndex.RefreshAsync(new LocalTasks(client), root, DocumentIndexPath, null, cancelled.Token));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(DocumentIndexPath, "snapshot.json")));
        }
        finally { if (Directory.Exists(DocumentIndexPath)) Directory.Delete(DocumentIndexPath, true); }
    }

    [Fact]
    public async Task Pdf_ocr_exposes_page_observations_without_parsing_localized_headings()
    {
        var path = Pdf(3);
        var text = "PDF page 99 of 100\n\nПривет 😀";
        var result = await new LocalTasks(new RecordingClient { Answer = (_, _) => text })
            .ReadImageAsync([path], "Read", LocalTaskProfile.Ocr, null, false,
                TestContext.Current.CancellationToken, pdfPages: "3,1");
        var evidence = Assert.IsType<PdfOcrEvidence>(result.PdfEvidence);
        Assert.Equal(Path.GetFullPath(path), evidence.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), evidence.Sha256);
        Assert.Equal(3, evidence.TotalPages);
        Assert.Equal([1, 3], evidence.Pages.Select(page => page.Number));
        Assert.All(evidence.Pages, page => { Assert.Equal(text, page.Text); Assert.Equal("qwen3.5:9b", page.Model); });
    }
}
