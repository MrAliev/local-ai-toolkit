using System.Net;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class EmbeddingTimingTests
{
    [Theory]
    [InlineData("{}", null, null, null, null)]
    [InlineData("{\"total_duration\":0,\"load_duration\":0,\"prompt_eval_duration\":0,\"prompt_eval_count\":0}", 0L, 0L, 0L, 0L)]
    [InlineData("{\"total_duration\":-1,\"load_duration\":\"4\",\"prompt_eval_duration\":1.5,\"prompt_eval_count\":1e40}", null, null, null, null)]
    [InlineData("{\"total_duration\":10,\"load_duration\":11,\"prompt_eval_duration\":12}", 10L, null, null, null)]
    [InlineData("{\"total_duration\":10,\"load_duration\":4,\"prompt_eval_duration\":7}", 10L, 4L, null, null)]
    [InlineData("{\"total_duration\":10,\"load_duration\":4,\"prompt_eval_count\":9}", 10L, 4L, null, 9L)]
    public async Task Missing_invalid_or_inconsistent_metrics_stay_unknown_without_rejecting_the_vector(
        string metrics, long? total, long? load, long? evaluation, long? tokens)
    {
        using var server = new FakeOllamaServer();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metrics)!;
        body["embeddings"] = JsonSerializer.SerializeToElement(new[] { new[] { 1, 2, 3 } });
        server.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(body));
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var request = LocalJobRequestFactory.CreateEmbed("timing", LocalJobPriority.Interactive, "embedding:1", ["query"]);
        var result = await transport.ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new EmbeddingBackendTiming(false, total, load, evaluation, tokens), result.EmbeddingTiming);
        Assert.Equal("{\"Embeddings\":[[1,2,3]]}", result.Body.GetRawText());
    }

    [Fact]
    public async Task A_cache_hit_does_not_replay_the_original_backend_timings_or_token_count()
    {
        using var server = new FakeOllamaServer();
        var tags = JsonSerializer.Serialize(new { models = new[] { new { name = "embedding:1", digest = new string('a', 64) } } });
        server.EnqueueJson(HttpStatusCode.OK, tags);
        server.EnqueueJson(HttpStatusCode.OK, """{"embeddings":[[1,2,3]],"total_duration":12,"load_duration":4,"prompt_eval_count":9}""");
        server.EnqueueJson(HttpStatusCode.OK, tags);
        server.EnqueueJson(HttpStatusCode.OK, tags);
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var first = await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        var second = await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(new EmbeddingBackendTiming(false, 12, 4, null, 9), first.EmbeddingTiming);
        Assert.Equal(new EmbeddingBackendTiming(true, null, null, null, null), second.EmbeddingTiming);
        Assert.Null(second.BackendInputTokens);
        Assert.Equal(first.Body.GetRawText(), second.Body.GetRawText());
        Assert.Single(server.Requests, request => request.Uri.AbsolutePath == "/api/embed");
        static LocalJobRequest Query() => LocalJobRequestFactory.CreateEmbed("codesearch:query:" + Guid.NewGuid(),
            LocalJobPriority.Interactive, "embedding:1", ["query"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostic_write_is_content_free_and_a_storage_failure_does_not_fail_the_job(bool failWrite)
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var store = new EmbeddingTimingStore(root.Path);
        if (failWrite) File.WriteAllText(Path.Combine(root.Path, "telemetry"), "blocks directory creation");
        var diagnostics = new List<BrokerHostDiagnostic>();
        var request = LocalJobRequestFactory.CreateEmbed("diagnostic", LocalJobPriority.Interactive, "embedding:1", ["private query text"]);
        await queue.EnqueueAsync(request, TestContext.Current.CancellationToken);
        var timing = new EmbeddingBackendTiming(false, 12, 4, null, 9);
        var result = new BrokerExecutionResult(JsonSerializer.SerializeToElement(new EmbedJobOutput([[1d, 2d, 3d]])))
        { EmbeddingTiming = timing };
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var host = new BrokerHost(queue, "timing-worker", (_, _, _) => Task.FromResult(result),
            diagnostic: diagnostics.Add,
            resultObserver: (completed, output) => store.Write(completed.JobId, output.EmbeddingTiming!));
        var pending = new BrokerClient(queue, new RunningProcess()).ExecuteAsync<EmbedJobOutput>(request, TestContext.Current.CancellationToken);
        var running = host.RunAsync(shutdown.Token);
        var response = await pending;
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(new[] { 1d, 2d, 3d }, Assert.Single(response.Value.Embeddings));
        Assert.Equal(request.JobId, response.Receipt.JobId);
        if (failWrite)
            Assert.Contains(diagnostics, entry => entry.Operation == "result-observe");
        else
        {
            var stored = Assert.IsType<EmbeddingTimingRecord>(store.Read(request.JobId));
            Assert.Equal(request.JobId, stored.JobId);
            Assert.Equal(timing, stored.Backend);
            var json = File.ReadAllText(store.PathFor(request.JobId));
            Assert.DoesNotContain("private query text", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Embeddings", json, StringComparison.Ordinal);
            Assert.DoesNotContain("embedding:1", json, StringComparison.Ordinal);
            Assert.Empty(diagnostics);
        }
    }

    private sealed class RunningProcess : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void Diagnostic_reader_distinguishes_an_absent_record_from_a_malformed_or_misattributed_record()
    {
        using var root = new TemporaryRuntimeRoot();
        var store = new EmbeddingTimingStore(root.Path);
        var id = Guid.NewGuid();
        Assert.Null(store.Read(id));
        store.Write(id, new EmbeddingBackendTiming(true, null, null, null, null));
        var record = store.Read(id)!;
        File.WriteAllText(store.PathFor(id), JsonSerializer.Serialize(record with { JobId = Guid.NewGuid() }, LocalAiJson.Strict));
        Assert.Throws<InvalidDataException>(() => store.Read(id));
        File.WriteAllText(store.PathFor(id), "broken");
        Assert.Throws<JsonException>(() => store.Read(id));
        Assert.Throws<InvalidDataException>(() => store.Write(id, new EmbeddingBackendTiming(true, 1, null, null, null)));
    }

    [Fact]
    public async Task Backend_embedding_timings_survive_transport_without_changing_the_vector_contract()
    {
        using var server = new FakeOllamaServer();
        server.EnqueueJson(HttpStatusCode.OK,
            """{"embeddings":[[1,2,3]],"total_duration":12000000,"load_duration":4000000,"prompt_eval_duration":7000000,"prompt_eval_count":9}""");
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var request = LocalJobRequestFactory.CreateEmbed("timing", LocalJobPriority.Interactive, "embedding:1", ["query"]);
        var result = await transport.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var timing = Assert.IsType<EmbeddingBackendTiming>(result.EmbeddingTiming);
        Assert.Equal(new EmbeddingBackendTiming(false, 12000000, 4000000, 7000000, 9), timing);
        Assert.Equal("{\"Embeddings\":[[1,2,3]]}", result.Body.GetRawText());
        Assert.Equal(3, result.Body.Deserialize<EmbedJobOutput>(LocalAiJson.Strict)!.Embeddings[0].Count);
    }
}
