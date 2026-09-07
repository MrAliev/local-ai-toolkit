using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// Reduces an unbounded stream of partial findings to one, in levels, so neither the number of
/// them nor the size of any one has to fit a single prompt.
///
/// Written for log triage, where a log yields as many fragments as it yields. It is here rather
/// than inside that pipeline because reading a document page by page is the same shape of work:
/// many partial answers, bounded groups, one answer at the end. The caller supplies the prompt
/// through <c>reduce</c>, so nothing about logs is baked in.
/// </summary>
internal sealed class HierarchicalReducer(
    int characterBudget,
    Func<IReadOnlyList<string>, int, CancellationToken, Task<ReducedSummary>> reduce)
{
    private readonly List<Level> levels = [];
    private LocalUsageReceipt? lastReceipt;

    public async Task AddAsync(string summary, CancellationToken cancellationToken)
    {
        foreach (var piece in Split(summary, Math.Max(256, characterBudget / 2)))
        {
            await AddPieceAsync(0, piece, cancellationToken);
        }
    }

    public async Task<ReducedSummary> CompleteAsync(CancellationToken cancellationToken)
    {
        for (var levelIndex = 0; ; levelIndex++)
        {
            EnsureLevel(levelIndex);
            var level = levels[levelIndex];
            var isHighest = levelIndex == levels.Count - 1;
            if (isHighest)
            {
                if (level.Items.Count == 0)
                {
                    return new ReducedSummary(
                        LocalLmText.EmptyLog,
                        lastReceipt);
                }

                if (level.Items.Count == 1)
                {
                    return new ReducedSummary(level.Items[0], lastReceipt);
                }

                var final = await reduce(
                    level.Items,
                    levelIndex,
                    cancellationToken);
                lastReceipt = final.Receipt;
                return final;
            }

            if (level.Items.Count == 0)
            {
                continue;
            }

            var reduced = level.Items.Count == 1
                ? new ReducedSummary(level.Items[0], lastReceipt)
                : await reduce(level.Items, levelIndex, cancellationToken);
            if (reduced.Receipt is not null)
            {
                lastReceipt = reduced.Receipt;
            }

            level.Clear();
            await AddPieceAsync(
                levelIndex + 1,
                reduced.Text,
                cancellationToken);
        }
    }

    private async Task AddPieceAsync(
        int levelIndex,
        string piece,
        CancellationToken cancellationToken)
    {
        EnsureLevel(levelIndex);
        var level = levels[levelIndex];
        if (level.Items.Count > 0 &&
            level.Characters + piece.Length > characterBudget)
        {
            var reduced = await reduce(
                level.Items,
                levelIndex,
                cancellationToken);
            lastReceipt = reduced.Receipt;
            level.Clear();
            await AddPieceAsync(
                levelIndex + 1,
                reduced.Text,
                cancellationToken);
        }

        level.Add(piece);
    }

    private void EnsureLevel(int levelIndex)
    {
        while (levels.Count <= levelIndex)
        {
            levels.Add(new Level());
        }
    }

    private static IEnumerable<string> Split(string value, int maximumCharacters)
    {
        if (value.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(maximumCharacters, value.Length - offset);
            length = SurrogateSafe.Extend(value, offset, length);
            yield return value.Substring(offset, length);
            offset += length;
        }
    }

    private sealed class Level
    {
        public List<string> Items { get; } = [];

        public int Characters { get; private set; }

        public void Add(string value)
        {
            Items.Add(value);
            Characters = checked(Characters + value.Length);
        }

        public void Clear()
        {
            Items.Clear();
            Characters = 0;
        }
    }
}

internal sealed record ReducedSummary(string Text, LocalUsageReceipt? Receipt);
