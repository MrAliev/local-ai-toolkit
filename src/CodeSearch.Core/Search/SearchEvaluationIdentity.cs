using System.Globalization;
using CodeSearch.Core.Chunking;

namespace CodeSearch.Core.Search;

internal sealed record EvaluationDeclaration(ChunkKind Kind, int StartLine, int EndLine);

internal static class SearchEvaluationIdentity
{
    // The suffix is a display convention, not an identifier. Accept only the exact positive
    // decimal format emitted by ChunkSplitter, and remove it once rather than stripping brackets.
    public static string CanonicalSymbol(string symbol) =>
        Part(symbol) is { } part ? symbol[..part.Offset] : symbol;

    public static SearchEvaluationTarget Resolve(SearchEvaluationTarget target, string source)
    {
        var chunks = new RoslynChunker().Split(target.Path, source).ToArray();
        var declarations = new List<SearchEvaluationTarget>();
        for (var index = 0; index < chunks.Length; index++)
        {
            var first = chunks[index];
            var part = Part(first.Symbol);
            if (part is { Number: not 1 } ||
                !string.Equals(CanonicalSymbol(first.Symbol), target.Symbol, StringComparison.Ordinal) ||
                (target.Signature is not null && target.Signature != first.Signature) ||
                (target.Namespace is not null && target.Namespace != first.Namespace))
            {
                continue;
            }

            // Each part sequence is one declaration. Do not group solely by the display signature:
            // it is truncated and can collide for overloads, explicit implementations or types.
            var last = chunks[index + (part?.Total ?? 1) - 1];
            declarations.Add(target with
            {
                Signature = first.Signature,
                Namespace = first.Namespace,
                Declaration = new(first.Kind, first.StartLine, last.EndLine)
            });
        }

        return declarations.Count switch
        {
            1 => declarations[0],
            0 => throw new InvalidDataException(
                $"Evaluation target symbol '{target.Symbol}' is absent from '{target.Path}' with the specified declaration qualifiers."),
            _ => throw new InvalidDataException(
                $"Evaluation target symbol '{target.Symbol}' is ambiguous in '{target.Path}'; " +
                "provide a unique signature and namespace, or use a path-only target when every declaration is relevant.")
        };
    }

    private static (int Offset, int Number, int Total)? Part(string symbol)
    {
        var offset = symbol.LastIndexOf(" [", StringComparison.Ordinal);
        if (offset <= 0 || !symbol.EndsWith(']')) return null;
        var suffix = symbol.AsSpan(offset + 2, symbol.Length - offset - 3);
        var slash = suffix.IndexOf('/');
        if (slash <= 0 || slash == suffix.Length - 1) return null;
        var number = PositiveDecimal(suffix[..slash]);
        var total = PositiveDecimal(suffix[(slash + 1)..]);
        return number > 0 && total >= number ? (offset, number, total) : null;
    }

    private static int PositiveDecimal(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value[0] == '0') return 0;
        foreach (var digit in value)
            if (digit is < '0' or > '9') return 0;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
