using System.Globalization;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>One-based inclusive ranges, resolved once into document order without duplicates.</summary>
public sealed class PdfPageSelection
{
    private readonly (int First, int Last)[] ranges;
    private PdfPageSelection((int First, int Last)[] ranges) => this.ranges = ranges;

    public static PdfPageSelection Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            throw new ArgumentException(LocalLmText.PdfPagesInvalid, nameof(text));
        var ranges = new List<(int, int)>();
        foreach (var part in text.Split(','))
        {
            var ends = part.Trim().Split('-');
            if (ends.Length is < 1 or > 2 ||
                !int.TryParse(ends[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1)
                throw new ArgumentException(LocalLmText.PdfPagesInvalid, nameof(text));
            var last = first;
            if (ends.Length == 2 &&
                (!int.TryParse(ends[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
                throw new ArgumentException(LocalLmText.PdfPagesInvalid, nameof(text));
            ranges.Add((first, last));
        }
        return new PdfPageSelection(ranges.ToArray());
    }

    public IReadOnlyList<int> Resolve(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var selected = new SortedSet<int>();
        foreach (var (first, last) in ranges)
        {
            if (last > pageCount) throw new ArgumentException(LocalLmText.PdfPagesOutsideDocument(pageCount));
            for (var page = first; ; page++)
            {
                selected.Add(page);
                if (page == last) break;
            }
        }
        return selected.ToArray();
    }
}
