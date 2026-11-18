using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>Private cancellation authority. Never pass its contents on a command line or print it.</summary>
public sealed class BrokerJobHandleStore(string runtimeRoot)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(runtimeRoot), "client-handles");

    public void Save(BrokerJobHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        // Joining a deduplicated job grants no authority and must not erase our earlier owner handle.
        if (handle.JoinedExisting || handle.ControlToken is null) return;
        Validate(handle);
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var destination = PathFor(handle.JobId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, handle, LocalAiJson.Strict);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { RejectLink(destination); }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public BrokerJobHandle? Read(Guid jobId)
    {
        var path = PathFor(jobId);
        if (!File.Exists(path)) return null;
        RejectLink(directory);
        RejectLink(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) throw new InvalidDataException("Invalid private job handle.");
        var handle = JsonSerializer.Deserialize<BrokerJobHandle>(stream, LocalAiJson.Strict)
            ?? throw new InvalidDataException("Invalid private job handle.");
        Validate(handle);
        if (handle.JobId != jobId || handle.JoinedExisting)
            throw new InvalidDataException("Invalid private job handle identity.");
        return handle;
    }

    private string PathFor(Guid jobId)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID cannot be empty.", nameof(jobId));
        return Path.Combine(directory, jobId.ToString("N") + ".json");
    }

    private static void Validate(BrokerJobHandle handle)
    {
        if (handle.JobId == Guid.Empty || handle.ControlToken is not { Length: 64 } token ||
            !token.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid private job handle.");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Private job handles cannot use symbolic links.");
    }
}
