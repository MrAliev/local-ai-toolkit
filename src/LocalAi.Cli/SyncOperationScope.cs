using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using LocalAi.Repository;

namespace LocalAi.Cli;

/// <summary>Only this sync's linked token is cancelled; the shared broker remains running.</summary>
internal sealed class SyncOperationScope : IDisposable
{
    private readonly RepositorySyncOperationStore _store;
    private readonly CancellationTokenSource _cancellation;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private RepositorySyncOperation _operation;
    private int _pass;
    private int _total;
    private int _completed;

    public SyncOperationScope(FsPath runtimeRoot, string repositoryId, string requestedRoot,
        Guid? operationId, CancellationToken cancellationToken)
    {
        _store = new RepositorySyncOperationStore(runtimeRoot);
        var id = operationId ?? Guid.NewGuid();
        if (id == Guid.Empty || _store.Read()?.OperationId == id)
            throw new ArgumentException(OutputCulture.Pick(
                "sync_operation_id_reused: use a new operation ID.",
                "sync_operation_id_reused: используйте новый идентификатор операции."), nameof(operationId));
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = new(id, repositoryId, requestedRoot, requestedRoot, 0, 0, 0,
            "Planning", "Running", DateTimeOffset.UtcNow);
        _store.Save(_operation);
        _poll = new Timer(_ =>
        {
            try
            {
                if (_store.CancellationRequested(id)) _cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public CancellationToken Token => _cancellation.Token;

    public void Pass(int pass, int total, int completed)
    {
        lock (_gate)
        {
            _pass = pass;
            _total = total;
            _completed = completed;
        }
    }

    public void Report(RepositoryIndexProgress progress)
    {
        lock (_gate)
        {
            _operation = _operation with
            {
                CurrentRoot = progress.WorkingRoot,
                Pass = _pass,
                TotalPasses = _total,
                CompletedPasses = progress.Phase == RepositoryIndexProgressPhase.Completed ? _total : _completed,
                Phase = progress.Phase.ToString(),
                State = progress.Phase switch
                {
                    RepositoryIndexProgressPhase.Completed => "Completed",
                    RepositoryIndexProgressPhase.Failed when Token.IsCancellationRequested => "Cancelled",
                    RepositoryIndexProgressPhase.Failed => "Failed",
                    _ => "Running",
                },
                UpdatedAtUtc = progress.UpdatedAtUtc,
            };
            _store.Save(_operation);
        }
    }

    public string Describe() { lock (_gate) return _operation.Describe(); }

    public void Refused()
    {
        lock (_gate)
        {
            _operation = _operation with { State = "Refused", UpdatedAtUtc = DateTimeOffset.UtcNow };
            _store.Save(_operation);
        }
    }

    public void Dispose()
    {
        _poll.Dispose();
        // Also cover an exception before the foreground try block gets its first report.
        if (_operation.State == "Running")
        {
            try
            {
                _store.Save(_operation with
                {
                    State = Token.IsCancellationRequested ? "Cancelled" : "Failed",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        _cancellation.Dispose();
        try { _store.ClearCancellation(_operation.OperationId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
