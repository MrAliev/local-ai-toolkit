using LocalAi.Contracts.Localization;

namespace CodeSearch.Mcp.Resources;

/// <summary>
/// What the CodeSearch tools say, in the language the reader's machine is set to.
///
/// One rule decides what is in here and what stays written into the code: anything to the left of
/// the first colon on a line is a field name and stays English, and so does any status token,
/// enum name, identifier, path or command to the right of one. Sentences and clauses are
/// translated.
///
/// That is not thrift. The instruction block this product installs into every agent
/// configuration is English-only and keys agent behaviour on the exact tokens <c>STALE</c>,
/// <c>INITIALIZING</c> and <c>CONFIGURED</c>, and the shipped Russian documentation quotes
/// <c>CONFIGURED</c> and <c>Update:</c> as literals inside Russian prose. Translating the field
/// names would break, for a Russian reader only, the rule their agent was installed with — and
/// it would widen the aligned gutter from twelve characters to twenty for a column whose values
/// are mostly identifiers.
/// </summary>
public static class CodeSearchText
{
    public static TextCatalogue Catalogue { get; } = new(
        "CodeSearch.Mcp.Resources.CodeSearchText",
        typeof(CodeSearchText).Assembly);

    public static string NoDefinition => Catalogue.Get(nameof(NoDefinition));

    public static string NoReferences => Catalogue.Get(nameof(NoReferences));

    public static string NoImplementations => Catalogue.Get(nameof(NoImplementations));

    public static string NoRelationships => Catalogue.Get(nameof(NoRelationships));

    public static string NoMatches => Catalogue.Get(nameof(NoMatches));

    /// <summary>
    /// One sentence for all eight tools, with the tool's own name in it. It used to be eight
    /// literals, one of which said "Search failed" for a tool called <c>search_code</c>.
    /// </summary>
    public static string ToolFailed(string tool, string reason) =>
        Catalogue.Format(nameof(ToolFailed), tool, reason);

    public static string LspDocumentOpen(string path, int version, string languageId) =>
        Catalogue.Format(nameof(LspDocumentOpen), path, version, languageId);

    public static string LspDocumentClosed(string path) =>
        Catalogue.Format(nameof(LspDocumentClosed), path);

    public static string LspNoLiveDocument(string path) =>
        Catalogue.Format(nameof(LspNoLiveDocument), path);

    /// <summary>
    /// The line above a result list. The third hole is the model, or the token that says
    /// nothing was embedded — a model named after a search that never used one reports a
    /// comparison that was never made.
    /// </summary>
    public static string SearchIndexHeader(
        int chunks,
        int files,
        string modelOrToken,
        string elapsed) =>
        Catalogue.Format(nameof(SearchIndexHeader), chunks, files, modelOrToken, elapsed);

    public static string SearchIndexModel(string model) =>
        Catalogue.Format(nameof(SearchIndexModel), model);

    /// <summary>
    /// What a search says when no embedding model answered. The token that opens it and the
    /// command that closes it are the same in every language; the sentences between them
    /// are the whole point of the message and are not.
    /// </summary>
    public static string SearchDegraded => Catalogue.Get(nameof(SearchDegraded));

    public static string SearchIndexHeaderStale(string indexed, string mainline) =>
        Catalogue.Format(nameof(SearchIndexHeaderStale), indexed, mainline);

    public static string Seconds(string seconds) => Catalogue.Format(nameof(Seconds), seconds);

    public static string NoIndexForRoot(string root) =>
        Catalogue.Format(nameof(NoIndexForRoot), root);

    public static string BuildItBackground => Catalogue.Get(nameof(BuildItBackground));

    public static string BuildItWith => Catalogue.Get(nameof(BuildItWith));

    public static string StatusStale(string indexed, string mainline) =>
        Catalogue.Format(nameof(StatusStale), indexed, mainline);

    public static string ModelWithDims(string model, int dimensions) =>
        Catalogue.Format(nameof(ModelWithDims), model, dimensions);

    public static string SizeMegabytes(string megabytes) =>
        Catalogue.Format(nameof(SizeMegabytes), megabytes);

    public static string BuiltAtCommit(string built, string commit) =>
        Catalogue.Format(nameof(BuiltAtCommit), built, commit);

    public static string NavigationPrecise => Catalogue.Get(nameof(NavigationPrecise));

