using System.Net.Sockets;

namespace LocalAi.Broker;

/// <summary>
/// Whether anything answers where the backend is supposed to be.
///
/// Two things need this and they used to guess it the same wrong way. An
/// <c>HttpRequestException</c> is every HTTP status as well as every failure to connect, so a 404
/// for a model the server does not hold is indistinguishable, by type, from a server that is gone.
/// One of them started a second Ollama on that reading; the other published "the backend is
/// unreachable" to every waiting client on it.
///
/// A socket rather than a request, because the question is only whether a server is there, and any
/// HTTP answer at all already settles it.
/// </summary>
internal static class BackendEndpoint
{
    /// <summary>
    /// How long to wait for the connection. On a loopback address an accept and a refusal both
    /// arrive at once; the deadline exists so an address that swallows connections cannot hold up
    /// the diagnostic path this is called from.
    /// </summary>
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(1);

    public static bool AnythingListening(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(endpoint.Host, endpoint.Port).Wait(ConnectBudget) &&
                   client.Connected;
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException or ObjectDisposedException or
                AggregateException or InvalidOperationException)
        {
            // Nothing accepted the connection, which is the answer rather than a failure to get
            // one. A host that does not resolve reaches here too, and it is not a backend that is
            // there either.
            return false;
        }
    }
}
