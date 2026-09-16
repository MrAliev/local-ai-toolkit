#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAi.Quality;

public sealed record OcrFieldReference(string Label, string Value);

public sealed record OcrQualityScore(
    bool TechnicalCompletion,
    bool HasOutput,
    double? CharacterErrorRate,
    double? WordErrorRate,
    int ReferenceLineCount,
    int? OutputLineCount,
    double? FieldLabelRecall,
    double? FieldValueAccuracy,
    long ElapsedMilliseconds);

public static class OcrQualityScoring
{
    public static OcrQualityScore Measure(string reference, string? output, bool invocationSucceeded,
        IReadOnlyList<OcrFieldReference> fields, long elapsedMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Label);
            ArgumentNullException.ThrowIfNull(field.Value);
        }

        var expected = Normalize(reference);
        var actual = Normalize(output ?? "");
        var referenceLines = Lines(expected);
        var expectedFields = fields.Select(field => (Label: Normalize(field.Label), Value: Normalize(field.Value))).ToArray();
        if (MatchCount(expectedFields, ParseFields(referenceLines)) != fields.Count)
            throw new ArgumentException("ocr_reference_fields_invalid", nameof(fields));
        if (!invocationSucceeded)
            return new(false, actual.Length > 0, null, null, referenceLines.Length, null, null, null, elapsedMilliseconds);

        var actualLines = Lines(actual);
        var expectedCharacters = expected.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var actualCharacters = actual.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var expectedWords = Words(expected);
        var actualWords = Words(actual);
        var actualFields = ParseFields(actualLines);

        return new(true, actual.Length > 0,
            Distance(expectedCharacters, actualCharacters) / (double)expectedCharacters.Length,
            Distance(expectedWords, actualWords) / (double)expectedWords.Length,
            referenceLines.Length, actualLines.Length,
            fields.Count == 0 ? null : MatchCount(expectedFields.Select(field => field.Label), actualFields.Select(field => field.Label)) / (double)fields.Count,
            fields.Count == 0 ? null : MatchCount(expectedFields, actualFields) / (double)fields.Count,
            elapsedMilliseconds);
    }

    // Only canonical Unicode, line endings and outer whitespace are normalized. Repetitions,
    // punctuation, letter case and internal line breaks remain evidence of recognition quality.
    private static string Normalize(string text) => text.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n").Trim();
    private static string[] Lines(string text) => text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    private static string[] Words(string text) => text.Length == 0 ? [] : Regex.Split(text, @"\s+");

    private static (string Label, string Value)[] ParseFields(string[] lines) =>
        lines.Where(line => line.Contains(':')).Select(line =>
        {
            var separator = line.IndexOf(':');
            return (Label: line[..separator].Trim(), Value: line[(separator + 1)..].Trim());
        }).ToArray();

    private static int MatchCount<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : notnull
    {
        var remaining = actual.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        var matched = 0;
        foreach (var value in expected)
        {
            if (remaining.TryGetValue(value, out var count) && count > 0)
            {
                matched++;
                remaining[value] = count - 1;
            }
        }
        return matched;
    }

    private static int Distance<T>(T[] expected, T[] actual)
    {
        // Edit distance is symmetric; keep the shorter sequence in the row buffers.
        if (expected.Length < actual.Length) (expected, actual) = (actual, expected);
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var current = new int[actual.Length + 1];
        for (var row = 1; row <= expected.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= actual.Length; column++)
            {
                var substitution = EqualityComparer<T>.Default.Equals(expected[row - 1], actual[column - 1]) ? 0 : 1;
                current[column] = Math.Min(previous[column] + 1,
                    Math.Min(current[column - 1] + 1, previous[column - 1] + substitution));
            }
            (previous, current) = (current, previous);
        }
        return previous[actual.Length];
    }
}
