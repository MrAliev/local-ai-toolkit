namespace LocalAi.Broker.Client;

/// <summary>
/// Whether Ollama answers where the running broker reaches for it, asked after a job failed.
///
/// The broker's own verdict rides its heartbeat, up to a second after the failure, so a client
/// reading it at once still sees the one before. The address does not change; the listener there
/// is asked instead.
/// </summary>
public static class BrokerBackendProbe
{
    /// <summary>
    /// The endpoint, when nothing answers there; null when something does, or when no broker has
    /// said where it reaches.
    /// </summary>
    public static string? SilentEndpoint(string runtimeRoot) =>
        SilentEndpoint(runtimeRoot, BackendEndpoint.AnythingListening);

    internal static string? SilentEndpoint(string runtimeRoot, Func<Uri, bool> isListening) =>
        BrokerProcess.ReadState(runtimeRoot)?.Backend?.Endpoint is { } endpoint &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        !isListening(uri)
            ? endpoint
            : null;
}
