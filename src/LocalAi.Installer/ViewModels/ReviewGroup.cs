using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

public sealed record ReviewGroup(string Title, IReadOnlyList<string> Lines)
{
    public static IReadOnlyList<ReviewGroup> ForRemoval(UninstallPlan plan)
    {
        var (changes, retained) = plan.PreviewSections();
        return [new(PageLabels.WillBeRemoved, SplitLines(changes)), new(PageLabels.WillBeKept, SplitLines(retained))];
    }

    public static IReadOnlyList<string> SplitLines(params string[] text) => text
        .SelectMany(value => value.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        .ToArray();
}
