using LocalAi.Broker;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class UsageReportLookupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "usage-lookup-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task Lookup_returns_the_saved_cancelled_call_as_one_json_document()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local"); call.Cancelled();
        var expected = call.Complete();
        await new UsageReportStore(root).WriteAsync(expected, TestContext.Current.CancellationToken);
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TelemetryCommand.ExecuteAsync(["--call-id", call.CallId.ToString()], root,
            new(output, error), TestContext.Current.CancellationToken);
        Assert.Equal(0, exit); Assert.Empty(error.ToString());
        Assert.Equal(UsageReportJson.Write(expected), UsageReportJson.Write(UsageReportJson.Read(output.ToString())));
    }
    [Theory]
    [InlineData("bad-id", 2)]
    [InlineData("00000000-0000-0000-0000-000000000000", 2)]
    [InlineData("bb0cfe6a-4a04-4341-9e5e-4f3e00c3ec99", 1)]
    public async Task Invalid_or_missing_identity_has_no_partial_stdout(string id, int expected)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await TelemetryCommand.ExecuteAsync(["--call-id", id], root, new(output, error), TestContext.Current.CancellationToken);
        Assert.Equal(expected, exit); Assert.Empty(output.ToString()); Assert.NotEmpty(error.ToString());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
