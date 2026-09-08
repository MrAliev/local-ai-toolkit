using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Starts Ollama once, when a model call finds it down.
///
/// Four limits, all of them deliberate.
///
/// **Only when nothing is listening.** The trigger is an <c>HttpRequestException</c>, and that is
/// every HTTP status as well as every failure to connect — a 404 for a model the server does not
/// hold looks identical to a server that is gone. Starting on one of those put a second
/// <c>ollama serve</c> beside a healthy one, where it took the traffic and answered 404 to
/// everything, so the symptom fed itself. The endpoint is asked whether anything answers before
/// anything is launched, which is what the report already claimed had happened.
///
/// **Only the executable an installation recorded**, never one resolved now. A path found at the
/// moment of use is how a background process ends up running whatever answers to the name, and
/// Ollama installs into a directory the user can write to, so the ACL check that guards the
/// winget executable would reject an ordinary installation rather than protect one. No record
/// means nothing is started.
///
/// **Only a loopback endpoint.** A broker pointed at another machine cannot start what it does
/// not own, and pretending otherwise would replace a clear message with a confusing one.
///
/// **Only once per broker.** Somebody who closed Ollama on purpose should not have it reappear
/// on every retry, and a start that did not help will not help the second time either.
/// </summary>
public sealed class BackendStarter
{
    private readonly Uri _endpoint;
    private readonly OllamaLaunchRecordStore _records;
    private readonly Action<string> _report;
    private readonly Action<string> _start;

    /// <summary>
    /// Whether anything answers at the endpoint. A socket rather than a request on purpose: the
    /// question is "is a server there", and every HTTP answer — including the 404 that caused this
    /// to be written — already proves it is.
    /// </summary>
    private readonly Func<Uri, bool> _isListening;

    private int _attempted;
    private int _answeringReported;

    /// <param name="start">
    /// How the recorded executable is run. Injected so a test can see what would be launched
    /// without launching it; by default a detached <c>ollama serve</c>, because the broker wants
    /// the API rather than the tray application.
    /// </param>
    public BackendStarter(
        Uri endpoint,
        OllamaLaunchRecordStore records,
        Action<string> report,
        Action<string>? start = null,
        Func<Uri, bool>? isListening = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _start = start ?? StartDetached;
        _isListening = isListening ?? AnythingListening;
    }

    /// <summary>
    /// Whether a start has already been attempted, so a caller can say that the one attempt is
    /// spent rather than implying another is coming.
    /// </summary>
    public bool HasAttempted => Volatile.Read(ref _attempted) == 1;

    public void OnDiagnostic(BrokerHostDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!string.Equals(
                diagnostic.ExceptionType,
                nameof(HttpRequestException),
                StringComparison.Ordinal))
        {
            return;
        }

        if (Volatile.Read(ref _attempted) == 1)
        {
            return;
        }

        Start();
    }

    private void Start()
    {
        if (!_endpoint.IsLoopback)
        {
            Volatile.Write(ref _attempted, 1);
            _report(
                $"Ollama is not answering at {_endpoint}, which is not this machine, so it " +
                "cannot be started from here.");
            return;
        }

        // A start is for a backend that is not there, and until now nothing checked that it was
        // not. Every HTTP status the transport is handed arrives as HttpRequestException, and the
        // commonest of them is a 404 for a model this server does not hold — which comes from a
        // server answering normally. Reading that as "Ollama is down" started a second one beside
        // the first, and on Windows both stand: the new server bound 127.0.0.1:11434 next to the
        // original's [::]:11434, the more specific bind took every IPv4 request, and a build's
        // embeddings went to a server that had just started and held no models at all.
        //
        // The one attempt is deliberately not spent here. A server answering now says nothing
        // about an hour from now, and the outage this whole class exists for must still get its
        // start when it comes.
        if (_isListening(_endpoint))
        {
            if (Interlocked.Exchange(ref _answeringReported, 1) == 0)
            {
                _report(
                    $"Ollama is answering at {_endpoint}, so this failure is not that it is " +
                    "down, and nothing was started. A second server would bind the same port " +
                    "on a different address and take the traffic from the first.");
            }

            return;
        }

        if (_records.Read() is not { } record)
        {
            Volatile.Write(ref _attempted, 1);
            _report(
                "Ollama is not answering and no verified installation of it was recorded, so " +
                "it will not be started. Run the LocalAi installer once to record one.");
            return;
        }

        Volatile.Write(ref _attempted, 1);
        try
        {
            _start(record.ExecutablePath);
            _report(
                $"Ollama was not answering; started {record.ExecutablePath}. Queued work runs " +
                "as soon as it answers.");
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or
                ObjectDisposedException)
        {
            _report(
                $"Ollama could not be started from {record.ExecutablePath}: " +
                exception.Message);
        }
    }

    /// <summary>
    /// Opens a socket to the endpoint and closes it again.
    ///
    /// A connect rather than a request, because the question is only whether a server is there and
    /// every HTTP answer already settles that — including the 404 this check was written for. The
    /// deadline is short because on a loopback address both an answer and a refusal arrive at once;
    /// it exists so a port that swallows connections cannot hold up the diagnostic path.
    /// </summary>
    private static bool AnythingListening(Uri endpoint)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(endpoint.Host, endpoint.Port)
                       .Wait(TimeSpan.FromSeconds(1)) &&
                   client.Connected;
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException or ObjectDisposedException or
                AggregateException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void StartDetached(string executablePath)
    {
        using var started = Process.Start(new ProcessStartInfo(executablePath)
        {
            ArgumentList = { "serve" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (started is null)
        {
            throw new InvalidOperationException("The process did not start.");
        }
    }
}
