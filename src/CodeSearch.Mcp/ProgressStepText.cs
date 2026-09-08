using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Mcp;

/// <summary>
/// What a build is doing, for a phase whose chunk counters mean nothing.
///
/// One place, called by both faces. `index_status` and the one-line `Sync:` form report the same
/// build from the same record, and the figures they already share come from one set of keys for
/// exactly this reason: a reader who sees both must never be given two accounts of one thing.
///
/// It stands where `not counted in this phase` stood. That line kept a finished embedding tally
/// from being printed beside a phase that was not counting — which was right, and still is, and is
/// <c>CountsChunks</c>'s job rather than this one's — but as an answer it said only that this
/// surface had nothing to offer. A reader watching a ten-minute Roslyn restore is not asking
/// whether anything is counted. They are asking what it is doing and whether it is moving.
/// </summary>
internal static class ProgressStepText
{
    /// <summary>
    /// Only one step carries figures, because only one has any: the analysis knows its project
    /// count before the first iteration and awaits every project in turn. The rest name their
    /// work, and what says the build is alive beside them is the age the record is printed with.
    ///
    /// A clause per member, and a throw rather than an empty string for anything else. C# cannot
    /// make a new member a build error — an enum value need not be one of the named ones, so a
    /// switch expression without the final arm is rejected as non-exhaustive (CS8524) — and an
    /// empty clause is the worse failure: it reads exactly like a phase with nothing to say, which
    /// is the state this exists to end.
    /// </summary>
    public static string Of(RepositoryIndexProgress progress) =>
        progress.Step is not { } step
            ? CodeSearchText.ProgressStepNotRecorded
            : step switch
            {
                IndexBuildStep.ReadingRepository => CodeSearchText.ProgressReadingRepository,
                IndexBuildStep.RestoringDependencies =>
                    CodeSearchText.ProgressRestoringDependencies,
                IndexBuildStep.OpeningProjects => CodeSearchText.ProgressOpeningProjects,
                IndexBuildStep.AnalysingProjects => CodeSearchText.ProgressAnalysingProjects(
                    progress.StepDone,
                    progress.StepTotal),
                IndexBuildStep.ScipTypeScript => CodeSearchText.ProgressScipTypeScript,
                IndexBuildStep.ScipPython => CodeSearchText.ProgressScipPython,
                IndexBuildStep.CopyingGeneration => CodeSearchText.ProgressCopyingGeneration,
                IndexBuildStep.SwitchingOver => CodeSearchText.ProgressSwitchingOver,
                _ => throw new ArgumentOutOfRangeException(nameof(progress), step, null),
            };
}
