using System.Text;

namespace LocalLm.Core;

public sealed record ProtectedTranslationSegment(string Token, string Original);

public sealed record TranslationPart(string Text, bool IsTranslatable);

public sealed record ProtectedTranslationText(
    string Text,
    IReadOnlyList<ProtectedTranslationSegment> Segments)
{
    public string Restore(string translated)
    {
        ArgumentNullException.ThrowIfNull(translated);
        var restored = translated;
        foreach (var segment in Segments)
        {
            var first = restored.IndexOf(segment.Token, StringComparison.Ordinal);
            var last = restored.LastIndexOf(segment.Token, StringComparison.Ordinal);
            if (first < 0 || first != last)
            {
                throw new InvalidDataException(
                    $"Translation did not preserve protected placeholder '{segment.Token}'.");
            }

            restored = restored.Replace(
                segment.Token,
                segment.Original,
                StringComparison.Ordinal);
        }

        return restored;
    }
}

public static class TranslationProtector
{
    public static IReadOnlyList<TranslationPart> SplitFencedCode(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var parts = new List<TranslationPart>();
        var offset = 0;
        foreach (var range in MarkdownCodeBlocks.Find(source))
        {
            if (range.Start > offset)
            {
                parts.Add(new TranslationPart(
                    source.Substring(offset, range.Start - offset),
                    IsTranslatable: true));
            }

            parts.Add(new TranslationPart(source.Substring(range.Start, range.Length), IsTranslatable: false));
            offset = range.Start + range.Length;
        }

        if (offset < source.Length)
        {
            parts.Add(new TranslationPart(source[offset..], IsTranslatable: true));
        }

        return Array.AsReadOnly(parts.ToArray());
    }

    public static ProtectedTranslationText ProtectFencedCode(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var segments = new List<ProtectedTranslationSegment>();
        var text = new StringBuilder();
        foreach (var part in SplitFencedCode(source))
        {
            if (part.IsTranslatable)
            {
                text.Append(part.Text);
                continue;
            }

            var token = $"__LOCALAI_PROTECTED_FENCE_{segments.Count:D4}__";
            segments.Add(new ProtectedTranslationSegment(token, part.Text));
            text.Append(token);
        }

        return new ProtectedTranslationText(
            text.ToString(),
            Array.AsReadOnly(segments.ToArray()));
    }
}
