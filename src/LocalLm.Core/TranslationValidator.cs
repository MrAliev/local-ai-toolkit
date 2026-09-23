using System.Text.RegularExpressions;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

public sealed record TranslationValidationResult(bool Passed, string Detail);

public static partial class TranslationValidator
{
    private static readonly string[] PlainPromptLeakMarkers =
    [
        "Translate the following fragment",
        "Return only the translated fragment",
        "Preserve Markdown structure"
    ];

    public static TranslationValidationResult ValidatePlain(
        string source,
        string translated,
        string? sourceLanguage = null,
        string? targetLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return new TranslationValidationResult(false, LocalLmText.ValidatorEmptyTranslation);
        }

        if (translated.Contains("```", StringComparison.Ordinal) &&
            !source.Contains("```", StringComparison.Ordinal))
        {
            return new TranslationValidationResult(
                false,
                LocalLmText.ValidatorUnexpectedFence);
        }

        if (PlainPromptLeakMarkers.Any(
                marker => translated.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new TranslationValidationResult(
                false,
                LocalLmText.ValidatorPromptLeak);
        }

        var maximumPlausibleLength = Math.Max(64, source.Length * 8);
        if (translated.Length > maximumPlausibleLength)
        {
            return new TranslationValidationResult(
                false,
                LocalLmText.ValidatorExpanded(source.Length, translated.Length));
        }

        return ValidateContent(source, translated, false, sourceLanguage, targetLanguage)
            ?? new TranslationValidationResult(true, LocalLmText.ValidatorPlausible);
    }

    public static TranslationValidationResult ValidateMarkdown(
        string source,
        string translated,
        string? sourceLanguage = null,
        string? targetLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        var checks = new[]
        {
            CompareCount(LocalLmText.ValidatorHeadings, Heading().Matches(source).Count, Heading().Matches(translated).Count),
            CompareFencedCode(source, translated),
            CompareCount(LocalLmText.ValidatorListMarkers, ListMarker().Matches(source).Count, ListMarker().Matches(translated).Count),
            CompareTokens(LocalLmText.ValidatorInlineCode, InlineCode().Matches(source), translated),
            CompareTokens(LocalLmText.ValidatorUrls, Url().Matches(source), translated),
            CompareTokens(LocalLmText.ValidatorPlaceholders, Placeholder().Matches(source), translated)
        };
        var failure = checks.FirstOrDefault(result => !result.Passed);
        return failure ?? ValidateContent(source, translated, true, sourceLanguage, targetLanguage)
            ?? new TranslationValidationResult(true, LocalLmText.ValidatorStructurePreserved);
    }

    private static TranslationValidationResult? ValidateContent(string source, string translated, bool markdown,
        string? sourceLanguage, string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated)) return new(false, LocalLmText.ValidatorEmptyTranslation);
        // Preserve literal spelling and order, including repeated values and table cells.
        // A localized decimal separator is a literal change, not silently accepted rounding.
        if (!Number().Matches(ComparableText(source, markdown)).Select(match => match.Value)
            .SequenceEqual(Number().Matches(ComparableText(translated, markdown)).Select(match => match.Value), StringComparer.Ordinal))
            return new(false, LocalLmText.Get("ValidatorNumbersChanged"));
        if (string.IsNullOrWhiteSpace(sourceLanguage) || string.IsNullOrWhiteSpace(targetLanguage) ||
            string.Equals(sourceLanguage.Trim(), targetLanguage.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        var expected = ProseWords(source, markdown);
        if (expected.Length == 0) return null;
        var actual = ProseWords(translated, markdown);
        // Whitespace, punctuation and a generated footer cannot disguise a copied source.
        // This compares text; it is not language identification or semantic validation.
        if ((" " + actual + " ").Contains(" " + expected + " ", StringComparison.Ordinal))
            return new(false, LocalLmText.Get("ValidatorUnchangedProse"));
        return null;
    }

    private static string ProseWords(string text, bool markdown)
    {
        text = ComparableText(text, markdown);
        if (markdown) text = InlineCode().Replace(text, " ");
        text = Placeholder().Replace(Url().Replace(text, " "), " ");
        return string.Join(" ", Word().Matches(text).Select(match => match.Value.ToUpperInvariant()));
    }

    private static string ComparableText(string text, bool markdown)
    {
        // Fences are already compared byte-for-byte. Generated attribution identifies
        // the model, whose tag may contain digits; it is not a source numeric literal.
        if (markdown && !string.IsNullOrWhiteSpace(text)) text = string.Concat(TranslationProtector.SplitFencedCode(text)
            .Where(part => part.IsTranslatable).Select(part => part.Text));
        return AttributionLine().Replace(text, " ");
    }

    [GeneratedRegex(@"^(?:Translation performed by the local model|Перевод выполнен локальной моделью):[ \t]*[^\r\n]+\.[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributionLine();

    [GeneratedRegex(@"[+\-−]?\d+(?:[.,]\d+)*(?:[eE][+\-]?\d+)?%?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"[\p{L}\p{M}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    private static TranslationValidationResult CompareFencedCode(string source, string translated)
    {
        var expected = MarkdownCodeBlocks.Find(source)
            .Select(range => source.Substring(range.Start, range.Length));
        var actual = MarkdownCodeBlocks.Find(translated)
            .Select(range => translated.Substring(range.Start, range.Length));

        return expected.SequenceEqual(actual, StringComparer.Ordinal)
            ? new TranslationValidationResult(true, LocalLmText.ValidatorFencedCode)
            : new TranslationValidationResult(false,
                LocalLmText.ValidatorProtectedTokensChanged(LocalLmText.ValidatorFencedCode));
    }

    private static TranslationValidationResult CompareCount(
        string name,
        int expected,
        int actual) =>
        expected == actual
            ? new TranslationValidationResult(true, name)
            : new TranslationValidationResult(
                false,
                LocalLmText.ValidatorCountMismatch(name, expected, actual));

    private static TranslationValidationResult CompareTokens(
        string name,
        MatchCollection expected,
        string translated)
    {
        foreach (var group in expected
                     .Select(match => match.Value)
                     .GroupBy(value => value, StringComparer.Ordinal))
        {
            var actual = Regex.Matches(
                    translated,
                    Regex.Escape(group.Key),
                    RegexOptions.CultureInvariant)
                .Count;
            if (actual != group.Count())
            {
                return new TranslationValidationResult(
                    false,
                    LocalLmText.ValidatorProtectedTokensChanged(name));
            }
        }

        return new TranslationValidationResult(true, name);
    }

    [GeneratedRegex(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"`[^`\r\n]+`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"https?://[^\s)]+", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"\{\{?[^{}\r\n]+\}?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
