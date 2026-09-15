using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class UsageReportStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localai-usage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_final_report_survives_reopening_and_is_retrievable_by_call_id()
    {
        var report = Report();
        await new UsageReportStore(_root).WriteAsync(report, TestContext.Current.CancellationToken);
        var loaded = await new UsageReportStore(_root).ReadAsync(report.Identity.CallId, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(UsageReportJson.Write(report), UsageReportJson.Write(loaded));
    }

    [Fact]
    public async Task Concurrent_repeated_writes_leave_one_final_report()
    {
        var report = Report();
        var store = new UsageReportStore(_root);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.WriteAsync(report, TestContext.Current.CancellationToken)));
        Assert.Single((await store.ReadForReportAsync(TestContext.Current.CancellationToken)).Reports);
        Assert.Empty(Directory.GetFiles(store.ReportsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task A_conflicting_final_report_cannot_replace_the_original()
    {
        var report = Report();
        var store = new UsageReportStore(_root);
        await store.WriteAsync(report, TestContext.Current.CancellationToken);
        var changed = report with { Outcome = report.Outcome with { Technical = UsageTechnicalOutcome.Cancelled, TechnicalReason = "cancelled" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(changed, TestContext.Current.CancellationToken));
        var loaded = await store.ReadAsync(report.Identity.CallId, TestContext.Current.CancellationToken);
        Assert.Equal(UsageReportJson.Write(report), UsageReportJson.Write(loaded!));
    }

    [Fact]
    public async Task Unsupported_or_truncated_records_are_counted_without_losing_valid_reports()
    {
        var store = new UsageReportStore(_root);
        var report = Report();
        await store.WriteAsync(report, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(store.ReportsDirectory);
        await File.WriteAllTextAsync(Path.Combine(store.ReportsDirectory, "broken.json"), "{", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(store.ReportsDirectory, "future.json"),
            UsageReportJson.Write(report).Replace("\"schemaVersion\":1", "\"schemaVersion\":999", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        var read = await store.ReadForReportAsync(TestContext.Current.CancellationToken);
        Assert.Single(read.Reports);
        Assert.Equal(2, read.Unreadable);
    }

    private static UsageReport Report() => UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", "ask_local", Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