    /// <summary>
    /// A path search_code would never have printed, refused by name. Passed on, it came back as
    /// the core's ArgumentException — English, with a framework parameter suffix — inside an
    /// answer that otherwise follows the reader.
    /// </summary>
    public static string PathNotRelative(string path) =>
        Catalogue.Format(nameof(PathNotRelative), path);

    /// <summary>
    /// A position nobody could have meant, refused by name. Passing it on would answer
    /// about the line above and look like an answer.
    /// </summary>
    public static string PositionNotFromOne(int line, int column) =>
        Catalogue.Format(nameof(PositionNotFromOne), line, column);

    /// <summary>The form the locations below are printed in, said once, in the open.</summary>
    public static string LocationNumbering => Catalogue.Get(nameof(LocationNumbering));

    public static string NavigationHeuristicCoversNothing(string command) =>
        Catalogue.Format(nameof(NavigationHeuristicCoversNothing), command);

    public static string NavigationHeuristicMissing(string command) =>
        Catalogue.Format(nameof(NavigationHeuristicMissing), command);

    public static string NavigationUnknownHeader(string command) =>
        Catalogue.Format(nameof(NavigationUnknownHeader), command);

    public static string NavigationHeaderOnly => Catalogue.Get(nameof(NavigationHeaderOnly));

    /// <summary>
    /// A worktree whose required semantic overlay has not been built. This differs from a
    /// missing base or an unreadable header —
    /// HEURISTIC would say answers arrive and are merely worse, and two of the four tools refuse
    /// outright in this state.
    /// </summary>
    public static string NavigationOverlayNotBuilt(string command) =>
        Catalogue.Format(nameof(NavigationOverlayNotBuilt), command);

    /// <summary>
    /// What a build in flight says on the answers it changes, and what it says when it ends.
    ///
    /// The figures come from the keys <c>index_status</c> already prints, so the two faces cannot
    /// disagree about the same build. These four are what the inline line adds: how long ago the
    /// figures were written, what they mean for the answer they sit above, and — once — that the
    /// build is over and the qualification can be dropped.
    ///
    /// <see cref="SyncEnded"/> and <see cref="SyncStopped"/> begin lower case: they follow the
    /// literal <c>Sync: </c> prefix, which is a field name and stays English.
    /// </summary>
    public static string RecordedAgo(string age) => Catalogue.Format(nameof(RecordedAgo), age);

    public static string SyncNotSearchableYet => Catalogue.Get(nameof(SyncNotSearchableYet));

    /// <summary>
    /// The two rungs above <see cref="EtaMinutes"/> on the ladder <see cref="RecordedAgo"/>
    /// carries. They exist because a build whose process died leaves a record nobody clears, so
    /// the age this reports is measured in hours and days rather than in minutes, and
    /// <c>recorded 2880 min ago</c> is a number nobody reads.
    /// </summary>
    public static string Hours(string hours) => Catalogue.Format(nameof(Hours), hours);

    public static string Days(string days) => Catalogue.Format(nameof(Days), days);

    /// <summary>
    /// What stands where the figures go when a build holds the gate and has written nothing yet -
    /// the window between a sync starting and its first record. It is also the antecedent of "it"
    /// in <see cref="SyncNotSearchableYet"/>, which names nothing without a line above it.
    /// </summary>
    public static string ProgressNotRecordedYet =>
        Catalogue.Get(nameof(ProgressNotRecordedYet));

    /// <summary>
    /// Wraps the figures when a record is left behind by a build that is no longer running. The
    /// ETA is dropped inside it: it was the dead process's prediction of a remaining time nobody
    /// is spending.
    /// </summary>
    public static string ProgressStoppedAt(string figures) =>
        Catalogue.Format(nameof(ProgressStoppedAt), figures);

    /// <summary>
    /// The end notice for a build that died rather than failed. A killed process never writes
    /// <c>Failed</c>, so it never reaches <see cref="SyncStopped"/>.
    /// </summary>
    public static string SyncDidNotFinish => Catalogue.Get(nameof(SyncDidNotFinish));

    /// <summary>
    /// Replaces the instruction to start a build, on the paths that give one, while a build is
    /// already running. That instruction is not merely redundant then - a second
    /// <c>localai sync</c> exits 75 against the gate - and it is what a new machine shows on
    /// every search for the whole of its first build.
    /// </summary>
    public static string BuildAlreadyRunning => Catalogue.Get(nameof(BuildAlreadyRunning));

    public static string SyncEnded => Catalogue.Get(nameof(SyncEnded));

