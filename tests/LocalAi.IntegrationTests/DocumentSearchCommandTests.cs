using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class DocumentSearchCommandTests
{
    [Fact]
    public async Task Real_cli_returns_docx_structural_evidence_and_token_notice_without_a_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-docx-cli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var index = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(source, "letter.docx"), System.IO.Compression.ZipArchiveMode.Create))
        {
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
                ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
                ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Invoice ALPHA-7319</w:t></w:r></w:p></w:body></w:document>",
            };
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open());
                writer.Write(part.Value);
            }
        }
        try
        {
            var created = await Run(["documents", "index", "--source", source, "--index", index, "--json"]);
            Assert.Equal(0, created.Exit);
            Assert.Contains("No model call", created.Error);
            var searched = await Run(["documents", "search", "--index", index, "--query", "ALPHA-7319", "--json"]);
            Assert.Equal(0, searched.Exit);
            using var found = JsonDocument.Parse(searched.Output);
            var hit = Assert.Single(found.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());
            Assert.Equal("docx-text", hit.GetProperty("textKind").GetString());
            Assert.Equal(1, hit.GetProperty("docxSource").GetProperty("paragraph").GetInt32());
            Assert.Equal(JsonValueKind.Null, hit.GetProperty("page").ValueKind);
            Assert.DoesNotContain("INPUT", searched.Error);
            Assert.DoesNotContain("OUTPUT", searched.Error);
            Assert.Contains("tokens of request/response input context", searched.Error);
            Assert.Contains("necessary source context is not established", searched.Error);
            Assert.DoesNotContain("additional cloud tokens", searched.Error);
            Assert.Contains("No model call", searched.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_literal_json_query_is_preserved_as_an_operand(bool envelope)
    {
        string[] args = ["documents", "search", "--index", "folder", "--query", "--json", .. envelope ? new[] { "--json" } : []];
        Assert.Equal(envelope, MachineOutput.Requested(args));
        Assert.Equal(["documents", "search", "--index", "folder", "--query", "--json"], MachineOutput.Without(args));
    }
    [Theory]
    [InlineData("search", "--index", "missing", "--query", "word", "--limit", "0")]
    [InlineData("search", "--index", "missing", "--query", "word", "--limit", "101")]
    [InlineData("search", "--index", "missing", "--query", "word", "--model", "any")]
    [InlineData("search", "--index", "missing", "--index", "duplicate", "--query", "word")]
    [InlineData("index", "--index", "missing", "--source", "source", "--query", "word")]
    [InlineData("index", "--index", "missing")]
    public async Task Invalid_document_arguments_are_refused_before_model_or_filesystem_work(params string[] args)
    {
        var output = new StringWriter();
        Assert.Equal(2, await DocumentSearchCommand.ExecuteAsync(args, new(output, new StringWriter()), true, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Real_cli_reads_plain_text_with_pdf_text_layer_option_without_a_model()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-native-cli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var index = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "notes.md"), "# Heading\nInvoice ALPHA-7319\n");
        try
        {
            var created = await Run(["documents", "index", "--source", source, "--index", index, "--pdf-mode", "TextLayer", "--json"]);
            Assert.Equal(0, created.Exit);
            Assert.Contains("No model call", created.Error);
            var searched = await Run(["documents", "search", "--index", index, "--query", "ALPHA-7319", "--json"]);
            Assert.Equal(0, searched.Exit);
            using var found = JsonDocument.Parse(searched.Output);
            var hit = Assert.Single(found.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());
            Assert.Equal("plain-text", hit.GetProperty("textKind").GetString());
            Assert.Equal(2, hit.GetProperty("matchStartLine").GetInt32());
            Assert.Equal(JsonValueKind.Null, hit.GetProperty("page").ValueKind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Already_cancelled_command_performs_no_work()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentSearchCommand.ExecuteAsync(
            ["index", "--source", "source", "--index", "index"], new(new StringWriter(), new StringWriter()), true, cancellation.Token));
    }

    [Fact]
    public async Task Real_cli_round_trips_an_empty_index_and_reports_token_applicability()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-documents-cli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var index = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        try
        {
            var created = await Run(["documents", "index", "--source", source, "--index", index, "--json"]);
            Assert.Equal(0, created.Exit);
            using var response = JsonDocument.Parse(created.Output);
            Assert.Equal(0, response.RootElement.GetProperty("data").GetProperty("documents").GetInt32());
            Assert.Contains("Token savings do not apply to this operation.", created.Error);
            var searched = await Run(["documents", "search", "--index", index, "--query", "hello", "--json"]);
            Assert.Equal(0, searched.Exit);
            using var found = JsonDocument.Parse(searched.Output);
            Assert.Empty(found.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());
            Assert.Contains("necessary source context is not established", searched.Error);
            Assert.Contains("No model call", searched.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(int Exit, string Output, string Error)> Run(string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LOCALAI_LANGUAGE"] = "en";
        start.ArgumentList.Add(Assembly.Load("localai").Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(true); }
        return (process.ExitCode, await output, await error);
    }
}
