using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// This flag is the only thing that reaches a person. The broker's own stderr goes nowhere — it is
/// started detached, with no console — so what a client prints when its wait runs out is built from
/// here: "Ollama is not answering … start Ollama and try again." A wrong verdict sends somebody to
/// restart a service that was never down, while the real fault waits where they are not looking.
/// </summary>
public sealed class BackendReachabilityTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434/");

    /// <summary>
    /// The case this class was corrected for. An <c>HttpRequestException</c> covers every HTTP
    /// status as well as every failure to connect, and the commonest of them is a 404 for a model
    /// the server does not hold — which arrives from a server answering normally.
    /// </summary>
    [Fact]
    public void A_failure_from_an_endpoint_that_is_listening_leaves_the_backend_reachable()
    {
        var reachability = new BackendReachability(Endpoint, _ => true);

        reachability.Observe(Diagnostic(nameof(HttpRequestException)));

        Assert.True(reachability.Current.Reachable);
    }

    [Fact]
    public void A_failure_with_nothing_listening_marks_the_backend_unreachable()
    {
        var reachability = new BackendReachability(Endpoint, _ => false);

        reachability.Observe(Diagnostic(nameof(HttpRequestException)));

        Assert.False(reachability.Current.Reachable);
        Assert.Equal(Endpoint.ToString(), reachability.Current.Endpoint);
    }

    /// <summary>
    /// Every other failure has its own cause, and none of them is a verdict about the backend.
    /// The endpoint is not even asked: a job that threw on its own data says nothing about whether
    /// a server is there, and a socket per failure would be a cost with no answer in it.
    /// </summary>
    [Theory]
    [InlineData("IOException")]
    [InlineData("InvalidDataException")]
    [InlineData("TaskCanceledException")]
    public void Another_kind_of_failure_is_not_a_verdict_about_the_backend(string exceptionType)
    {
        var asked = 0;
        var reachability = new BackendReachability(
            Endpoint,
            _ =>
            {
                asked++;
                return false;
            });

        reachability.Observe(Diagnostic(exceptionType));

        Assert.True(reachability.Current.Reachable);
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// A job that ran clears it, whatever was concluded before. The flag is the last thing seen,
    /// not a history.
    /// </summary>
    [Fact]
    public void A_job_that_answered_clears_an_earlier_verdict()
    {
        var reachability = new BackendReachability(Endpoint, _ => false);
        reachability.Observe(Diagnostic(nameof(HttpRequestException)));

        reachability.Answered();

        Assert.True(reachability.Current.Reachable);
    }

    private static BrokerHostDiagnostic Diagnostic(string exceptionType) =>
        new(Guid.Empty, "worker", Guid.Empty, "execute", exceptionType);
}
