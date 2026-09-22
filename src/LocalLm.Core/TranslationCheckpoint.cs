using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>Durable, validated translation fragments under one exclusive session lease.</summary>
internal sealed class TranslationCheckpoint(string directory, FileStream lease) : IAsyncDisposable
{
    private const long MaximumRecordBytes = 4L * 1024 * 1024;
    private readonly Dictionary<int, Segment> completed = [];
    public Segment? Find(int index) => completed.GetValueOrDefault(index);

    public static async Task<TranslationCheckpoint> OpenAsync(string directory, Manifest expected,
        string[] sources, bool markdown, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new ArgumentException(LocalLmText.TranslationCheckpointUnavailable(directory), error); }
        var checkpoint = new TranslationCheckpoint(directory, lease);
        try
        {
            var path = Path.Combine(directory, "session.json");
            if (File.Exists(path))
            {
                var actual = await ReadAsync<Manifest>(path, cancellationToken);
                if (actual != expected) throw Mismatch(directory);
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(item => Path.GetFileName(item) != "session.lock"))
                    throw Mismatch(directory);
                await WriteAsync(path, expected, cancellationToken);
            }
            for (var index = 0; index < sources.Length; index++)
            {
                path = checkpoint.SegmentPath(index);
                if (!File.Exists(path)) continue;
                var segment = await ReadAsync<Segment>(path, cancellationToken);
                if (segment.Index != index || segment.Text is null || segment.Receipt is null ||
                    segment.SourceHash != Hash(sources[index]) || segment.TextHash != Hash(segment.Text) ||
                    !Validate(sources[index], segment.Text, markdown).Passed)
                    throw Mismatch(directory);
                checkpoint.completed.Add(index, segment);
            }
            return checkpoint;
        }
        catch
        {
            await checkpoint.DisposeAsync();
            throw;
        }
    }

    public static TranslationValidationResult Validate(string source, string text, bool markdown) => markdown
        ? TranslationValidator.ValidateMarkdown(source, text) : TranslationValidator.ValidatePlain(source, text);

    public async Task SaveAsync(int index, string source, string text, LocalUsageReceipt receipt)
    {
        var segment = new Segment(index, Hash(source), text, Hash(text), receipt);
        // Once a validated fragment has returned, persist it even when cancellation arrives
        // at that boundary. The next fragment still observes the caller's token.
        await WriteAsync(SegmentPath(index), segment, CancellationToken.None);
        completed.Add(index, segment);
    }

    private string SegmentPath(int index) => Path.Combine(directory, "segment-" + index.ToString(CultureInfo.InvariantCulture) + ".json");
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static ArgumentException Mismatch(string directory) => new(LocalLmText.TranslationCheckpointMismatch(directory));

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumRecordBytes) throw new JsonException("Checkpoint exceeds its size limit.");
            return await JsonSerializer.DeserializeAsync<T>(stream, LocalAiJson.Strict, cancellationToken)
                ?? throw new JsonException("Empty checkpoint.");
        }
        catch (JsonException error) { throw new ArgumentException(LocalLmText.TranslationCheckpointMismatch(Path.GetDirectoryName(path)!), error); }
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, async (stream, token) =>
        {
            await JsonSerializer.SerializeAsync(stream, value, LocalAiJson.Strict, token);
            if (stream.Length > MaximumRecordBytes) throw new IOException("Checkpoint exceeds its size limit.");
        }, AtomicWrite.Irrecoverable, cancellationToken);

    public ValueTask DisposeAsync() => lease.DisposeAsync();
    internal sealed record Manifest(int SchemaVersion, string SourceHash, string SourceLanguage, string TargetLanguage,
        bool Markdown, string RouteVersion, string ProcessorVersion, string Language, int SegmentCount);
    internal sealed record Segment(int Index, string SourceHash, string Text, string TextHash, LocalUsageReceipt Receipt);
}
