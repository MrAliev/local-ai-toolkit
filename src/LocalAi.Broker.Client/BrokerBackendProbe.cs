namespace LocalAi.Broker.Client;

/// <summary>
/// Whether Ollama answers where the running broker reaches for it, asked after a job failed.
///
/// The broker's own verdict is on its heartbeat, a second late: read straight after a failure it
/// still says what it said before. The address it publishes does not change, so the listener is
/// asked now instead.
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
