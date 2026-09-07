using System.Text;

namespace CodeSearch.Core.Chunking;

/// <summary>
/// Fallback chunker for everything that isn't C#: a sliding window over lines with overlap.
/// No syntax awareness, but it keeps exact line numbers, which is what makes a hit actionable.
/// </summary>
public sealed class GenericTextChunker : IChunker
{
    private const int WindowLines = 60;
    private const int OverlapLines = 12;

    public IEnumerable<Chunk> Split(string relPath, string content)
    {
        // A NUL byte means we were handed something binary that happens to carry a text
        // extension. Embedding it produces a meaningless vector, so drop the file.
        if (content.IndexOf('\0') >= 0)
        {
            yield break;
        }

        var lines = SourceLines.Split(content);
        if (lines.Length == 0)
        {
            yield break;
        }

        var fileName = Path.GetFileName(relPath);
        var step = WindowLines - OverlapLines;
        var part = 0;
        // How many windows are actually cut, which is not `ceil(lines / step)`: the loop stops as
        // soon as a window reaches the last line, so the tail windows that formula counts are
        // never produced. Every file of 48 to 59 lines was announced as `[1/2]` — a reader told
        // there is a second half they have not seen.
        var total = ChunkSplitter.WindowCount(lines.Length, WindowLines, step);

        for (var start = 0; start < lines.Length; start += step)
        {
            var end = Math.Min(start + WindowLines, lines.Length);
            part++;

            var body = string.Join("\n", lines[start..end]);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (body.Length > ChunkLimits.MaxChars)
            {
                body = body[..ChunkLimits.MaxChars];
            }

            var text = new StringBuilder()
                .Append("File: ").AppendLine(relPath)
                .Append("Lines: ").Append(start + 1).Append('-').Append(end).AppendLine()
                .AppendLine()
                .Append(body)
                .ToString();

            yield return new Chunk
            {
                RelPath = relPath,
                Kind = ChunkKind.Text,
                Symbol = total > 1 ? $"{fileName} [{part}/{total}]" : fileName,
                Signature = $"{relPath}:{start + 1}-{end}",
                Namespace = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty,
                StartLine = start + 1,
                EndLine = end,
                EmbedText = text,
            };

            if (end == lines.Length)
            {
                break;
            }
        }
    }
}

public static class SourceLines
{
    /// <summary>
    /// The lines of a file, without the empty one a trailing newline leaves behind.
    ///
    /// Nearly every file ends in a newline, and splitting on it gives a phantom last element that
    /// the chunkers counted as a line: a two-line file was published as `1-3`, a 21-line
    /// `.csproj` as `1-22`. That range is what a reader opens their editor at and what the
    /// `Lines:` header inside the embedded text says.
    ///
    /// Exactly one is dropped, not every trailing blank: a file that genuinely ends in blank
    /// lines has them, and they are its lines.
    /// </summary>
    public static string[] Split(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
    }
}
