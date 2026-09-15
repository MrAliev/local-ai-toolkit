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
        string translated)
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

        return new TranslationValidationResult(true, LocalLmText.ValidatorPlausible);
    }

    public static TranslationValidationResult ValidateMarkdown(
        string source,
        string translated)
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
        return failure ?? new TranslationValidationResult(true, LocalLmText.ValidatorStructurePreserved);
    }

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
