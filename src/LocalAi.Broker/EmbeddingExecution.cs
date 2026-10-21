using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>Lets a query cache decide whether inference is needed before the same job preflights.</summary>
public interface IEmbeddingExecutionTransport
{
    Task<BrokerExecutionResult> ExecuteEmbeddingAsync(LocalJobRequest request,
        Func<EmbedJobPayload, CancellationToken, Task<ModelResidencyProof>> ensureReady,
        CancellationToken cancellationToken);
}

internal static class EmbeddingExecution
{
    // CodeSearch already sends this tier explicitly. Legacy requests must apply the same
    // concrete tier to both preflight and inference, never prove a guessed backend default.
    internal const int DefaultContextTokens = 16_384;

    internal static LocalRoutingReceipt Routing(ModelResidencyProof proof)
    {
        var (shortfall, percent) = proof.Shortfall();
        return new(null, proof.Model, proof.ContextTokens, false, false,
            "embedding:validated", 0, 0, 0, ResidencyShortfall: shortfall, VramResidentPercent: percent);
    }
}
