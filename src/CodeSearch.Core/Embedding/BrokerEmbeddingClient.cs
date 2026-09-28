using CodeSearch.Core.Chunking;
using CodeSearch.Core.Resources;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace CodeSearch.Core.Embedding;

/// <param name="runtimeRoot">
/// The installation whose catalog says how a missing model is installed; the current user's when
/// null, which is the one <see cref="BrokerClientFactory.CreateDefault"/> serves.
/// </param>
public sealed class BrokerEmbeddingClient(
    string model,
    IBrokerClient broker,
    string? runtimeRoot = null) : IEmbeddingClient
{
    public string Model { get; } =
        !string.IsNullOrWhiteSpace(model)
            ? model
            : throw new ArgumentException("Model cannot be blank.", nameof(model));

    /// <summary>
    /// The failure code a job carries when the backend, not the request, was the problem. It is
    /// the name of the exception the broker recorded, which is the contract
    /// <see cref="BrokerJobFailedException.FailureCode"/> states.
    /// </summary>
    private const string BackendUnavailable = "BackendUnavailableException";
    private const string BackendRunnerUnavailable = "BackendRunnerUnavailableException";
    private const string ModelNotInstalled = "EmbeddingModelNotInstalledException";

    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> inputs,
        LocalJobPriority priority,
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        var request = LocalJobRequestFactory.CreateEmbed(
            deduplicationKey,
            priority,
            Model,
            inputs,
            requestedContextTokens: ChunkLimits.EmbeddingContextTokens);
        LocalJobResult<EmbedJobOutput> result;
        try
        {
            result = await broker.ExecuteAsync<EmbedJobOutput>(
                request,
                cancellationToken);
        }
        catch (BrokerBootstrapException exception) when (exception.Code == "broker_start_timeout")
        {
            // The bootstrap diagnostic - phase, last observation - is carried rather than
            // dropped: it is the only thing that says which step of starting the broker did not
            // finish. It arrives in English because BrokerProcess has no catalogue of its own,
            // which is a reason to catalogue those messages, not to drop this sentence.
            throw new EmbeddingUnavailableException(
                IndexText.EmbeddingBrokerNotStarted(exception.Message),
                exception);
        }
        // Nothing in src/ reaches this: BrokerClient turns its own deadline into an
        // OperationCanceledException or a BrokerBackendUnreachableException, and there is no
        // HttpClient or WaitAsync in that chain. It stays because deleting the catch would let
        // the indexer halve a batch against an absent backend, and it stays an English literal
        // because a string translated for a reader who cannot exist is cost with a parity test
        // guarding it - the argument this catalogue already makes for two other refusals. The
        // wording differs from the one above so that, if it ever does fire, the line says which
        // branch produced it.
        catch (TimeoutException exception)
        {
            throw new EmbeddingUnavailableException(
                "The Local AI broker did not answer in time.",
                exception);
        }
        // A backend that is not there is not a chunk that cannot be embedded. The two stable
        // failure codes below preserve whether Ollama itself was unreachable or whether Ollama
        // answered but could not reach its runner. IndexBuilder uses only the second signal for
        // one item-count step back; both signals use the patient backend-recovery path after that.
        //
        // Matched on the failure code, which BrokerJobFailedException documents as ours and
        // stable, and never on the reason: the reason is the backend's own words, quoted rather
        // than parsed, and learning Ollama's vocabulary up here is what the transport's own
        // comment argues against.
        catch (BrokerJobFailedException exception)
            when (exception.FailureCode.Equals(
                BackendRunnerUnavailable,
                StringComparison.Ordinal))
        {
            throw new EmbeddingRunnerUnavailableException(
                IndexText.EmbeddingBackendUnavailable(Model, exception.Message),
                exception);
        }
        catch (BrokerJobFailedException exception)
            when (exception.FailureCode.Equals(BackendUnavailable, StringComparison.Ordinal))
        {
            throw new EmbeddingUnavailableException(
                IndexText.EmbeddingBackendUnavailable(Model, exception.Message),
                exception);
        }
        catch (BrokerJobFailedException exception)
            when (exception.FailureCode.Equals(ModelNotInstalled, StringComparison.Ordinal))
        {
            throw new EmbeddingModelMissingException(
                Model,
                EmbeddingModelAdvice.For(Model, runtimeRoot),
                exception);
        }
        // The same rule, by the route nobody guarded. An Ollama that is not running does not fail
        // a job - the job never runs at all, and the wait ends here. This is an
        // InvalidOperationException, so it was neither caught nor excluded from halving: the
        // indexer divided a batch against an absent backend down to a single chunk, then reported
        // that chunk as rejected with exit 65, naming a file that was never the problem.
        //
        // Its own message already names the cause and what to do, so it travels whole rather than
        // being replaced by a sentence that knows less.
        catch (BrokerBackendUnreachableException exception)
        {
            throw new EmbeddingUnavailableException(exception.Message, exception);
        }

        if (result.Value.Embeddings.Count != inputs.Count)
        {
            throw new InvalidOperationException(
                $"Broker returned {result.Value.Embeddings.Count} embeddings " +
                $"for {inputs.Count} inputs.");
        }

        return result.Value.Embeddings
            .Select(vector =>
            {
                // The transport rejects non-finite doubles, but a finite double past
                // float.MaxValue casts to Infinity, the sum of squares follows, the scale
                // becomes zero and the product NaN — durably persisted into the index and
                // poisoning every ranking comparison it touches (#205). Checked here, at the
                // conversion, so the invariant does not hang on one transport implementation.
                var values = new float[vector.Count];
                for (var index = 0; index < values.Length; index++)
                {
                    var value = (float)vector[index];
                    if (!float.IsFinite(value))
                    {
                        throw new InvalidDataException(
                            $"The embedding backend returned {vector[index]} at position " +
                            $"{index}, which is not representable as a finite float.");
                    }

                    values[index] = value;
                }

                EmbeddingVector.Normalize(values);
                return values;
            })
            .ToArray();
    }
}

