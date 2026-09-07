using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A full build of this repository died twice in one day, at 6551 and at about 7280 of 7882
/// chunks, both times because Ollama's own runner had gone and no llama-server process was left.
/// The rate had not sagged — 12.4 to 12.9 chunks a second from the start to the moment it stopped
/// — and a third run started by hand minutes later finished. The difference between the second run
/// and the third was time and nothing else (#412).
///
/// The checkpoint makes a restart cheap, but the restart is a person noticing and typing a
/// command. On the post-commit hook or an installer's first index there is nobody to notice, and
/// the index is simply not there.
///
/// No test here waits out a real duration: the wait is a delegate, and these pass one that returns
/// at once and records what it was asked for. What is under test is what the build asks for and
/// what it says while asking, never how long the machine took to do it.
/// </summary>
public sealed class ABuildWaitsForABackendThatDiedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-backend-returns-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_backend_that_comes_back_lets_the_build_finish()
    {
        var sourceRoot = SourceTree();
        // Away for two refusals: the first ends the ordinary attempt, the second the free
        // immediate retry taken once the waiting gate is held. So exactly one wait happens, and
        // the run reaches the line that says the backend answered.
        var embedder = new BackendAwayFor(2);
        var lines = new List<string>();
        var indexPath = Path.Combine(_root, "index.cidx");

        await new IndexBuilder(embedder, lines.Add, backendWait: NoWait).BuildAsync(
            sourceRoot,
            indexPath,
            ct: TestContext.Current.CancellationToken,
            embeddingCheckpointPath: Path.Combine(_root, "checkpoint"),
            expectedEmbeddingDimension: 2);

        Assert.True(File.Exists(indexPath));
        // Proved rather than assumed: without this the test would pass on a build that never met
        // the failure it exists to survive.
        Assert.Equal(2, embedder.Refusals);
        Assert.Contains(
            lines,
            line => line.Contains("The build is waiting", StringComparison.Ordinal));
        // Not "Ollama answered": what is checked here is that this call returned vectors, and on
        // the outage this test is built from Ollama had been answering all along.
        Assert.Contains(
            lines,
            line => line.Contains("Embeddings came back", StringComparison.Ordinal));
    }

    /// <summary>
    /// What refused, in the words of whatever refused, once per outage.
    ///
    /// The countdown line used to open with "Ollama is not available", copied from
    /// EmbeddingBackendUnavailable on the ground that it was the same state. It is not one state:
    /// the broker failing to start, an endpoint answering nothing, and five distinct codes from
    /// Ollama itself all arrive here as one exception. On the owner's machine one build met
    /// runner_unreachable twenty-five times in twenty-eight minutes and an unreachable endpoint
    /// not once — Ollama was up and answering throughout, in the same seven to sixteen seconds a
    /// success takes, with an HTTP 400 whose body named a runner it could not reach.
    ///
    /// The sentence that says which one it was is on the exception already. The catch did not
    /// bind it, so it was composed, bounded, translated and then dropped.
    ///
    /// Once, not per attempt: three refusals here mean two waits, and a cause repeated above
    /// every line of a countdown is what the countdown was kept clean of.
    /// </summary>
    [Fact]
    public async Task The_wait_carries_the_refusal_s_own_words_once()
    {
        var sourceRoot = SourceTree();
        var lines = new List<string>();

        await new IndexBuilder(new BackendAwayFor(3), lines.Add, backendWait: NoWait).BuildAsync(
            sourceRoot,
            Path.Combine(_root, "index.cidx"),
            ct: TestContext.Current.CancellationToken,
            embeddingCheckpointPath: Path.Combine(_root, "checkpoint"),
            expectedEmbeddingDimension: 2);

        Assert.Equal(
            1,
            lines.Count(line => line.Contains(
                "The model backend was not available for embedding.",
                StringComparison.Ordinal)));
        // Two waits happened, so the countdown really did repeat under the one cause line.
        Assert.Equal(
            2,
            lines.Count(line => line.Contains("The build is waiting", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A build that is waiting still has to look alive. `Sync:` prints how long ago the record was
    /// written precisely so that a build whose process died reads as stale, so a pause that writes
    /// nothing for ten minutes is indistinguishable from a machine that has hung — the very thing
    /// this change exists to stop being invisible.
    /// </summary>
    [Fact]
    public async Task A_waiting_build_keeps_saying_it_is_alive()
    {
        var sourceRoot = SourceTree();
        var reports = new List<IndexBuildProgress>();

        await new IndexBuilder(
            new BackendAwayFor(2),
            progress: reports.Add,
            backendWait: NoWait).BuildAsync(
            sourceRoot,
            Path.Combine(_root, "index.cidx"),
            ct: TestContext.Current.CancellationToken,
            embeddingCheckpointPath: Path.Combine(_root, "checkpoint"),
            expectedEmbeddingDimension: 2);

        // Zero rate and no estimate: the shape a checkpoint restore already reports, so the
        // reader is told "calculating" rather than an estimate nobody is earning.
        Assert.Contains(
            reports,
            report => report.ChunksPerSecond == 0 && report.EstimatedRemaining is null);
    }

    /// <summary>
    /// And the patience is finite and is the budget it claims. A backend that never returns still
    /// ends in the refusal the reader can act on, not in a build that waits for ever.
    /// </summary>
    [Fact]
    public async Task A_backend_that_never_returns_is_waited_for_exactly_the_budget()
    {
        var sourceRoot = SourceTree();
        var asked = new List<TimeSpan>();

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => new IndexBuilder(
                new BackendAwayFor(int.MaxValue),
                backendWait: (interval, _) =>
                {
                    asked.Add(interval);
                    return Task.CompletedTask;
                }).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "index.cidx"),
                ct: TestContext.Current.CancellationToken,
                embeddingCheckpointPath: Path.Combine(_root, "checkpoint"),
                expectedEmbeddingDimension: 2));

        // The policy, asserted as the sum of what was asked for rather than as elapsed time: a
        // duration measured here would be a claim about the machine.
        Assert.Equal(
            IndexBuilder.BackendWaitBudget,
            asked.Aggregate(TimeSpan.Zero, (total, interval) => total + interval));
        // Short at first: a runner already coming back answers in seconds, and a first wait of
        // half a minute would spend most of an outage that was over.
        Assert.Equal(TimeSpan.FromSeconds(5), asked[0]);
    }

    private static Task NoWait(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>One batch is enough; the failure is not about how much work there is.</summary>
    private string SourceTree()
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(
            Path.Combine(sourceRoot, "notes.md"),
            "unique-note\n" + new string('a', 2_000));
        return sourceRoot;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Refuses the first <paramref name="refusals"/> calls the way an Ollama whose runner has gone
    /// does, then answers.
    /// </summary>
    private sealed class BackendAwayFor(int refusals) : IEmbeddingClient
    {
        public string Model => "test-model";

        public int Refusals { get; private set; }

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            if (Refusals < refusals)
            {
                Refusals++;
                throw new EmbeddingUnavailableException(
                    "The model backend was not available for embedding.",
                    new InvalidOperationException("runner_unreachable"));
            }

            return Task.FromResult(
                inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
