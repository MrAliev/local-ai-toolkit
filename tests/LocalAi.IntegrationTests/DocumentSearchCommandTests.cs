using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class DocumentSearchCommandTests
{
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
            Assert.Contains("administrative_operation", created.Error);
            var searched = await Run(["documents", "search", "--index", index, "--query", "hello", "--json"]);
            Assert.Equal(0, searched.Exit);
            using var found = JsonDocument.Parse(searched.Output);
            Assert.Empty(found.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());
            Assert.Contains("cloud token savings", searched.Error);
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
