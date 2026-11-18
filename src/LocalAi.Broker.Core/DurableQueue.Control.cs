using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed partial class DurableQueue
{
    /// <summary>Only the creator receives control authority; deduplication never transfers it.</summary>
    public async Task<BrokerJobHandle> EnqueueOwnedAsync(
        LocalJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var queued = await EnqueueCore(request, HashControlToken(token), cancellationToken);
        return new BrokerJobHandle(queued.JobId, queued.JoinedExisting,
            queued.JoinedExisting ? null : token);
    }

    /// <summary>Requests cancellation of exactly one owned job, independently of any wait.</summary>
    public Task<JobCancellationResult> RequestCancellationAsync(
        BrokerJobHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = EnterMutex(cancellationToken);
        var directory = new[] { GetJobDirectory(handle.JobId), GetArchiveDirectory(handle.JobId) }
            .FirstOrDefault(Directory.Exists);
        if (directory is null)
            return Task.FromResult(JobCancellationResult.NotFound);

        var entry = new JobEntry(directory, ReadState(directory));
        var control = ReadControl(entry);
        if (control is null || handle.ControlToken is not { Length: 64 } token ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(control.ControlTokenHash),
                Encoding.ASCII.GetBytes(HashControlToken(token))))
            return Task.FromResult(JobCancellationResult.NotOwner);

        if (entry.State.State is not (LocalJobState.Queued or LocalJobState.Running))
            return Task.FromResult(JobCancellationResult.AlreadyTerminal);

        var now = _timeProvider.GetUtcNow();
        AtomicWriteJson(GetControlPath(directory), control with
        {
            CancellationRequestedAtUtc = control.CancellationRequestedAtUtc ?? now
        });
        if (entry.State.State == LocalJobState.Queued)
        {
            WriteState(directory, ToTerminal(entry.State, LocalJobState.Cancelled, now, null));
            Archive(directory, handle.JobId, cancellationToken);
        }
        return Task.FromResult(JobCancellationResult.Requested);
    }

    // A sidecar preserves the strict legacy request/state schemas. Old binaries ignore it.
    private static string GetControlPath(string directory) => Path.Combine(directory, "control.json");

    private static string HashControlToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private JobControlDocument? ReadControl(JobEntry entry)
    {
        try
        {
            var path = Path.GetFullPath(GetControlPath(entry.Directory));
            var root = Path.GetFullPath(RuntimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Job entries include directory names read from storage. Keep the authority
            // sidecar inside the configured runtime even when that input is malformed.
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return null;
            if (!File.Exists(path)) return null;
            var control = JsonSerializer.Deserialize<JobControlDocument>(File.ReadAllText(path), JsonOptions);
            return control is { SchemaVersion: 1, ControlTokenHash.Length: 64 } &&
                control.JobId == entry.State.JobId ? control : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record JobControlDocument(
        int SchemaVersion,
        Guid JobId,
        string ControlTokenHash,
        DateTimeOffset? CancellationRequestedAtUtc);
}

/// <summary>Keep the control token private when persisting a handle for a later process.</summary>
public sealed record BrokerJobHandle(Guid JobId, bool JoinedExisting, string? ControlToken)
{
    public override string ToString() => $"Job {JobId} (joined: {JoinedExisting})";
}

public enum JobCancellationResult
{
    Requested,
    AlreadyTerminal,
    NotOwner,
    NotFound,
    Unsupported
}
