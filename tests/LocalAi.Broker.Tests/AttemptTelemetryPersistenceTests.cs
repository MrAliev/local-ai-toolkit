using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class AttemptTelemetryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localai-attempt-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repeated_attempt_writes_leave_one_observation()
    {
        var record = Record();
        var store = new ModelTelemetryStore(_root);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.AppendAsync(record, TestContext.Current.CancellationToken)));
        Assert.Single(await store.ReadAllAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendAsync(record with { BackendInputTokens = 99 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Future_attempt_schema_is_unreadable_instead_of_silently_misinterpreted()
    {
        var store = new ModelTelemetryStore(_root);
        Directory.CreateDirectory(store.MetricsDirectory);
        await File.WriteAllTextAsync(Path.Combine(store.MetricsDirectory, "future.json"),
            JsonSerializer.Serialize(Record() with { SchemaVersion = 999 }, LocalAiJson.Strict), TestContext.Current.CancellationToken);
        var read = await store.ReadForReportAsync(TestContext.Current.CancellationToken);
        Assert.Empty(read.Records);
        Assert.Equal(1, read.Unreadable);
    }

    [Fact]
    public async Task Old_records_have_unknown_identity_and_backend_usage_without_invented_zeroes()
    {
        var store = new ModelTelemetryStore(_root);
        Directory.CreateDirectory(store.MetricsDirectory);
        var json = JsonSerializer.SerializeToNode(Record(), LocalAiJson.Strict)!.AsObject();
        foreach (var key in new[] { "SchemaVersion", "AttemptId", "WorkflowId", "InputCharacters", "OutputCharacters", "BackendInputTokens", "BackendOutputTokens", "CloudTokenSavings" })
            json.Remove(key);
        await File.WriteAllTextAsync(Path.Combine(store.MetricsDirectory, "old.json"), json.ToJsonString(), TestContext.Current.CancellationToken);
        var loaded = Assert.Single((await store.ReadForReportAsync(TestContext.Current.CancellationToken)).Records);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Null(loaded.AttemptId);
        Assert.Null(loaded.BackendInputTokens);
        Assert.Equal(UsageValueStatus.Unknown, loaded.CloudTokenSavings.Status);
        Assert.Equal("legacy_comparison_unavailable", loaded.CloudTokenSavings.Reason);
    }

    [Fact]
    public async Task An_attempts_own_residency_survives_the_store_and_an_incomplete_one_is_refused()
    {
        var store = new ModelTelemetryStore(_root);
        var record = Record() with
        {
            Residency = ResidencyShortfall.PartialOffload, VramResidentPercent = 42, ResidencyVerifiedAtUtc = DateTimeOffset.UnixEpoch,
        };
        await store.AppendAsync(record, TestContext.Current.CancellationToken);

        var loaded = Assert.Single(await store.ReadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal((record.Residency, record.VramResidentPercent, record.ResidencyVerifiedAtUtc),
            (loaded.Residency, loaded.VramResidentPercent, loaded.ResidencyVerifiedAtUtc));
        await Assert.ThrowsAsync<JsonException>(() => store.AppendAsync(
            Record() with { Residency = ResidencyShortfall.None }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<JsonException>(() => store.AppendAsync(
            Record() with { Residency = ResidencyShortfall.None, VramResidentPercent = 101, ResidencyVerifiedAtUtc = DateTimeOffset.UnixEpoch },
            TestContext.Current.CancellationToken));
    }

    private static ModelTelemetryRecord Record() => new(Guid.NewGuid(), LocalTaskProfile.ShortSummary,
        "qwen3.5:9b", 2048, LocalSizeBucket.Small, LocalSizeBucket.Small, false, false, false,
        "none", ModelExecutionOutcome.Success, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), 0, 0, 0, "test", DateTimeOffset.UtcNow)
    { SchemaVersion = 2, AttemptId = Guid.NewGuid(), BackendInputTokens = 12 };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
