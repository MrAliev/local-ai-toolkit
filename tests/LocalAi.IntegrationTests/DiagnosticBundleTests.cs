using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DiagnosticBundleTests
{
    private const string Secret = "PRIVATE_PROMPT_document_path_model_name";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static void Put(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    private static string Serialize(object value) => JsonSerializer.Serialize(value, DiagnosticBundle.Json);

    [Fact]
    public async Task Projects_state_and_checksum_verified_index_without_exporting_any_source_text()
    {
        using var fixture = new TemporaryDirectory("diagnostic-projection");
        Put(fixture.Path, "jobs/job/state.json", """{"SchemaVersion":1,"State":"Running","WorkerId":"PRIVATE_PROMPT_document_path_model_name"}""");
        Put(fixture.Path, "jobs/job/request.json", Secret);
        Put(fixture.Path, "jobs/job/response.json", Secret);
        Put(fixture.Path, "settings/policy.json", Secret);
        var repository = fixture.Combine("repositories", Secret);
        Directory.CreateDirectory(repository);
        new RepositoryManifestStore(FsPath.From(repository)).Save(new(Secret, Secret, Secret, Secret, Secret, Secret, 1024, 1, 2,
            RepositoryIndexState.Current, [new(Secret, Secret, Secret)], DateTimeOffset.UtcNow));
        new RepositoryIndexProgressStore(FsPath.From(repository)).Save(new(Secret, RepositoryIndexProgressPhase.Completed, Secret, 3, 3, 0, null, DateTimeOffset.UtcNow));
        Put(fixture.Path, "diagnostics.jsonl", """{"operation":"execute","failure":"System.IO.IOException","reason":"PRIVATE_PROMPT_document_path_model_name","jobId":"PRIVATE_PROMPT_document_path_model_name","atUtc":"2026-09-23T10:00:00Z"}""" + "\n");
        Put(fixture.Path, "diagnostics.1.jsonl", "");
        var filesBefore = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        Assert.DoesNotContain(Secret, Serialize(snapshot));
        Assert.Equal(1, snapshot.Contents["queue.json"].GetProperty("states").GetProperty("Running").GetInt32());
        var index = Assert.Single(snapshot.Contents["indexes.json"].GetProperty("repositories").EnumerateArray());
        Assert.Equal("Current", index.GetProperty("recordedState").GetString());
        Assert.Equal("Completed", index.GetProperty("buildPhase").GetString());
        Assert.Equal(1, index.GetProperty("activeWorktrees").GetInt32());
        var error = Assert.Single(snapshot.Contents["errors.json"].GetProperty("groups").EnumerateArray());
        Assert.Equal("IOException", error.GetProperty("failure").GetString());
        Assert.False(snapshot.Manifest.GetProperty("indexFreshnessChecked").GetBoolean());
        Assert.Equal(filesBefore.Keys.Order(), Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in filesBefore) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":\"private\",\"State\":\"Running\"}")]
    [InlineData("{\"SchemaVersion\":1,\"State\":\"Running\",\"State\":\"Queued\"}")]
    [InlineData("{\"SchemaVersion\":99,\"State\":\"Running\"}")]
    [InlineData("{\"SchemaVersion\":1,\"State\":\"1\"}")]
    [InlineData("null")]
    public async Task Malformed_and_future_queue_states_are_not_reported_as_running(string state)
    {
        using var fixture = new TemporaryDirectory("diagnostic-invalid");
        Put(fixture.Path, "jobs/one/state.json", state);
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var queue = snapshot.Contents["queue.json"];
        Assert.Equal(0, queue.GetProperty("states").GetProperty("Running").GetInt32());
        Assert.Equal("partial", queue.GetProperty("source").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Missing_empty_locked_and_oversized_sources_are_distinguished()
    {
        using var fixture = new TemporaryDirectory("diagnostic-limits");
        var missing = await DiagnosticBundle.CaptureAsync(fixture.Combine("missing"), Token);
        Assert.Equal("missing", missing.Contents["queue.json"].GetProperty("source").GetProperty("state").GetString());
        Assert.False(Directory.Exists(fixture.Combine("missing")));
        Directory.CreateDirectory(fixture.Combine("jobs"));
        var empty = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        Assert.Equal("available", empty.Contents["queue.json"].GetProperty("source").GetProperty("state").GetString());
        Put(fixture.Path, "jobs/large/state.json", new string('x', 65537));
        Put(fixture.Path, "jobs/locked/state.json", "{}");
        using var locked = new FileStream(fixture.Combine("jobs", "locked", "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var partial = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var source = partial.Contents["queue.json"].GetProperty("source");
        Assert.True(source.GetProperty("limitReached").GetBoolean());
        Assert.Equal(2, source.GetProperty("omitted").GetInt32());
        Assert.Equal(1, source.GetProperty("issues").GetProperty("unreadable").GetInt32());
        Assert.Equal(1, source.GetProperty("issues").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Bounded_queue_collection_explicitly_reports_the_uncollected_remainder()
    {
        using var fixture = new TemporaryDirectory("diagnostic-bounded");
        for (var i = 0; i <= DiagnosticBundle.MaximumJobs; i++)
            Put(fixture.Path, $"jobs/{i}/state.json", """{"SchemaVersion":1,"State":"Queued"}""");
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var queue = snapshot.Contents["queue.json"];
        Assert.Equal(DiagnosticBundle.MaximumJobs, queue.GetProperty("states").GetProperty("Queued").GetInt32());
        Assert.True(queue.GetProperty("source").GetProperty("limitReached").GetBoolean());
    }

    [Fact]
    public async Task Mismatched_installed_record_does_not_claim_the_wrong_release()
    {
        using var fixture = new TemporaryDirectory("diagnostic-versions");
        Put(fixture.Path, "bin/current.json", """{"schemaVersion":1,"version":"123456789abc"}""");
        Put(fixture.Path, "bin/installed-release.json", """{"schemaVersion":1,"versionDirectory":"000000000000","releaseVersion":"0.2.1-RC22"}""");
        var stale = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        Assert.Equal(JsonValueKind.Null, stale.Contents["versions.json"].GetProperty("installedRelease").ValueKind);
        Put(fixture.Path, "bin/installed-release.json", """{"schemaVersion":1,"versionDirectory":"123456789abc","releaseVersion":"0.2.1-RC22"}""");
        var matched = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        Assert.Equal("0.2.1-RC22", matched.Contents["versions.json"].GetProperty("installedRelease").GetString());
    }

    [Fact]
    public async Task Corrupt_manifest_checksum_cannot_claim_a_current_index()
    {
        using var fixture = new TemporaryDirectory("diagnostic-checksum");
        var repository = fixture.Combine("repositories", "one");
        Directory.CreateDirectory(repository);
        new RepositoryManifestStore(FsPath.From(repository)).Save(new("id", "path", "main", null, null, "model", 1024, 1, 2,
            RepositoryIndexState.Current, [], DateTimeOffset.UtcNow));
        var path = Path.Combine(repository, "manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("1024", "2048", StringComparison.Ordinal));
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var item = Assert.Single(snapshot.Contents["indexes.json"].GetProperty("repositories").EnumerateArray());
        Assert.Equal("Unknown", item.GetProperty("recordedState").GetString());
        Assert.Equal("partial", item.GetProperty("manifestSource").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Metrics_keep_signed_estimates_unknowns_and_input_output_separate_without_exporting_identifiers()
    {
        using var fixture = new TemporaryDirectory("diagnostic-metrics");
        var store = new UsageReportStore(fixture.Path);
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Cli, "localai", "ask", Guid.NewGuid());
        var cloud = report.CloudTokens.ToDictionary();
        cloud[UsageCloudTokens.InputWithoutDelegation] = UsageValue.Estimated(100, Secret);
        cloud[UsageCloudTokens.InputWithDelegation] = UsageValue.Estimated(125, Secret);
        cloud[UsageCloudTokens.InputSaved] = UsageValue.Estimated(-25, Secret);
        report = report with { CloudTokens = cloud, Comparison = new(Secret, Secret, null), Identity = report.Identity with { Operation = Secret } };
        await store.WriteAsync(report, Token);
        await store.WriteAsync(UsageReportingRegistry.CreateReport(UsageSurface.Cli, "localai", "doctor", Guid.NewGuid()), Token);
        // A renamed copy cannot duplicate the same surface-call observation.
        File.Copy(Path.Combine(store.ReportsDirectory, $"{report.Identity.CallId:N}.json"), Path.Combine(store.ReportsDirectory, $"{Guid.NewGuid():N}.json"));
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var metrics = snapshot.Contents["metrics.json"];
        Assert.DoesNotContain(Secret, Serialize(snapshot));
        Assert.DoesNotContain(report.Identity.CallId.ToString(), Serialize(snapshot));
        Assert.Equal(2, metrics.GetProperty("source").GetProperty("read").GetInt32());
        Assert.Equal(1, metrics.GetProperty("source").GetProperty("omitted").GetInt32());
        var input = metrics.GetProperty("cloudInputSavedTokens").GetProperty("byStatus");
        Assert.Equal(-25, input.GetProperty("Estimated").GetProperty("minimum").GetInt64());
        Assert.Equal(1, input.GetProperty("NotApplicable").GetProperty("samples").GetInt32());
        var output = metrics.GetProperty("cloudOutputSavedTokens").GetProperty("byStatus");
        Assert.Equal(JsonValueKind.Null, output.GetProperty("Unknown").GetProperty("minimum").ValueKind);
    }

    [Fact]
    public async Task Zip_contains_exact_preview_bytes_and_hashes_and_never_replaces_an_existing_destination()
    {
        using var fixture = new TemporaryDirectory("diagnostic-export");
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        var path = fixture.Combine("result.zip");
        await DiagnosticBundle.WriteAsync(snapshot, path, Token);
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(6, zip.Entries.Count);
            foreach (var entry in snapshot.Manifest.GetProperty("files").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString()!;
                using var buffer = new MemoryStream();
                using var stream = zip.GetEntry(name)!.Open();
                await stream.CopyToAsync(buffer, Token);
                var bytes = buffer.ToArray();
                Assert.Equal(entry.GetProperty("bytes").GetInt32(), bytes.Length);
                Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
                Assert.Equal(Serialize(snapshot.Contents[name]), Encoding.UTF8.GetString(bytes));
            }
        }
        var original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<DiagnosticOutputExistsException>(() => DiagnosticBundle.WriteAsync(snapshot, path, Token));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task Cancelled_capture_and_export_do_not_create_output_or_runtime_directories()
    {
        using var fixture = new TemporaryDirectory("diagnostic-cancel");
        var snapshot = await DiagnosticBundle.CaptureAsync(fixture.Path, Token);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticBundle.CaptureAsync(fixture.Combine("missing"), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticBundle.WriteAsync(snapshot, fixture.Combine("cancelled.zip"), cancel.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public async Task Linked_source_directories_are_reported_without_following_them()
    {
        using var fixture = new TemporaryDirectory("diagnostic-links");
        var external = fixture.Combine("external");
        Put(external, "one/state.json", """{"SchemaVersion":1,"State":"Running"}""");
        var runtime = fixture.Combine("runtime");
        Directory.CreateDirectory(runtime);
        var link = Path.Combine(runtime, "jobs");
        await DirectoryLinkFixture.CreateAsync(link, external, Token);
        try
        {
            var snapshot = await DiagnosticBundle.CaptureAsync(runtime, Token);
            var queue = snapshot.Contents["queue.json"];
            Assert.Equal(0, queue.GetProperty("states").GetProperty("Running").GetInt32());
            Assert.Equal("unreadable", queue.GetProperty("source").GetProperty("state").GetString());
            Assert.Equal(1, queue.GetProperty("source").GetProperty("issues").GetProperty("linked_source").GetInt32());
        }
        finally { Directory.Delete(link); }
    }
}
