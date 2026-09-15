using Markdig;
using Markdig.Syntax;

namespace LocalLm.Core;

internal readonly record struct MarkdownCodeRange(int Start, int Length);

internal static class MarkdownCodeBlocks
{
    public static IReadOnlyList<MarkdownCodeRange> Find(string source)
    {
        var ranges = new List<MarkdownCodeRange>();
        foreach (var block in Markdown.Parse(source).Descendants<FencedCodeBlock>())
        {
            // Markdig resolves indentation, containers, and unterminated fences. Keep
            // the physical source lines, including container prefixes and line endings:
            // rendering the parsed block would normalize bytes that must not change.
            var start = block.Span.Start;
            while (start > 0 && source[start - 1] is not ('\r' or '\n')) start--;
            var end = Math.Min(block.Span.End + 1, source.Length);
            // An unclosed fence's Span may still end on its opening line. The last
            // content line retains its original position even when tabs were expanded
            // into a different StringSlice by the parser.
            if (block.Lines.Count > 0)
                end = Math.Max(end, block.Lines.Lines[block.Lines.Count - 1].Position);
            while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            if (end < source.Length && source[end] == '\r') end++;
            if (end < source.Length && source[end] == '\n') end++;
            ranges.Add(new MarkdownCodeRange(start, end - start));
        }

        return ranges;
    }
}
