using LocalAi.Contracts;
using System.Text;
using System.Text.Json;

namespace LocalAi.Broker;

public sealed record UsageReportReadResult(IReadOnlyList<UsageReport> Reports, int Unreadable);

/// <summary>Final surface-call reports, separate from model attempts and their legacy format.</summary>
public sealed class UsageReportStore(string runtimeRoot)
{
    public string ReportsDirectory { get; } = Path.Combine(Path.GetFullPath(runtimeRoot), "telemetry", "usage");
    public async Task WriteAsync(UsageReport report, CancellationToken cancellationToken = default)
    {
        var json = UsageReportJson.Write(report);
        Directory.CreateDirectory(ReportsDirectory);
        await TelemetryFile.WriteOnceAsync(Path.Combine(ReportsDirectory, $"{report.Identity.CallId:N}.json"), json, cancellationToken);
    }

    public async Task<UsageReport?> ReadAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (callId == Guid.Empty) throw new ArgumentException("A call ID is required.", nameof(callId));
        var path = Path.Combine(ReportsDirectory, $"{callId:N}.json");
        try { return UsageReportJson.Read(await TelemetryFile.ReadAsync(path, cancellationToken)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<UsageReportReadResult> ReadForReportAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ReportsDirectory)) return new([], 0);
        var reports = new List<UsageReport>();
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(ReportsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            try { reports.Add(UsageReportJson.Read(await TelemetryFile.ReadAsync(path, cancellationToken))); }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            { unreadable++; }
        }
        return new(reports.AsReadOnly(), unreadable);
    }
}

internal static class TelemetryFile
{
    internal static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        // Final records are immutable. Share rename/delete access with an atomic publisher,
        // but never share writes to the opened record. The handle keeps these bytes readable
        // if the directory entry moves while a duplicate publisher is comparing its result.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // A retry may repeat the same final observation. A different observation with the same ID
    // is a conflict, never a replacement or another result to add to the total.
    internal static async Task WriteOnceAsync(string destination, string json, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            await RequireSameAsync(destination, json, cancellationToken);
            return;
        }
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            { await RequireSameAsync(destination, json, cancellationToken); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task RequireSameAsync(string path, string json, CancellationToken cancellationToken)
    {
        using var existing = JsonDocument.Parse(await ReadAsync(path, cancellationToken));
        using var proposed = JsonDocument.Parse(json);
        if (!JsonElement.DeepEquals(existing.RootElement, proposed.RootElement))
            throw new InvalidDataException("A different final telemetry record already exists for this identity.");
    }
}
