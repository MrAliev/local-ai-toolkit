using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class BrokerJobCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-job-command-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    [Fact]
    public void Deduplicated_join_does_not_erase_owner_authority()
    {
        var store = new BrokerJobHandleStore(root);
        var owner = new BrokerJobHandle(id, false, new string('A', 64));
        store.Save(owner);
        store.Save(new(id, true, null));
        Assert.Equal(owner, store.Read(id));
        Assert.DoesNotContain(owner.ControlToken!, owner.ToString());
    }

    [Fact]
    public async Task Knowing_job_ID_does_not_allow_cancellation()
    {
        var client = new Fake(id);
        Assert.Equal(77, await Run(client, "cancel"));
        Assert.Equal(0, client.Cancellations);
        Assert.Contains("job_not_owner", output.ToString());
    }

    [Fact]
    public async Task Owner_can_request_cancellation_without_printing_control_token()
    {
        var token = new string('A', 64);
        new BrokerJobHandleStore(root).Save(new(id, false, token));
        var client = new Fake(id);
        Assert.Equal(0, await Run(client, "cancel"));
        Assert.Equal(1, client.Cancellations);
        Assert.Equal(token, client.LastHandle!.ControlToken);
        Assert.DoesNotContain(token, output + error.ToString());
        Assert.Contains("Requested", output.ToString());
        Assert.Contains("terminalCancellationConfirmed", output.ToString());
    }

    [Fact]
    public async Task Unsupported_broker_is_reported_without_assuming_cancellation()
    {
        new BrokerJobHandleStore(root).Save(new(id, false, new string('A', 64)));
        Assert.Equal(69, await Run(new Fake(id) { CancellationResult = JobCancellationResult.Unsupported }, "cancel"));
        Assert.Contains("job_cancel_unsupported", output.ToString());
    }

    [Theory]
    [InlineData(false, 130, "job_wait_detached")]
    [InlineData(true, 75, "job_wait_timeout")]
    public async Task Stopping_wait_does_not_request_cancellation(bool timeout, int exit, string code)
    {
        var client = new Fake(id) { WaitFailure = timeout
            ? new BrokerJobWaitTimeoutException(id, TimeSpan.FromSeconds(1))
            : new BrokerJobWaitCancelledException(id, new CancellationToken(true)) };
        Assert.Equal(exit, await Run(client, "wait"));
        Assert.Equal(0, client.Cancellations);
        Assert.Contains(code, output.ToString());
        Assert.Contains(id.ToString(), output.ToString());
    }

    [Fact]
    public async Task Reattachment_wraps_cached_output_as_untrusted_and_stays_job_scoped()
    {
        Assert.Equal(0, await Run(new Fake(id), "wait"));
        using var envelope = JsonDocument.Parse(output.ToString());
        var data = envelope.RootElement.GetProperty("data");
        Assert.Equal("broker-job", data.GetProperty("scope").GetString());
        Assert.Contains("<untrusted-content", data.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Unknown_status_does_not_wait_or_cancel()
    {
        var client = new Fake(id) { Missing = true };
        Assert.Equal(66, await Run(client, "status"));
        Assert.Equal(0, client.Waits);
        Assert.Equal(0, client.Cancellations);
    }

    private Task<int> Run(Fake client, string operation) => BrokerJobCommand.ExecuteAsync(
        [operation, id.ToString()], new(output, error), true, (_, _) => client, new(root), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("wait", "not-a-guid")]
    [InlineData("wait", "00000000-0000-0000-0000-000000000000")]
    [InlineData("wait", "valid", "--timeout-seconds", "0")]
    [InlineData("wait", "valid", "--timeout-seconds", "86401")]
    [InlineData("wait", "valid", "--timeout-seconds", "NaN")]
    [InlineData("status", "valid", "--timeout-seconds", "1")]
    [InlineData("cancel", "valid", "--unexpected")]
    public async Task Invalid_arguments_are_rejected_before_creating_a_broker_client(params string[] arguments)
    {
        var actual = arguments.Select(value => value == "valid" ? id.ToString() : value).ToArray();
        var exit = await BrokerJobCommand.ExecuteAsync(actual, new(output, error), true,
            (_, _) => throw new InvalidOperationException("Client must not be created."), new(root), TestContext.Current.CancellationToken);
        Assert.Equal(2, exit);
        Assert.Contains("input_rejected", output.ToString());
    }

    [Fact]
    public void Submission_is_visible_before_the_first_progress_timer()
    {
        var progress = new LocalRunProgress(error, () => DateTimeOffset.UnixEpoch);
        progress.Report(new BrokerJobSubmitted(id, false));
        Assert.Contains(id.ToString(), error.ToString());
        Assert.Contains("localai job status", error.ToString());
        Assert.Contains("localai job wait", error.ToString());
    }

    [Fact]
    public void A_private_handle_for_another_job_is_rejected()
    {
        var store = new BrokerJobHandleStore(root);
        store.Save(new(id, false, new string('A', 64)));
        var file = Path.Combine(root, "client-handles", id.ToString("N") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new BrokerJobHandle(Guid.NewGuid(), false, new string('A', 64)), LocalAiJson.Strict));
        Assert.Throws<InvalidDataException>(() => store.Read(id));
    }

    [Fact]
    public async Task Corrupt_owner_record_is_reported_without_requesting_cancellation()
    {
        var store = new BrokerJobHandleStore(root);
        store.Save(new(id, false, new string('A', 64)));
        File.WriteAllText(Path.Combine(root, "client-handles", id.ToString("N") + ".json"),
            JsonSerializer.Serialize(new BrokerJobHandle(Guid.NewGuid(), false, new string('A', 64)), LocalAiJson.Strict));
        var client = new Fake(id);
        Assert.Equal(73, await Run(client, "cancel"));
        Assert.Equal(0, client.Cancellations);
        Assert.Contains("job_handle_unavailable", output.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Fake(Guid jobId) : IBrokerJobClient
    {
        public bool Missing { get; init; }
        public Exception? WaitFailure { get; init; }
        public JobCancellationResult CancellationResult { get; init; } = JobCancellationResult.Requested;
        public int Cancellations { get; private set; }
        public int Waits { get; private set; }
        public BrokerJobHandle? LastHandle { get; private set; }
        public Task<JobStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<JobStatus?>(Missing ? null : new(jobId, LocalJobState.Running, null, null, null, null));
        public Task<JobCancellationResult> RequestCancellationAsync(BrokerJobHandle handle, CancellationToken cancellationToken = default)
        {
            Cancellations++;
            LastHandle = handle;
            return Task.FromResult(CancellationResult);
        }
        public Task<LocalJobResult<T>> WaitAsync<T>(Guid id, CancellationToken cancellationToken = default)
        {
            Waits++;
            return WaitFailure is { } failure ? Task.FromException<LocalJobResult<T>>(failure)
                : Task.FromResult(new LocalJobResult<T>((T)(object)JsonSerializer.SerializeToElement(new { answer = "cached answer" }), null!));
        }
        public Task<BrokerJobHandle> SubmitAsync(LocalJobRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<T>> ExecuteAsync<T>(LocalJobRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
