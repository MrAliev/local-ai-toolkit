using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class EmbeddingCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-embedding-checkpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Completed_batches_are_reused_after_interrupted_build()
    {
        var sourceRoot = Path.Combine(_root, "source");
        var indexPath = Path.Combine(_root, "index.cidx");
        var checkpointPath = Path.Combine(_root, "checkpoint");
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < 40; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"unique-{index:D2}\n" + new string((char)('a' + index % 26), 6_000));
        }

        var interrupted = new InterruptOnSecondBatchEmbedder();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new IndexBuilder(interrupted).BuildAsync(
                sourceRoot,
                indexPath,
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: checkpointPath,
                expectedEmbeddingDimension: 2));

        Assert.NotEmpty(interrupted.CompletedInputs);
        Assert.NotEmpty(Directory.EnumerateFiles(checkpointPath, "*.batch"));

        var progress = new List<IndexBuildProgress>();
        var resumed = new RecordingEmbedder();
        var result = await new IndexBuilder(resumed, progress: progress.Add).BuildAsync(
            sourceRoot,
            indexPath,
            ct: TestContext.Current.CancellationToken,
            embeddingCheckpointPath: checkpointPath,
            expectedEmbeddingDimension: 2);

        Assert.Equal(
            result.ChunksEmbedded,
            interrupted.CompletedInputs.Count + resumed.Inputs.Count);
        Assert.Empty(interrupted.CompletedInputs.Intersect(resumed.Inputs, StringComparer.Ordinal));
        Assert.Contains(
            progress,
            item => item.ProcessedChunks == interrupted.CompletedInputs.Count &&
                    item.TotalChunks == result.ChunksEmbedded);

        // Every record, not just the one the restore emits. `RepositoryIndexProgressStore`
        // rejects a count above its own total or a negative estimate, and a resumed build used
        // to report both: the counter starts at what the checkpoint restored and was compared
        // against what was left to embed, so it read 21 075 of 20 980 with minus a minute to go.
        // The store threw, and the build died after every chunk of it had already been embedded —
        // on a repository where that is half an hour of GPU time thrown away at the finish line.
        Assert.All(progress, item =>
        {
            Assert.InRange(item.ProcessedChunks, 0, item.TotalChunks);
            Assert.True(
                item.EstimatedRemaining is null || item.EstimatedRemaining >= TimeSpan.Zero,
                $"Estimated remaining was {item.EstimatedRemaining}.");
        });
        Assert.Equal(result.ChunksEmbedded, progress[^1].ProcessedChunks);
        Assert.Equal(result.ChunksEmbedded, progress[^1].TotalChunks);
        Assert.Equal(result.ChunkCount, CodeIndex.Load(indexPath).Chunks.Count);
    }

    /// <summary>
    /// Halving a failed batch is what keeps one bad chunk from ending a multi-hour run, but the
    /// half that succeeded was thrown away with the half that did not: <c>SaveBatch</c> is reached
    /// only when the whole batch comes back, so a deterministic failure at the end of a batch of
    /// 512 made the build recompute up to 511 good chunks on every restart after it — and,
    /// because the failure is deterministic, on every restart for ever.
    ///
    /// The measure is what the second run asks for. A chunk this run already embedded must not be
    /// requested again, whatever happened to the chunk beside it.
    /// </summary>
    [Fact]
    public async Task A_half_that_succeeded_outlives_the_half_that_failed()
    {
        const string poison = "poison-chunk";
        var sourceRoot = Path.Combine(_root, "source");
        var checkpointPath = Path.Combine(_root, "checkpoint");
        Directory.CreateDirectory(sourceRoot);

        // Twenty files of about 2 000 characters: 40 000 in total, inside the 48 000-character
        // opening budget, so every chunk lands in one batch and the failure is halved rather
        // than isolated by the batching itself.
        for (var index = 0; index < 20; index++)
        {
            var marker = index == 19 ? poison : $"clean-{index:D2}";
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"{marker}\n" + new string((char)('a' + index % 26), 2_000));
        }

        var first = new PoisonChunkEmbedder(poison);
        await Assert.ThrowsAsync<EmbeddingChunkException>(
            () => new IndexBuilder(first).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "first.cidx"),
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: checkpointPath,
                expectedEmbeddingDimension: 2));

        // Without this the rest proves nothing: it says the batch really was halved and one half
        // really did succeed, rather than the whole thing failing on its first call.
        Assert.NotEmpty(first.CompletedInputs);

        var second = new PoisonChunkEmbedder(poison);
        await Assert.ThrowsAsync<EmbeddingChunkException>(
            () => new IndexBuilder(second).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "second.cidx"),
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: checkpointPath,
                expectedEmbeddingDimension: 2));

        Assert.Empty(
            first.CompletedInputs.Intersect(second.RequestedInputs, StringComparer.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InterruptOnSecondBatchEmbedder : IEmbeddingClient
    {
        private int _calls;

        public string Model => "test-model";

        public List<string> CompletedInputs { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 2)
            {
                throw new OperationCanceledException("Simulated interruption.");
            }

            CompletedInputs.AddRange(inputs);
            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }

    private sealed class PoisonChunkEmbedder(string poison) : IEmbeddingClient
    {
        public string Model => "test-model";

        public List<string> RequestedInputs { get; } = [];

        public List<string> CompletedInputs { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            RequestedInputs.AddRange(inputs);

            // Neither OperationCanceledException nor EmbeddingUnavailableException, so the
            // builder halves the batch rather than giving up on it — which is the path under
            // test. Deterministic, so halving never clears it: it fails again on every retry,
            // all the way down to the single chunk that carries the marker.
            if (inputs.Any(input => input.Contains(poison, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Simulated deterministic chunk failure.");
            }

            CompletedInputs.AddRange(inputs);
            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }

    private sealed class RecordingEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public List<string> Inputs { get; } = [];

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(inputs);
            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
