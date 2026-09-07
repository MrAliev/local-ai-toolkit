namespace LocalLm.Core;

/// <summary>
/// Cutting a string at a chosen length can land between the two halves of a surrogate pair, which
/// turns one character into two that mean nothing apart. Both cutters here ask this: the fragment
/// reader, which cuts a log into pieces that fit a prompt, and the reducer's splitter, which cuts
/// an over-long finding into pieces that fit the next level.
/// </summary>
internal static class SurrogateSafe
{
    /// <summary>
    /// The length to cut at, extended by one when cutting at <paramref name="length"/> would split
    /// a pair. Never shortens: a cut that is one character long has nothing to give back.
    /// </summary>
    internal static int Extend(string text, int offset, int length) =>
        offset + length < text.Length &&
        length > 0 &&
        char.IsHighSurrogate(text[offset + length - 1]) &&
        char.IsLowSurrogate(text[offset + length])
            ? length + 1
            : length;
}