    public static string SyncStopped => Catalogue.Get(nameof(SyncStopped));

    /// <summary>
    /// One clause per <see cref="LocalAi.Contracts.IndexBuildStep"/> member, in the slot that used
    /// to hold `not counted in this phase` for every phase but the two that embed.
    ///
    /// They replace that line rather than joining it. It said only that this surface had nothing
    /// to offer, which was true of the old build and useless in the state a reader reaches for it:
    /// the question is never "does it count" but "what is it doing, and is it moving". Only one of
    /// these carries figures, because only one step has any — the rest name the work, and what
    /// says a build is alive there is the age printed beside them.
    /// </summary>
    public static string ProgressReadingRepository =>
        Catalogue.Get(nameof(ProgressReadingRepository));

    public static string ProgressRestoringDependencies =>
        Catalogue.Get(nameof(ProgressRestoringDependencies));

    public static string ProgressOpeningProjects =>
        Catalogue.Get(nameof(ProgressOpeningProjects));

    public static string ProgressAnalysingProjects(int analysed, int total) =>
        Catalogue.Format(nameof(ProgressAnalysingProjects), analysed, total);

    public static string ProgressScipTypeScript =>
        Catalogue.Get(nameof(ProgressScipTypeScript));

    public static string ProgressScipPython => Catalogue.Get(nameof(ProgressScipPython));

    public static string ProgressCopyingGeneration =>
        Catalogue.Get(nameof(ProgressCopyingGeneration));

    public static string ProgressSwitchingOver => Catalogue.Get(nameof(ProgressSwitchingOver));

    /// <summary>
    /// What stands there for a record written by a build that predates the step. It names what is
    /// missing rather than what is happening, because nothing here knows what is happening.
    /// </summary>
    public static string ProgressStepNotRecorded =>
        Catalogue.Get(nameof(ProgressStepNotRecorded));

    public static string ProgressChunks(int processed, int total, int remaining) =>
        Catalogue.Format(nameof(ProgressChunks), processed, total, remaining);

    /// <summary>
    /// One key per <see cref="LocalAi.Contracts.IndexBuildPause"/> member rather than a generic
    /// clause with the token in it, which is the enum's own rule — a second member gets a second
    /// key, and the switch that chooses between them stops compiling until it does.
    /// </summary>
    public static string ProgressPausedBackendUnavailable =>
        Catalogue.Get(nameof(ProgressPausedBackendUnavailable));

    public static string RateChunks(string rate) => Catalogue.Format(nameof(RateChunks), rate);

    public static string EtaMinutes(string minutes) =>
        Catalogue.Format(nameof(EtaMinutes), minutes);

    public static string EtaCalculating => Catalogue.Get(nameof(EtaCalculating));

    public static string NothingLoaded => Catalogue.Get(nameof(NothingLoaded));

    public static string WorkingSetTrimmed => Catalogue.Get(nameof(WorkingSetTrimmed));

    public static string WorkingSetNotTrimmed => Catalogue.Get(nameof(WorkingSetNotTrimmed));

    public static string Unloaded(string names) => Catalogue.Format(nameof(Unloaded), names);

    public static string ProcessMemory(long freed, long remaining, string trim) =>
        Catalogue.Format(nameof(ProcessMemory), freed, remaining, trim);

    public static string ReloadsInASecond => Catalogue.Get(nameof(ReloadsInASecond));

    public static string CliUnavailable(string executable) =>
        Catalogue.Format(nameof(CliUnavailable), executable);

    public static string SyncFailed(int exitCode, string error) =>
        Catalogue.Format(nameof(SyncFailed), exitCode, error);

    public static string StatusNotRefreshed(int files, int limit) =>
        Catalogue.Format(nameof(StatusNotRefreshed), files, limit);

    public static string NothingHappened => Catalogue.Get(nameof(NothingHappened));

    public static string TooBigForToolCall => Catalogue.Get(nameof(TooBigForToolCall));

    public static string RunInBackground => Catalogue.Get(nameof(RunInBackground));

    public static string WhileItRuns => Catalogue.Get(nameof(WhileItRuns));

    /// <summary>
    /// The three values are nullable on the way in and were interpolated as such before, which
    /// renders a missing one as nothing rather than as the word "null".
    /// </summary>
    public static string UpdateAvailable(string? latest, string? installed, string? url) =>
        Catalogue.Format(nameof(UpdateAvailable), latest, installed, url);
}
