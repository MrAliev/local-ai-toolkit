using System.Net;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class EmbeddingResidencyTests : IDisposable
{
    private const string Model = "qwen3-embedding:8b-q8_0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "embedding-residency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Partial_residency_refuses_the_embedding_payload_before_it_is_sent()
    {
        using var backend = new Backend { SizeVram = 60 };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var router = Router(transport);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(() =>
            router.ExecuteAsync(Request(), null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.CpuOffload, error.Outcome);
        Assert.Equal(1, backend.Preflights);
        Assert.Equal(0, backend.Embeddings);
    }

    [Theory]
    [InlineData(ModelResidencyPolicy.RequireFullVram, 100, ResidencyShortfall.None)]
    [InlineData(ModelResidencyPolicy.AllowPartialOffload, 60, ResidencyShortfall.PartialOffload)]
    [InlineData(ModelResidencyPolicy.AllowCpu, 0, ResidencyShortfall.Cpu)]
    public async Task An_embedding_carries_its_own_proof_into_the_usage_notice(
        ModelResidencyPolicy policy, int sizeVram, ResidencyShortfall shortfall)
    {
        using var backend = new Backend { SizeVram = sizeVram };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var result = await Router(transport, policy).ExecuteAsync(Request(), null, TestContext.Current.CancellationToken);

        var routing = Assert.IsType<LocalRoutingReceipt>(result.Routing);
        Assert.Equal(Model, routing.SelectedModel);
        Assert.Equal(2048, routing.ContextTokens);
        Assert.Equal(sizeVram, routing.VramResidentPercent);
        Assert.Equal(shortfall, routing.ResidencyShortfall);
        Assert.Equal(1, backend.Preflights);
        Assert.Equal(1, backend.Embeddings);

        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        var job = Guid.NewGuid();
        call.LinkJob(job);
        call.RegisterJobEvidence(root, job, new LocalUsageReceipt(job, "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null, routing));
        await UsageEvidence.CollectAsync(call);
        var model = Assert.Single(call.Complete().Models.Actual);
        Assert.Equal(shortfall switch
        {
            ResidencyShortfall.PartialOffload => UsageOffload.Partial,
            ResidencyShortfall.Cpu => UsageOffload.Cpu,
            _ => UsageOffload.None,
        }, model.Offload);
    }

    [Fact]
    public async Task A_cached_query_does_not_repeat_preflight_or_inherit_a_previous_residency_proof()
    {
        using var backend = new Backend();
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var router = Router(transport);
        var first = await router.ExecuteAsync(Request(), null, TestContext.Current.CancellationToken);
        var second = await router.ExecuteAsync(Request(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(first.Routing);
        Assert.Null(second.Routing);
        Assert.True(second.EmbeddingTiming!.CacheHit);
        Assert.False(second.InferenceExecuted);
        Assert.Equal(1, backend.Preflights);
        Assert.Equal(1, backend.Embeddings);
    }

    [Fact]
    public async Task An_external_embedding_model_keeps_its_supported_path_with_an_explicit_default_context()
    {
        const string customModel = "external-embedding:1";
        using var backend = new Backend { Model = customModel };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var router = Router(transport);
        var request = LocalJobRequestFactory.CreateEmbed("external", LocalJobPriority.Foreground, customModel, ["text"]);

        var result = await router.ExecuteAsync(request, null, TestContext.Current.CancellationToken);

        var routing = Assert.IsType<LocalRoutingReceipt>(result.Routing);
        Assert.Equal(customModel, routing.SelectedModel);
        Assert.Equal(16384, routing.ContextTokens);
        Assert.Equal(16384, backend.LastEmbeddingContext);
        Assert.Equal(100, routing.VramResidentPercent);
        Assert.Equal(1, backend.Preflights);
        Assert.Equal(1, backend.Embeddings);
        Assert.Equal(customModel, router.ResidentModel);
    }

    [Fact]
    public async Task A_cached_query_is_reported_as_no_inference_instead_of_unknown_residency()
    {
        using var call = new UsageCall(UsageSurface.Cli, "codesearch", "search");
        var job = Guid.NewGuid();
        call.LinkJob(job);
        call.RegisterJobEvidence(root, job, new LocalUsageReceipt(job, "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null) { InferenceExecuted = false });

        await UsageEvidence.CollectAsync(call);

        var report = call.Complete();
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.Equal(UsageCache.Hit, report.Models.Cache);
        Assert.Empty(report.Models.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_external_model_at_another_context_is_refused_without_unloading_it_or_other_models(bool anotherManagedModel)
    {
        using var backend = new Backend { Model = "external-embedding:1", Loaded = true,
            OtherManagedModel = anotherManagedModel ? "qwen3.5:9b" : null };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var request = LocalJobRequestFactory.CreateEmbed("external", LocalJobPriority.Foreground,
            backend.Model, ["text"], requestedContextTokens: 4096);
        var router = Router(transport);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(() =>
            router.ExecuteAsync(request, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.ContextFailure, error.Outcome);
        Assert.Equal(0, backend.Preflights);
        Assert.Equal(0, backend.Embeddings);
        Assert.Equal(0, backend.Unloads);

        backend.Context = 4096; // The external owner corrected its loaded context.
        var fixedContext = await router.ExecuteAsync(request, null, TestContext.Current.CancellationToken);
        Assert.Equal(4096, fixedContext.Routing!.ContextTokens);
    }

    [Theory]
    [InlineData("external-embedding:1")]
    [InlineData(Model)]
    public async Task An_ambiguous_process_snapshot_is_refused_before_unloading_any_model(string requestedModel)
    {
        using var backend = new Backend { Model = requestedModel, Loaded = true,
            DuplicateTarget = true, OtherManagedModel = "qwen3.5:9b" };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var request = LocalJobRequestFactory.CreateEmbed("external", LocalJobPriority.Foreground,
            backend.Model, ["text"], requestedContextTokens: 2048);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(() =>
            Router(transport).ExecuteAsync(request, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.TechnicalFailure, error.Outcome);
        Assert.Equal(0, backend.Preflights);
        Assert.Equal(0, backend.Embeddings);
        Assert.Equal(0, backend.Unloads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cache_evidence_only_resolves_the_inference_of_its_own_job(bool allCached)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        foreach (var cached in new[] { true, allCached })
        {
            var job = Guid.NewGuid();
            call.LinkJob(job);
            call.RegisterJobEvidence(root, job, cached ? new LocalUsageReceipt(job, "code-search", "embed", Model,
                TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null) { InferenceExecuted = false } : null);
        }

        await UsageEvidence.CollectAsync(call);

        Assert.Equal(allCached ? UsageModelState.NotCalled : UsageModelState.Unknown, call.Complete().Models.State);
    }

    [Fact]
    public async Task A_retry_rechecks_residency_after_the_runner_returns_a_transient_failure()
    {
        using var backend = new Backend { OffloadAfterFirstUsefulFailure = true };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"),
            (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(() =>
            Router(transport).ExecuteAsync(Request(), null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.CpuOffload, error.Outcome);
        Assert.Equal(2, backend.Preflights);
        Assert.Equal(1, backend.Embeddings);
    }

    [Fact]
    public async Task An_admitted_retry_reports_its_new_partial_proof_not_the_original_full_proof()
    {
        using var backend = new Backend { OffloadAfterFirstUsefulFailure = true };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"),
            (_, _) => Task.CompletedTask);

        var result = await Router(transport, ModelResidencyPolicy.AllowPartialOffload)
            .ExecuteAsync(Request(), null, TestContext.Current.CancellationToken);

        Assert.Equal(ResidencyShortfall.PartialOffload, result.Routing!.ResidencyShortfall);
        Assert.Equal(60, result.Routing!.VramResidentPercent);
        Assert.Equal(2, backend.Preflights);
        Assert.Equal(2, backend.Embeddings);
    }

    [Fact]
    public async Task A_receipt_for_another_job_cannot_resolve_the_linked_jobs_inference()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        var linked = Guid.NewGuid();
        call.LinkJob(linked);
        var foreign = new LocalUsageReceipt(Guid.NewGuid(), "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null) { InferenceExecuted = false };

        Assert.Throws<ArgumentException>(() => call.RegisterJobEvidence(root, linked, foreign));
        await UsageEvidence.CollectAsync(call);

        Assert.Equal(UsageModelState.Unknown, call.Complete().Models.State);
    }

    [Fact]
    public async Task Routing_for_another_model_does_not_prove_receipt_residency()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        var job = Guid.NewGuid();
        call.LinkJob(job);
        var routing = new LocalRoutingReceipt(null, "another-model", 2048, false, false, null,
            0, 0, 0, VramResidentPercent: 100);
        call.RegisterJobEvidence(root, job, new LocalUsageReceipt(job, "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null, routing));

        await UsageEvidence.CollectAsync(call);

        Assert.Equal(UsageOffload.Unknown, Assert.Single(call.Complete().Models.Actual).Offload);
    }

    [Theory]
    [InlineData(2048, UsageOffload.None)]
    [InlineData(4096, UsageOffload.Unknown)]
    public async Task Routing_must_match_the_context_requested_by_the_linked_embedding_job(int receiptContext, UsageOffload expected)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        var job = Guid.NewGuid();
        call.LinkJob(job);
        call.RegisterJobEvidence(root, job, expectedModel: Model, expectedContextTokens: 2048);
        var routing = new LocalRoutingReceipt(null, Model, receiptContext, false, false, null,
            0, 0, 0, VramResidentPercent: 100);
        call.RegisterJobEvidence(root, job, new LocalUsageReceipt(job, "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null, routing));

        await UsageEvidence.CollectAsync(call);

        Assert.Equal(expected, Assert.Single(call.Complete().Models.Actual).Offload);
    }

    [Fact]
    public async Task An_omitted_external_context_that_the_backend_clamps_is_refused_with_the_expected_tier()
    {
        using var backend = new Backend { Model = "external-embedding:1", ClampContext = 2048 };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var request = LocalJobRequestFactory.CreateEmbed("external", LocalJobPriority.Foreground, backend.Model, ["text"]);

        var error = await Assert.ThrowsAsync<ModelPreflightException>(() =>
            Router(transport).ExecuteAsync(request, null, TestContext.Current.CancellationToken));

        Assert.Equal(ModelExecutionOutcome.ContextFailure, error.Outcome);
        Assert.Contains("16384", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, backend.Embeddings);
    }

    private BrokerExecutionRouter Router(OllamaTransport transport,
        ModelResidencyPolicy policy = ModelResidencyPolicy.RequireFullVram)
    {
        var catalog = ModelRoutingCatalog.LoadEmbedded();
        var runtime = new ModelRuntime(transport, catalog, residencyPolicy: policy);
        var experiments = new ExperimentStateStore(root);
        var telemetry = new ModelTelemetryStore(root);
        return new BrokerExecutionRouter(catalog, transport, runtime,
            new ModelExecutionCoordinator(new ModelRouter(catalog), runtime, experiments, telemetry, transport.ExecuteAsync),
            new ModelControlService(catalog, transport, experiments, telemetry), transport.ExecuteAsync);
    }

    [Fact]
    public async Task Cancelling_preflight_does_not_send_the_request_or_seed_the_query_cache()
    {
        using var backend = new Backend { BlockPreflight = true };
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var router = Router(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = router.ExecuteAsync(Request(), null, cancellation.Token);
        await backend.PreflightStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, backend.Embeddings);
        backend.BlockPreflight = false;

        var result = await router.ExecuteAsync(Request(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Routing);
        Assert.False(result.EmbeddingTiming!.CacheHit);
        Assert.Equal(2, backend.Preflights);
        Assert.Equal(1, backend.Embeddings);
    }

    [Fact]
    public async Task Parallel_embeddings_keep_their_own_model_context_and_residency()
    {
        using var backend = new Backend();
        using var client = new HttpClient(backend);
        using var transport = new OllamaTransport(client, new Uri("http://embedding.test/"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<ModelResidencyProof> Prepare(EmbedJobPayload payload, CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(ct);
            var full = payload.RequestedContextTokens == 2048;
            return new(payload.Model, payload.RequestedContextTokens!.Value, 100, full ? 100 : 60,
                full, DateTimeOffset.UtcNow);
        }
        var requests = new[]
        {
            LocalJobRequestFactory.CreateEmbed("first", LocalJobPriority.Foreground, "first-model", ["one"], requestedContextTokens: 2048),
            LocalJobRequestFactory.CreateEmbed("second", LocalJobPriority.Foreground, "second-model", ["two"], requestedContextTokens: 4096),
        };

        var results = await Task.WhenAll(requests.Select(request =>
            transport.ExecuteEmbeddingAsync(request, Prepare, TestContext.Current.CancellationToken)));

        Assert.Equal("first-model", results[0].Routing!.SelectedModel);
        Assert.Equal(2048, results[0].Routing!.ContextTokens);
        Assert.Equal(ResidencyShortfall.None, results[0].Routing!.ResidencyShortfall);
        Assert.Equal("second-model", results[1].Routing!.SelectedModel);
        Assert.Equal(4096, results[1].Routing!.ContextTokens);
        Assert.Equal(ResidencyShortfall.PartialOffload, results[1].Routing!.ResidencyShortfall);
    }

    [Fact]
    public void Cache_inference_evidence_roundtrips_and_old_receipts_keep_unknown_execution()
    {
        var receipt = new LocalUsageReceipt(Guid.NewGuid(), "code-search", "embed", Model,
            TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null);
        var legacy = JsonSerializer.Deserialize<LocalUsageReceipt>(JsonSerializer.Serialize(receipt, LocalAiJson.Strict), LocalAiJson.Strict);
        var cached = JsonSerializer.Deserialize<LocalUsageReceipt>(JsonSerializer.Serialize(receipt with
            { InferenceExecuted = false }, LocalAiJson.Strict), LocalAiJson.Strict);
        Assert.Null(legacy!.InferenceExecuted);
        Assert.False(cached!.InferenceExecuted);
    }

    private static LocalJobRequest Request() => LocalJobRequestFactory.CreateEmbed(
        "codesearch:query:" + Guid.NewGuid(), LocalJobPriority.Interactive, Model, ["prepared query"],
        requestedContextTokens: 2048);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Backend : HttpMessageHandler
    {
        internal string Model = EmbeddingResidencyTests.Model;
        internal int SizeVram = 100;
        internal int Preflights;
        internal int Embeddings;
        internal int? LastEmbeddingContext;
        internal bool Loaded;
        internal string? OtherManagedModel;
        internal bool DuplicateTarget;
        internal bool OffloadAfterFirstUsefulFailure;
        internal int Unloads;
        internal bool BlockPreflight;
        internal TaskCompletionSource PreflightStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Context = 2048;
        internal int? ClampContext;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/tags":
                    body = new { models = new[] { new { name = Model, digest = new string('a', 64) } } };
                    break;
                case "/api/ps":
                    var models = new List<object>();
                    if (Loaded)
                    {
                        var target = new { name = Model, size = 100, size_vram = SizeVram,
                            context_length = Context, expires_at = DateTimeOffset.UtcNow.AddMinutes(30) };
                        models.Add(target);
                        if (DuplicateTarget) models.Add(target);
                    }
                    if (OtherManagedModel is { } otherModel)
                        models.Add(new { name = otherModel, size = 100, size_vram = 100,
                            context_length = 2048, expires_at = DateTimeOffset.UtcNow.AddMinutes(30) });
                    body = new { models };
                    break;
                case "/api/embed":
                    using (var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        var input = document.RootElement.GetProperty("input")[0].GetString();
                        var selected = document.RootElement.TryGetProperty("options", out var options)
                            ? options.GetProperty("num_ctx").GetInt32() : (int?)null;
                        if (input == "localai-preflight")
                        {
                            Preflights++;
                            PreflightStarted.TrySetResult();
                            if (BlockPreflight) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                            Loaded = true;
                            Context = ClampContext ?? selected!.Value;
                        }
                        else
                        {
                            Embeddings++;
                            LastEmbeddingContext = selected;
                            if (OffloadAfterFirstUsefulFailure && Embeddings == 1)
                            {
                                SizeVram = 60;
                                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                                    { Content = new StringContent("runner restarting") };
                            }
                        }
                    }
                    body = new { embeddings = new[] { new[] { 1d, 2, 3 } } };
                    break;
                case "/api/generate":
                    Unloads++;
                    body = new { done = true, response = "" };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected fake request: " + request.RequestUri);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
