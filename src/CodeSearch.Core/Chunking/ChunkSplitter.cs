namespace CodeSearch.Core.Chunking;

/// <summary>
/// Emits one chunk for a definition, or several overlapping ones when its source runs past the
/// character budget.
/// </summary>
/// <remarks>
/// Every part repeats the header, so a part read on its own still says what it belongs to, and
/// line numbers stay exact rather than estimated. Shared by the Roslyn chunker and the
/// symbol-aware one: the rule for "this definition is too big for one vector" has nothing to do
/// with the language it was written in, and two copies of it would drift.
/// </remarks>
internal static class ChunkSplitter
{
    /// <summary>
    /// How many windows a sliding pass actually cuts, which is the denominator a part number is
    /// out of.
    ///
    /// Both callers had it as <c>ceil(lines / step)</c>, and both stop the moment a window reaches
    /// the last line — so the windows that formula counts past that point are never produced. The
    /// first window covers <paramref name="window"/> lines and each one after it advances by
    /// <paramref name="step"/>, so what is left to cover after the first is what decides the rest.
    /// </summary>
    public static int WindowCount(int lines, int window, int step) =>
        lines <= window
            ? 1
            : 1 + (int)Math.Ceiling((double)(lines - window) / step);

    public static IEnumerable<Chunk> Split(
        string relPath,
        ChunkKind kind,
        string symbol,
        string signature,
        string ns,
        string header,
        string[] lines,
        int firstLine,
        int lastLine)
    {
        var whole = header + string.Join("\n", lines);
        if (whole.Length <= ChunkLimits.MaxChars)
        {
            yield return new Chunk
            {
                RelPath = relPath,
                Kind = kind,
                Symbol = symbol,
                Signature = signature,
                Namespace = ns,
                StartLine = firstLine,
                EndLine = lastLine,
                EmbedText = whole,
            };

            yield break;
        }

        // Size the window from the file's own average line length rather than a fixed line count:
        // a 300-char-per-line generated-ish file and a terse one should both land near the budget.
        var budget = ChunkLimits.MaxChars - header.Length;
        var avgLineLength = Math.Max(1, whole.Length / Math.Max(1, lines.Length));
        var windowLines = Math.Max(20, budget / avgLineLength);
        var step = Math.Max(1, windowLines - ChunkLimits.SplitOverlapLines);
        var totalParts = WindowCount(lines.Length, windowLines, step);

        var part = 0;
        for (var offset = 0; offset < lines.Length; offset += step)
        {
            var end = Math.Min(offset + windowLines, lines.Length);
            part++;

            var text = Truncate(header + string.Join("\n", lines[offset..end]), ChunkLimits.MaxChars);

            yield return new Chunk
            {
                RelPath = relPath,
                Kind = kind,
                Symbol = $"{symbol} [{part}/{totalParts}]",
                Signature = signature,
                Namespace = ns,
                StartLine = firstLine + offset,
                EndLine = Math.Min(lastLine, firstLine + end - 1),
                EmbedText = text,
            };

            if (end == lines.Length)
            {
                break;
            }
        }
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
