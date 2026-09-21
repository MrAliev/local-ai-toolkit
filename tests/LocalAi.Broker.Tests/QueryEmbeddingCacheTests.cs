using System.Net;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class QueryEmbeddingCacheTests
{
    [Fact]
    public async Task A_repeated_search_reuses_the_vector_after_checking_the_current_model_digest()
    {
        using var server = new Backend();
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var first = await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        var second = await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(first.Body.GetRawText(), second.Body.GetRawText());
        Assert.Equal(1, server.Embeddings);
        Assert.Equal(3, server.Identities);
    }

    [Fact]
    public async Task Model_digest_prepared_input_and_context_changes_do_not_reuse_a_vector()
    {
        using var server = new Backend();
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        await Run(Query());
        server.Digest = new string('b', 64);
        await Run(Query());
        await Run(Query(input: "new instruction: prepared query"));
        await Run(Query(context: 4096));
        await Run(Query(model: "embedding:2"));
        Assert.Equal(5, server.Embeddings);
        await Run(Query(model: "embedding:2"));
        Assert.Equal(5, server.Embeddings);
        Task<BrokerExecutionResult> Run(LocalJobRequest request) => transport.ExecuteAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    public async Task Unknown_or_unreadable_identity_bypasses_the_cache(string? identity)
    {
        using var server = new Backend();
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        server.Digest = identity;
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(3, server.Embeddings);
    }

    [Fact]
    public async Task An_identity_change_during_embedding_does_not_seed_the_cache()
    {
        using var server = new Backend { AfterEmbed = backend => backend.Digest = new string('b', 64) };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Embeddings);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("zero")]
    public async Task Failed_or_unusable_embeddings_do_not_seed_the_cache(string mode)
    {
        using var server = new Backend { EmbedMode = mode };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        if (mode == "failure")
            await Assert.ThrowsAsync<HttpRequestException>(() => transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken));
        else await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        server.EmbedMode = "normal";
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Embeddings);
    }

    [Fact]
    public async Task Concurrent_requests_reuse_the_completed_vector_and_a_cancelled_waiter_does_not_cancel_its_owner()
    {
        using var server = new Backend { BlockEmbedding = true };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var first = transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await server.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiter = transport.ExecuteAsync(Query(), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var others = Enumerable.Range(0, 8).Select(_ => transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken)).ToArray();
        server.Resume.SetResult();
        var expected = await first;
        Assert.All(await Task.WhenAll(others), result => Assert.Equal(expected.Body.GetRawText(), result.Body.GetRawText()));
        Assert.Equal(1, server.Embeddings);
    }

    [Fact]
    public async Task Cancelled_inference_releases_the_gate_and_is_not_reused()
    {
        using var server = new Backend { BlockEmbedding = true };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = transport.ExecuteAsync(Query(), cancelled.Token);
        await server.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        server.BlockEmbedding = false;
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Embeddings);
    }

    [Fact]
    public async Task Old_queries_are_evicted_and_a_fresh_transport_has_no_cached_output()
    {
        using var server = new Backend();
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        for (var i = 0; i < 129; i++) await transport.ExecuteAsync(Query(input: "query-" + i), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(input: "query-128"), TestContext.Current.CancellationToken);
        Assert.Equal(129, server.Embeddings);
        await transport.ExecuteAsync(Query(input: "query-0"), TestContext.Current.CancellationToken);
        Assert.Equal(130, server.Embeddings);
        using var restarted = new OllamaTransport(client, new Uri("http://embedding.test/"));
        await restarted.ExecuteAsync(Query(input: "query-0"), TestContext.Current.CancellationToken);
        Assert.Equal(131, server.Embeddings);
    }

    [Fact]
    public async Task Serialized_vector_byte_limit_evicts_entries_before_the_count_limit()
    {
        using var server = new Backend { VectorLength = 2_000_000 };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        // Each vector is just over 4 MB in JSON: two fit, three exceed 8 MiB.
        foreach (var input in new[] { "a", "b", "c", "b", "a" })
            await transport.ExecuteAsync(Query(input: input), TestContext.Current.CancellationToken);
        Assert.Equal(4, server.Embeddings);
    }

    [Fact]
    public async Task A_vector_larger_than_the_byte_limit_is_returned_but_not_cached()
    {
        using var server = new Backend { VectorLength = 4_200_000 };
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        for (var i = 0; i < 2; i++)
        {
            var result = await transport.ExecuteAsync(Query(), TestContext.Current.CancellationToken);
            Assert.Equal(server.VectorLength, result.Body.GetProperty("Embeddings")[0].GetArrayLength());
        }
        Assert.Equal(2, server.Embeddings);
    }

    [Fact]
    public async Task Ordinary_embedding_jobs_do_not_pay_for_identity_checks_or_use_the_search_cache()
    {
        using var server = new Backend();
        using var client = new HttpClient(server);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        await transport.ExecuteAsync(Query(prefix: "index:"), TestContext.Current.CancellationToken);
        await transport.ExecuteAsync(Query(prefix: "index:"), TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Embeddings);
        Assert.Equal(0, server.Identities);
    }

    private static LocalJobRequest Query(string input = "prepared query", string model = "embedding:1", int context = 8192,
        string prefix = "codesearch:query:") => LocalJobRequestFactory.CreateEmbed(
            prefix + Guid.NewGuid(), LocalJobPriority.Interactive, model, [input], requestedContextTokens: context);

    private sealed class Backend : HttpMessageHandler
    {
        internal int Embeddings;
        internal int Identities;
        internal string? Digest = new string('a', 64);
        internal string EmbedMode = "normal";
        internal int VectorLength;
        internal Action<Backend>? AfterEmbed;
        internal bool BlockEmbedding;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body;
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                Identities++;
                if (Digest == "unavailable") return new(HttpStatusCode.NotFound) { Content = new StringContent("metadata unavailable") };
                body = Digest == "malformed" ? "broken" : JsonSerializer.Serialize(new { models = new[] {
                    new { name = "embedding:1", digest = Digest },
                    new { name = Digest == "duplicate" ? "embedding:1" : "embedding:2", digest = Digest } } });
            }
            else
            {
                Embeddings++;
                if (BlockEmbedding) { Started.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
                if (EmbedMode == "failure") return new(HttpStatusCode.BadRequest) { Content = new StringContent("embedding failed") };
                body = EmbedMode == "zero" ? "{\"embeddings\":[[0,0,0]]}" : JsonSerializer.Serialize(new { embeddings = new[] {
                    VectorLength > 0 ? Enumerable.Repeat(1d, VectorLength).ToArray() : new[] { (double)Embeddings, 2, 3 } } });
                AfterEmbed?.Invoke(this);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
