using System.Globalization;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// A full build of this repository — 7882 chunks — died at 6551, and the whole of what it said
/// was `The model backend was not available for embedding.` No cause, no figures, and not the one
/// fact that decides what the reader does next: that the finished work is checkpointed, so
/// running it again continues rather than starting over (#389).
///
/// The figures exist only in <c>EmbedAsync</c> — the layer that raises the refusal does not know
/// them — so this is about where the sentence is composed as much as what it says.
/// </summary>
public sealed class AnEmbeddingRefusalSaysWhatHappenedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-embedding-refusal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_refusal_says_how_far_the_embedding_got()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);

        // Two batches' worth: 40 files of about 2 000 characters against a 48 000-character
        // opening budget, so the backend goes away with one batch already embedded and the
        // refusal has something to report.
        for (var index = 0; index < 40; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"unique-{index:D2}\n" + new string((char)('a' + index % 26), 2_000));
        }

        var embedder = new BackendGoesAwayEmbedder();

        var error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            // A wait that returns at once. The build now waits out a backend that has gone
            // (#412), and this test is about the sentence it prints when the waiting is over —
            // with the shipped delay it would sit here for ten minutes to reach the same line.
            () => new IndexBuilder(
                embedder,
                backendWait: (_, _) => Task.CompletedTask).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "index.cidx"),
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: Path.Combine(_root, "checkpoint"),
                expectedEmbeddingDimension: 2));

        // Without this the assertion below could pass on a build that embedded nothing.
        Assert.NotEmpty(embedder.CompletedInputs);
        Assert.Contains(
            embedder.CompletedInputs.Count.ToString(CultureInfo.InvariantCulture),
            error.Message);
    }

    /// <summary>
    /// The other half of #393: this sentence used to be an English literal, so a reader on a
    /// Russian machine got a Russian refusal with an English cause — the exact thing the
    /// catalogue's own summary says it was written to end.
    /// </summary>
    [Theory]
    [InlineData("stopped")]
    [InlineData("at-start")]
    [InlineData("backend")]
    [InlineData("not-started")]
    public void Each_refusal_is_not_the_same_text_in_both_languages(string which)
    {
        var english = Refusal(which);

        using (TestCulture.Reading("ru"))
        {
            Assert.NotEqual(english, Refusal(which), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Numbers stay invariant in every language: these lines are quoted verbatim by agents and
    /// parsed by tests, so a Russian reader must not be given "6 551" with a group separator.
    /// </summary>
    [Fact]
    public void The_counts_are_invariant_in_both_languages()
    {
        using var reading = TestCulture.Reading("ru");

        var russian = IndexText.BuildEmbeddingStopped(6551, 7882);

        Assert.Contains("6551", russian, StringComparison.Ordinal);
        Assert.Contains("7882", russian, StringComparison.Ordinal);
    }

    private static string Refusal(string which) => which switch
    {
        "stopped" => IndexText.BuildEmbeddingStopped(6551, 7882),
        "at-start" => IndexText.BuildEmbeddingStoppedAtStart(7882),
        "backend" => IndexText.EmbeddingBackendUnavailable("embed-model", "detail"),
        "not-started" => IndexText.EmbeddingBrokerNotStarted("detail"),
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Answers once, then behaves like an Ollama that has gone away: the exception the builder
    /// deliberately refuses to halve on, so the build stops rather than dividing work it cannot
    /// make smaller (#369).
    /// </summary>
    private sealed class BackendGoesAwayEmbedder : IEmbeddingClient
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
            if (Interlocked.Increment(ref _calls) > 1)
            {
                throw new EmbeddingUnavailableException(
                    "The model backend was not available for embedding.",
                    new InvalidOperationException("Ollama is not answering."));
            }

            lock (CompletedInputs)
            {
                CompletedInputs.AddRange(inputs);
            }

            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
