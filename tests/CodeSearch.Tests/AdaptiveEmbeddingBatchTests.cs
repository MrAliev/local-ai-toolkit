using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class AdaptiveEmbeddingBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-adaptive-batches-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_new_machine_profile_starts_with_four_inputs()
    {
        var embedder = new RecordingEmbedder();

        await new IndexBuilder(embedder).BuildAsync(
            SourceTree("first", 10),
            Path.Combine(_root, "index.cidx"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, embedder.BatchSizes[0]);
    }

    [Fact]
    public void Three_full_successes_are_required_before_each_growth_step()
    {
        var policy = new AdaptiveEmbeddingBatchPolicy("test-embedding-model", store: null);

        foreach (var expected in new[] { 4, 8, 16, 32, 64, 128 })
        {
            Assert.Equal(expected, policy.CurrentItems);
            Assert.Null(policy.RecordSuccess(expected));
            Assert.Null(policy.RecordSuccess(expected));
            var growth = policy.RecordSuccess(expected);
            Assert.NotNull(growth);
            Assert.Equal(expected * 2, growth.NextItems);
        }

        Assert.Equal(256, policy.CurrentItems);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Assert.Null(policy.RecordSuccess(256));
        }

        Assert.Equal(256, policy.CurrentItems);
    }

    /// <summary>
    /// A card where the largest batch does not work is not a card the policy should keep
    /// re-proving. Without a ceiling it climbs to 256, the runner drops the request, it steps
    /// back to 128, three successes later it tries 256 again — twice per build on the machine
    /// this was written for, each costing a failed request and a recovery.
    ///
    /// The ceiling stops the growth; it does not lower what already works, and it is a property
    /// of the machine rather than of the product, which is why 256 stays the absolute maximum.
    /// </summary>
    [Fact]
    public void Growth_stops_at_the_ceiling_this_machine_was_given()
    {
        var policy = new AdaptiveEmbeddingBatchPolicy(
            "test-embedding-model",
            store: null,
            maximumItems: 128);

        foreach (var expected in new[] { 4, 8, 16, 32, 64 })
        {
            Assert.Equal(expected, policy.CurrentItems);
            Assert.Null(policy.RecordSuccess(expected));
            Assert.Null(policy.RecordSuccess(expected));
            Assert.NotNull(policy.RecordSuccess(expected));
        }

        Assert.Equal(128, policy.CurrentItems);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Assert.Null(policy.RecordSuccess(128));
        }

        Assert.Equal(128, policy.CurrentItems);
    }

    /// <summary>
    /// A ceiling nobody set is the product's own, so a machine that never chose one behaves
    /// exactly as it did.
    /// </summary>
    [Fact]
    public void A_machine_with_no_ceiling_keeps_the_products_own()
    {
        var policy = new AdaptiveEmbeddingBatchPolicy("test-embedding-model", store: null);

        foreach (var expected in new[] { 4, 8, 16, 32, 64, 128 })
        {
            policy.RecordSuccess(expected);
            policy.RecordSuccess(expected);
            policy.RecordSuccess(expected);
        }

        Assert.Equal(AdaptiveEmbeddingBatchPolicy.MaximumItems, policy.CurrentItems);
    }

    [Fact]
    public async Task The_proved_size_is_saved_and_the_next_build_starts_there()
    {
        var store = new EmbeddingBatchProfileStore(Path.Combine(_root, "runtime"));
        var learning = new RecordingEmbedder();

        await new IndexBuilder(learning, batchProfiles: store).BuildAsync(
            SourceTree("learning", 60),
            Path.Combine(_root, "learning.cidx"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(8, store.Read(learning.Model));
        Assert.True(File.Exists(store.StatePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StatePath)!, "*.tmp"));

        var resumed = new RecordingEmbedder();
        await new IndexBuilder(resumed, batchProfiles: store).BuildAsync(
            SourceTree("resumed", 12),
            Path.Combine(_root, "resumed.cidx"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(8, resumed.BatchSizes[0]);
    }

    [Fact]
    public async Task A_runner_refusal_steps_back_and_retries_the_failed_batch()
    {
        var store = new EmbeddingBatchProfileStore(Path.Combine(_root, "runtime"));
        Assert.True(store.TryWrite("test-embedding-model", 8));
        var embedder = new RefusesMoreThan(8);

        await new IndexBuilder(embedder, batchProfiles: store).BuildAsync(
            SourceTree("backoff", 48),
            Path.Combine(_root, "backoff.cidx"),
            ct: TestContext.Current.CancellationToken);

        var refused = embedder.BatchSizes.IndexOf(16);
        Assert.True(refused >= 0, "the fixture must reach the failing candidate of 16 inputs");
        Assert.Equal(8, embedder.BatchSizes[refused + 1]);
        Assert.Equal(8, store.Read(embedder.Model));
    }

    [Fact]
    public async Task A_second_runner_refusal_at_the_reduced_size_uses_backend_recovery()
    {
        var store = new EmbeddingBatchProfileStore(Path.Combine(_root, "runtime"));
        Assert.True(store.TryWrite("test-embedding-model", 8));
        var embedder = new RunnerAwayFor(3);
        var waits = new List<TimeSpan>();

        await new IndexBuilder(
            embedder,
            backendWait: (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            },
            batchProfiles: store).BuildAsync(
            SourceTree("recovery", 12),
            Path.Combine(_root, "recovery.cidx"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal([8, 4, 4], embedder.BatchSizes.Take(3));
        Assert.Equal([TimeSpan.FromSeconds(5)], waits);
        Assert.Equal(4, store.Read(embedder.Model));
    }

    [Fact]
    public async Task A_runner_refusal_at_the_minimum_size_uses_backend_recovery()
    {
        var embedder = new RunnerAwayFor(2);
        var waits = new List<TimeSpan>();

        await new IndexBuilder(
            embedder,
            backendWait: (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            }).BuildAsync(
            SourceTree("minimum-recovery", 4),
            Path.Combine(_root, "minimum-recovery.cidx"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal([4, 4, 4], embedder.BatchSizes);
        Assert.Equal([TimeSpan.FromSeconds(5)], waits);
    }

    private string SourceTree(string name, int files)
    {
        var sourceRoot = Path.Combine(_root, name);
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < files; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D3}.md"),
                $"unique note {index:D3}\nsmall body");
        }

        return sourceRoot;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private class RecordingEmbedder : IEmbeddingClient
    {
        public string Model => "test-embedding-model";

        public List<int> BatchSizes { get; } = [];

        public virtual Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(inputs.Count);
            return Answer(inputs);
        }

        protected static Task<float[][]> Answer(IReadOnlyList<string> inputs) =>
            Task.FromResult(inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
    }

    private sealed class RefusesMoreThan(int maximum) : RecordingEmbedder
    {
        public override Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(inputs.Count);
            if (inputs.Count > maximum)
            {
                throw new EmbeddingRunnerUnavailableException(
                    "runner_unreachable",
                    new InvalidOperationException("connection refused"));
            }

            return Answer(inputs);
        }
    }

    private sealed class RunnerAwayFor(int refusals) : RecordingEmbedder
    {
        public override Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(inputs.Count);
            if (BatchSizes.Count <= refusals)
            {
                throw new EmbeddingRunnerUnavailableException(
                    "runner_unreachable",
                    new InvalidOperationException("connection refused"));
            }

            return Answer(inputs);
        }
    }
}
