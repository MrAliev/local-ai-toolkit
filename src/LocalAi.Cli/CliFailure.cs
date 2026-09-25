using CodeSearch.Core.Embedding;

namespace LocalAi.Cli;

/// <summary>
/// The failures the command guard names, with the code and exit status each one gets: 75 for
/// "try again later", 69 for a service this needs that is not there, 65 for input that will never
/// work. Everything else is an unexpected failure, described by type.
/// </summary>
public static class CliFailure
{
    public static (string Code, string Message, int ExitCode)? Classify(Exception exception) =>
        exception switch
        {
            EmbeddingUnavailableException => ("broker_unavailable", exception.Message, 75),
            // The same "try again later" family as an unreachable embedder: the other run is doing
            // this run's work, and a hook or script must be able to tell that from a real fault.
            RepositorySyncBusyException => ("sync_busy", exception.Message, 75),
            // A model that is not installed does not install itself, and no chunk is to blame (#633).
            EmbeddingModelMissingException => ("model_not_installed", exception.Message, 69),
            EmbeddingChunkException => ("chunk_rejected", exception.Message, 65),
            _ => null,
        };
}
