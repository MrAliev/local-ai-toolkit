using CodeSearch.Core.Embedding;
using LocalAi.Cli;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The code and exit status each named failure of a `localai` command gets. A script tells "try
/// again later" (75) from "this input will never work" (65) from "a service this needs is not
/// there" (69) by the status alone.
/// </summary>
public sealed class CliFailureTests
{
    /// <summary>
    /// A missing embedding model used to reach the guard as a chunk the model refused: exit 65,
    /// naming a file that was never the problem (#633).
    /// </summary>
    [Fact]
    public void A_missing_embedding_model_is_a_service_that_is_not_there()
    {
        var failure = new EmbeddingModelMissingException(
            "qwen3-embedding:8b-q8_0",
            "Search and indexing need the embedding model qwen3-embedding:8b-q8_0.",
            new InvalidOperationException());

        Assert.Equal(
            ("model_not_installed", failure.Message, 69),
            CliFailure.Classify(failure));
    }

    [Fact]
    public void The_failures_named_before_keep_their_codes()
    {
        var unavailable = new EmbeddingUnavailableException("away", new TimeoutException());
        var runner = new EmbeddingRunnerUnavailableException("runner", new TimeoutException());
        var chunk = new EmbeddingChunkException("chunk", new InvalidOperationException());
        var busy = new RepositorySyncBusyException("repository");

        Assert.Equal(("broker_unavailable", unavailable.Message, 75), CliFailure.Classify(unavailable));
        Assert.Equal(("broker_unavailable", runner.Message, 75), CliFailure.Classify(runner));
        Assert.Equal(("chunk_rejected", chunk.Message, 65), CliFailure.Classify(chunk));
        Assert.Equal(("sync_busy", busy.Message, 75), CliFailure.Classify(busy));
        Assert.Null(CliFailure.Classify(new InvalidOperationException("anything else")));
    }
}
