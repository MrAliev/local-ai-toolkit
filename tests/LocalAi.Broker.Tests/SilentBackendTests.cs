using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A failed job is blamed on Ollama only when nothing answers where the running broker reaches for
/// it (#612). The address is the broker's, from its published state; the listener is asked, so no
/// test here reaches for a real Ollama.
/// </summary>
public sealed class SilentBackendTests : IDisposable
{
    private const string Endpoint = "http://127.0.0.1:11434/";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-silent-backend-" + Guid.NewGuid().ToString("N"));

    public SilentBackendTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Names_the_brokers_endpoint_when_nothing_answers_there()
    {
        Publish(new BrokerBackendState(true, Endpoint));
        Uri? asked = null;

        var silent = BrokerBackendProbe.SilentEndpoint(_root, uri =>
        {
            asked = uri;
            return false;
        });

        Assert.Equal(Endpoint, silent);
        Assert.Equal(new Uri(Endpoint), asked);
    }

    [Fact]
    public void Says_nothing_when_something_answers_there()
    {
        Publish(new BrokerBackendState(false, Endpoint));

        Assert.Null(BrokerBackendProbe.SilentEndpoint(_root, _ => true));
    }

    [Fact]
    public void Says_nothing_when_no_broker_has_said_where_it_reaches()
    {
        Publish(backend: null);

        Assert.Null(BrokerBackendProbe.SilentEndpoint(
            _root,
            _ => throw new InvalidOperationException("Nothing to ask.")));
    }

    [Fact]
    public void Says_nothing_without_a_broker_state_at_all()
    {
        Assert.Null(BrokerBackendProbe.SilentEndpoint(
            _root,
            _ => throw new InvalidOperationException("Nothing to ask.")));
    }

    private void Publish(BrokerBackendState? backend)
    {
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(
            BrokerProcess.StatePath(_root),
            JsonSerializer.Serialize(
                new BrokerProcessState(
                    42,
                    now,
                    now,
                    BrokerCompatibilityContract.HostStateSchemaVersion,
                    Path.GetFullPath("LocalAi.Broker.exe"),
                    BrokerCompatibilityContract.Current,
                    backend),
                LocalAiJson.Strict));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
