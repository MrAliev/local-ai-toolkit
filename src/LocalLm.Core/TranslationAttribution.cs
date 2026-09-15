using System.Text.RegularExpressions;

namespace LocalLm.Core;

public static partial class TranslationAttribution
{
    public static string Append(
        string translated,
        string targetLanguage,
        string model) => Append(translated, targetLanguage, model, markdown: false);

    internal static string Append(
        string translated,
        string targetLanguage,
        string model,
        bool markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translated);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var note = targetLanguage.Contains("Russian", StringComparison.OrdinalIgnoreCase) ||
                   targetLanguage.Contains("рус", StringComparison.OrdinalIgnoreCase)
            ? $"Перевод выполнен локальной моделью: {model}."
            : $"Translation performed by the local model: {model}.";
        var protectedEnd = 0;
        if (markdown)
        {
            var ranges = MarkdownCodeBlocks.Find(translated);
            if (ranges.Count > 0)
            {
                var last = ranges[^1];
                protectedEnd = last.Start + last.Length;
                // Appending here could change a fence's final line ending or put the
                // note inside an unclosed block. Keep the document intact after it.
                if (protectedEnd == translated.Length)
                    return $"{note}\r\n\r\n{translated}";
            }
        }

        // Only prose after the final code block can contain a replaceable note. A
        // matching line in code is user content, even if it names another model.
        var withoutExisting = ExistingAttribution()
            .Replace(translated[protectedEnd..].TrimEnd(), string.Empty)
            .TrimEnd();
        return $"{translated[..protectedEnd]}{withoutExisting}\r\n\r\n{note}\r\n";
    }

    [GeneratedRegex(
        @"(?:\r?\n){0,2}(?:Translation performed by the local model|Перевод выполнен локальной моделью):\s*[^\r\n]+\.\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistingAttribution();
}
