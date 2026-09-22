using System.ComponentModel;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>Publish a complete transcript without truncating an old result on failure.</summary>
internal sealed class PdfOcrOutput(string source, string target)
{
    public static PdfOcrOutput Prepare(string source, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (OutputFileIdentity.IsSource(source, target))
            throw new ArgumentException(LocalLmText.PdfOutputIsSource(target));
        if (Directory.Exists(target))
            throw new ArgumentException(LocalLmText.PdfOutputIsDirectory(target));
        if (!Directory.Exists(Path.GetDirectoryName(target)))
            throw new ArgumentException(LocalLmText.PdfOutputDirectoryMissing(target));
        return new PdfOcrOutput(source, target);
    }

    public async Task<string> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        try
        {
            // A destination can become an alias of the source during inference. Check again,
            // then replace the directory entry so other hard links retain their old content.
            if (OutputFileIdentity.IsSource(source, target))
                throw new IOException(LocalLmText.PdfOutputIsSource(target));
            await AtomicFile.WriteAsync(target, async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(transcript.AsMemory(), token);
                await writer.FlushAsync(token);
                token.ThrowIfCancellationRequested();
            }, AtomicWrite.Irrecoverable, cancellationToken);
            return LocalLmText.PdfOutputWritten(target, transcript.Length);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or Win32Exception)
        {
            UsageCall.Current?.DeliveryFailed("output_not_written");
            throw new PdfOutputWriteException(target, failure);
        }
    }
}

/// <summary>Inference finished, but its complete transcript could not be delivered.</summary>
public sealed class PdfOutputWriteException(string path, Exception inner)
    : IOException(LocalLmText.PdfOutputNotWritten(path, inner.Message), inner);