public class EmbeddingUnavailableException(
    string message,
    Exception innerException) : Exception(message, innerException);

/// <summary>
/// Ollama answered but its own runner connection was refused. This type is the stable signal for
/// one batch-size step back; no caller parses the backend's quoted reason.
/// </summary>
public sealed class EmbeddingRunnerUnavailableException(
    string message,
    Exception innerException) : EmbeddingUnavailableException(message, innerException);

/// <summary>
/// A single chunk the embedding model refused, already isolated from its batch. Carries the file
/// and chunk so the operator can act on it; the broker's own job id names nothing a human owns.
/// </summary>
public sealed class EmbeddingChunkException(
    string message,
    Exception innerException) : Exception(message, innerException);

/// <summary>
/// The embedding model is not installed. Not unavailable — waiting will not bring it — and not a
/// chunk the model refused, so nothing is halved and no file is blamed (#633).
/// </summary>
public sealed class EmbeddingModelMissingException(
    string model,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    public string Model { get; } = model;
}

public static class EmbeddingVector
{
    /// <summary>
    /// Normalization refuses what it cannot normalize (#205): a zero vector used to pass
    /// through silently and a non-finite sum produced NaNs that survived into the durable
    /// index. Both are backend anomalies worth one loud failure, not a poisoned ranking.
    /// </summary>
    public static void Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        if (!double.IsFinite(sum) || sum <= 0)
        {
            throw new InvalidDataException(
                sum <= 0
                    ? "The embedding backend returned a zero vector, which cannot be normalized."
                    : "The embedding vector's magnitude is not finite.");
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
            if (!float.IsFinite(vector[index]))
            {
                throw new InvalidDataException(
                    "Normalizing the embedding produced a non-finite value.");
            }
        }
    }
}
