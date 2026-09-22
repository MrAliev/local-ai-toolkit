using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>One explicitly requested, exclusively owned OCR session with atomic page commits.</summary>
internal sealed class PdfOcrCheckpoint(string directory, FileStream lease) : IAsyncDisposable
{
    private const long MaximumDocumentBytes = 4L * 1024 * 1024;
    private readonly Dictionary<int, Page> completed = [];
    public IReadOnlySet<int> CompletedPages => completed.Keys.ToHashSet();
    public int Count => completed.Count;
    public Page? Find(int page) => completed.GetValueOrDefault(page);

    public void ValidateOutput(string? output)
    {
        if (output is null) return;
        output = Path.GetFullPath(output);
        var relative = Path.GetRelativePath(directory, output);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new ArgumentException(LocalLmText.PdfCheckpointOutputConflict(directory));
        // Existing aliases can have a different spelling or parent path.
        foreach (var path in completed.Keys.Select(PagePath).Append(Path.Combine(directory, "session.json")))
            if (OutputFileIdentity.IsSource(path, output))
                throw new ArgumentException(LocalLmText.PdfCheckpointOutputConflict(directory));
    }

    public static async Task<PdfOcrCheckpoint> OpenAsync(string directory, Manifest expected, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new ArgumentException(LocalLmText.PdfCheckpointUnavailable(directory), error); }
        var session = new PdfOcrCheckpoint(directory, lease);
        try
        {
            var manifestPath = Path.Combine(directory, "session.json");
            if (File.Exists(manifestPath))
            {
                var actual = await ReadAsync<Manifest>(manifestPath, cancellationToken);
                if (JsonSerializer.Serialize(actual, LocalAiJson.Strict) != JsonSerializer.Serialize(expected, LocalAiJson.Strict))
                    throw new ArgumentException(LocalLmText.PdfCheckpointMismatch(directory));
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != "session.lock"))
                    throw new ArgumentException(LocalLmText.PdfCheckpointMismatch(directory));
                await WriteAsync(manifestPath, expected, cancellationToken);
            }

            foreach (var number in expected.Pages)
            {
                var path = session.PagePath(number);
                if (!File.Exists(path)) continue;
                var page = await ReadAsync<Page>(path, cancellationToken);
                if (page.Number != number || page.Text is null || page.Receipt is null || Hash(page.Text) != page.TextHash)
                    throw new ArgumentException(LocalLmText.PdfCheckpointMismatch(directory));
                session.completed.Add(number, page);
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task SaveAsync(int number, LocalJobResult<string> result)
    {
        var page = new Page(number, result.Value, Hash(result.Value), result.Receipt);
        // Once inference returns a complete page, finish its small durable commit even if
        // cancellation has just arrived. The next page still observes the caller's token.
        await WriteAsync(PagePath(number), page, CancellationToken.None);
        completed.Add(number, page);
    }

    private string PagePath(int number) => Path.Combine(directory, "page-" + number.ToString(CultureInfo.InvariantCulture) + ".json");
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumDocumentBytes) throw new JsonException("Checkpoint exceeds its size limit.");
            return await JsonSerializer.DeserializeAsync<T>(input, LocalAiJson.Strict, cancellationToken)
                ?? throw new JsonException("Empty checkpoint.");
        }
        catch (JsonException error) { throw new ArgumentException(LocalLmText.PdfCheckpointMismatch(Path.GetDirectoryName(path)!), error); }
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, async (stream, token) =>
        {
            await JsonSerializer.SerializeAsync(stream, value, LocalAiJson.Strict, token);
            if (stream.Length > MaximumDocumentBytes) throw new IOException("Checkpoint exceeds its size limit.");
        }, AtomicWrite.Irrecoverable, cancellationToken);

    public ValueTask DisposeAsync() => lease.DisposeAsync();

    internal sealed record Manifest(int SchemaVersion, string PdfHash, string RouteVersion, string ProcessorVersion,
        string PromptHash, string? ModelOverride, string Language, int TotalPages, int[] Pages);
    internal sealed record Page(int Number, string Text, string TextHash, LocalUsageReceipt Receipt);
}
