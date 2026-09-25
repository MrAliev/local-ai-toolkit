using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A build against an Ollama without the embedding model halved every batch down to single chunks,
/// one failing job per half, and ended by blaming a chunk of a file that was never the problem
/// (#633). The model's absence is one fact about the machine, said once.
/// </summary>
public sealed class AMissingEmbeddingModelStopsTheBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-missing-model-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_build_stops_at_the_first_batch_without_halving_or_blaming_a_file()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < 8; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index}.md"),
                $"unique-{index}\n" + new string((char)('a' + index), 400));
        }

        var embedder = new ModelMissingEmbedder();

        var error = await Assert.ThrowsAsync<EmbeddingModelMissingException>(
            () => new IndexBuilder(
                embedder,
                backendWait: (_, _) => Task.CompletedTask).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "index.cidx"),
                ct: TestContext.Current.CancellationToken,
                expectedEmbeddingDimension: 2));

        Assert.Equal("test-model", error.Model);
        Assert.Equal(1, embedder.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ModelMissingEmbedder : IEmbeddingClient
    {
        private int _calls;

        public string Model => "test-model";

        public int Calls => Volatile.Read(ref _calls);

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new EmbeddingModelMissingException(
                Model,
                "The embedding model 'test-model' is not installed.",
                new InvalidOperationException("Embedding model 'test-model' is not installed in Ollama."));
        }
    }
}
