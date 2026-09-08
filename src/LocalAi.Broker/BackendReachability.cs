using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Whether the backend answered, as last seen by the host loop.
///
/// Written from the diagnostic callback and read by the heartbeat, both on the broker's own
/// threads, so a volatile flag is the whole of it — this is a fact to publish, not a decision to
/// coordinate.
///
/// It lives here rather than inside the entry point because of what it publishes. The flag becomes
/// <see cref="BrokerBackendState"/> in the heartbeat, and a client whose wait runs out turns that
/// into the one sentence a person actually reads: "Ollama is not answering … start Ollama and try
/// again." A diagnosis that reaches a user through three components deserves a test, and a private
/// nested class in <c>Program</c> could not have one.
/// </summary>
public sealed class BackendReachability(Uri endpoint, Func<Uri, bool>? isListening = null)
{
    private readonly Func<Uri, bool> _isListening =
        isListening ?? BackendEndpoint.AnythingListening;

    private volatile bool _unreachable;

    public BrokerBackendState Current => new(!_unreachable, endpoint.ToString());

    /// <summary>
    /// An <c>HttpRequestException</c> used to be enough on its own, and it is not: the type covers
    /// every HTTP status as well as every failure to connect, and the commonest of them is a 404
    /// for a model this server does not hold — from a server answering normally. On that reading
    /// the broker told every waiting client that Ollama was down and to start it, while Ollama was
    /// running and the real fault was a missing model or a dead runner behind a live server.
    ///
    /// So the endpoint is asked. Nothing listening is the only thing that makes the sentence the
    /// client prints true.
    /// </summary>
    public void Observe(BrokerHostDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!string.Equals(
                diagnostic.ExceptionType,
                nameof(HttpRequestException),
                StringComparison.Ordinal))
        {
            return;
        }

        if (!_isListening(endpoint))
        {
            _unreachable = true;
        }
    }

    public void Answered() => _unreachable = false;
}
