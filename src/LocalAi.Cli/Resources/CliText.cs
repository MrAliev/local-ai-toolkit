using LocalAi.Contracts.Localization;

namespace LocalAi.Cli.Resources;

/// <summary>
/// What the CLI says, in the language the reader's machine is set to.
///
/// The same rule as the search tools, in a second costume. Anything to the left of the first
/// colon is a field name and stays English — and in a report that separates name from value with
/// two spaces instead of punctuation, the left column is that same field name. So the left
/// thirty-two columns of a `doctor` report are byte-identical in both languages, and a Russian
/// paste and an English one into the same issue still diff line for line.
///
/// Status markers stay too. <c>ok  </c>, <c>note</c> and <c>FAIL</c> are a three-value column
/// that is fixed-width by construction, and they are the same kind of thing as the
/// <c>STALE</c>/<c>precise</c> pair the search tools keep.
/// </summary>
public static class CliText
{
    public static string UsageReportArguments => Catalogue.Get(nameof(UsageReportArguments));
    public static string UsageReportNotFound(Guid id) => Catalogue.Format(nameof(UsageReportNotFound), id);
    public static TextCatalogue Catalogue { get; } = new(
        "LocalAi.Cli.Resources.CliText",
        typeof(CliText).Assembly);

    public static string VersionNoPointer(string binRoot) =>
        Catalogue.Format(nameof(VersionNoPointer), binRoot);

    public static string ModelCatalogEmbedded(string version) =>
        Catalogue.Format(nameof(ModelCatalogEmbedded), version);

    public static string ProgressReadingPdfPage(int index, int total, string left) =>
        Catalogue.Format(nameof(ProgressReadingPdfPage), index, total, left);

    public static string ModelCatalogConfigured(string path, string version) =>
        Catalogue.Format(nameof(ModelCatalogConfigured), path, version);

    public static string ModelCatalogUnreadable(string path, string reason) =>
        Catalogue.Format(nameof(ModelCatalogUnreadable), path, reason);

    public static string VersionPointerUnreadable(string reason) =>
        Catalogue.Format(nameof(VersionPointerUnreadable), reason);

    public static string VersionPointerEmpty => Catalogue.Get(nameof(VersionPointerEmpty));

    public static string VersionDirectoryMissing(string version) =>
        Catalogue.Format(nameof(VersionDirectoryMissing), version);

    public static string VersionFilesMissing(string version, string missing) =>
        Catalogue.Format(nameof(VersionFilesMissing), version, missing);

    public static string VersionComplete(string version, int binaries) =>
        Catalogue.Format(nameof(VersionComplete), version, binaries);

    public static string LauncherMissing(string path) =>
        Catalogue.Format(nameof(LauncherMissing), path);

    public static string BrokerNotRunning => Catalogue.Get(nameof(BrokerNotRunning));

    public static string BrokerStateUnreadable(string reason) =>
        Catalogue.Format(nameof(BrokerStateUnreadable), reason);

    public static string BrokerStateEmpty => Catalogue.Get(nameof(BrokerStateEmpty));

    public static string BrokerSilent(int processId, string minutes) =>
        Catalogue.Format(nameof(BrokerSilent), processId, minutes);

    public static string BrokerAlive(int processId, string seconds) =>
        Catalogue.Format(nameof(BrokerAlive), processId, seconds);

    public static string QueueQuarantined(int queued, int quarantined) =>
        Catalogue.Format(nameof(QueueQuarantined), queued, quarantined);

    public static string QueueClean(int queued) => Catalogue.Format(nameof(QueueClean), queued);

    public static string QueueStalled(int queued, string minutes) =>
        Catalogue.Format(nameof(QueueStalled), queued, minutes);

    public static string PolicyModels(object residency, int keepAliveSeconds) =>
        Catalogue.Format(nameof(PolicyModels), residency, keepAliveSeconds);

    public static string PolicyRetention(int generations, int versions, int telemetryDays) =>
        Catalogue.Format(nameof(PolicyRetention), generations, versions, telemetryDays);

    public static string PolicyLanguageServersEnabled(string languages) =>
        Catalogue.Format(nameof(PolicyLanguageServersEnabled), languages);

    public static string PolicyLanguageServersDisabled =>
        Catalogue.Get(nameof(PolicyLanguageServersDisabled));

    public static string DoctorRootWithoutDirectory =>
        Catalogue.Get(nameof(DoctorRootWithoutDirectory));

    public static string DoctorTwoRepositories =>
        Catalogue.Get(nameof(DoctorTwoRepositories));

    public static string DoctorUnknownArgument(string argument, string usage) =>
        Catalogue.Format(nameof(DoctorUnknownArgument), argument, usage);

    /// <summary>
    /// Reached only when no repository was named, so naming <c>--root</c> is a next step rather
    /// than a repetition of what the reader just typed.
    /// </summary>
    public static string RepositoryNoneHere(string directory) =>
        Catalogue.Format(nameof(RepositoryNoneHere), directory);

    /// <summary>
    /// Says nothing about whether there is a repository. There is one — a <c>.git</c> was found —
    /// and git would not answer about it. A row folding this into "not a repository" would name a
    /// cause it never established.
    /// </summary>
    public static string RepositoryNotChecked(string reason) =>
        Catalogue.Format(nameof(RepositoryNotChecked), reason);

    public static string PolicyDefaults(string detail) =>
        Catalogue.Format(nameof(PolicyDefaults), detail);

    /// <summary>
    /// A policy file that is on disk and is doing nothing. Every store answers a document it
    /// cannot use with its defaults rather than an error, so without this line the report showed
    /// those defaults as though the file had produced them.
    /// </summary>
    public static string PolicyFileNotUsed(string detail, string path) =>
        Catalogue.Format(nameof(PolicyFileNotUsed), detail, path);

    // What `localai policy` itself prints. The report above answers about a machine; these
    // answer to somebody changing it, and say what the change costs.

    /// <summary>
    /// The whole usage block, holes and all.
    ///
    /// One string rather than a line each because its two columns only mean anything together:
    /// the description column sits at 23, set by the longest caption, and a caption translated a
    /// few characters longer moves it for every row at once. The interval range is a hole because
    /// the validator reads those same two constants, and a usage block naming a range nothing
    /// enforces is a lie no parity test can catch.
    ///
    /// Line endings are normalised because XML normalises them the other way: a resource file
    /// written with CRLF hands back LF, and this prints beside a WriteLine.
    /// </summary>
    public static string PolicyUsage(int minimumIntervalHours, int maximumIntervalHours) =>
        Catalogue.Format(nameof(PolicyUsage), minimumIntervalHours, maximumIntervalHours)
            .ReplaceLineEndings();

    public static string PolicyLanguageUnknown(string requested, string supported) =>
        Catalogue.Format(nameof(PolicyLanguageUnknown), requested, supported);

    public static string PolicyLanguageSystem => Catalogue.Get(nameof(PolicyLanguageSystem));

    public static string PolicyLanguageRestartNote =>
        Catalogue.Get(nameof(PolicyLanguageRestartNote));

    public static string PolicyKeepAlive(int seconds) =>
        Catalogue.Format(nameof(PolicyKeepAlive), seconds);

    public static string PolicyKeepAliveInvalid(string requested) =>
        Catalogue.Format(nameof(PolicyKeepAliveInvalid), requested);

    public static string PolicyResidencyRelaxed => Catalogue.Get(nameof(PolicyResidencyRelaxed));

    public static string PolicyResidencyMarks => Catalogue.Get(nameof(PolicyResidencyMarks));

    public static string PolicyResidencyNowCpu => Catalogue.Get(nameof(PolicyResidencyNowCpu));

    public static string PolicyResidencyNowPartial =>
        Catalogue.Get(nameof(PolicyResidencyNowPartial));

    public static string PolicyResidencyUnknown(string requested) =>
        Catalogue.Format(nameof(PolicyResidencyUnknown), requested);

    public static string PolicyRestartNote => Catalogue.Get(nameof(PolicyRestartNote));

    public static string PolicyUpdateCheckOn(int intervalHours) =>
        Catalogue.Format(nameof(PolicyUpdateCheckOn), intervalHours);

    public static string PolicyUpdateCheckOffWithInterval(int intervalHours) =>
        Catalogue.Format(nameof(PolicyUpdateCheckOffWithInterval), intervalHours);

    public static string PolicyUpdateCheckUnknown(string requested) =>
        Catalogue.Format(nameof(PolicyUpdateCheckUnknown), requested);

    public static string PolicyUpdateCheckIntervalInvalid(
        string requested,
        int minimumIntervalHours,
        int maximumIntervalHours) =>
        Catalogue.Format(
            nameof(PolicyUpdateCheckIntervalInvalid),
            requested,
            minimumIntervalHours,
            maximumIntervalHours);

    public static string PolicyUpdateCheckNothingFetched =>
        Catalogue.Get(nameof(PolicyUpdateCheckNothingFetched));

    public static string UpdateCheckDisabled => Catalogue.Get(nameof(UpdateCheckDisabled));

    public static string UpdateAvailable(string? latest, string? installed, string? url) =>
        Catalogue.Format(nameof(UpdateAvailable), latest, installed, url);

    public static string UpdateUpToDate(string? installed, string? checkedAt) =>
        Catalogue.Format(nameof(UpdateUpToDate), installed, checkedAt);

    public static string UpdateUnknownUnavailable(string? triedAt) =>
        Catalogue.Format(nameof(UpdateUnknownUnavailable), triedAt);

    public static string UpdateIncomparable(string? latest) =>
        Catalogue.Format(nameof(UpdateIncomparable), latest);

    public static string UpdateNeverChecked => Catalogue.Get(nameof(UpdateNeverChecked));

    /// <summary>
    /// What the last check verified. The timestamp arrives already formatted, so the resource
    /// carries no format specifier for a translator to alter.
    /// </summary>
    public static string UpdateVerifiedRelease(string? latest, string? checkedAt) =>
        Catalogue.Format(nameof(UpdateVerifiedRelease), latest, checkedAt);

    // What `localai update` prints while it installs. The keys above answer "is there a
    // newer one"; these answer to somebody who decided to take it.

    public static string UpdateCancelled => Catalogue.Get(nameof(UpdateCancelled));

    public static string UpdateClientsPickItUp => Catalogue.Get(nameof(UpdateClientsPickItUp));

    public static string UpdateDidNotComplete(object status, string? reason) =>
        Catalogue.Format(nameof(UpdateDidNotComplete), status, reason);

    public static string UpdateFailed(string reason) =>
        Catalogue.Format(nameof(UpdateFailed), reason);

    public static string UpdateInstalled(string? version, string versionPath) =>
        Catalogue.Format(nameof(UpdateInstalled), version, versionPath);

    public static string UpdateLookingForRelease => Catalogue.Get(nameof(UpdateLookingForRelease));

    public static string UpdateNoInstallation(string runtimeRoot) =>
        Catalogue.Format(nameof(UpdateNoInstallation), runtimeRoot);

    public static string UpdateNothingToInstall(string? latest) =>
        Catalogue.Format(nameof(UpdateNothingToInstall), latest);

    public static string UpdateQueueBusy(int queued) =>
        Catalogue.Format(nameof(UpdateQueueBusy), queued);

    public static string UpdateQueueStillBusy(int queued, int minutes) =>
        Catalogue.Format(nameof(UpdateQueueStillBusy), queued, minutes);

    public static string UpdateQueueWaiting(int queued) =>
        Catalogue.Format(nameof(UpdateQueueWaiting), queued);

    public static string UpdateUnknownOption(string option) =>
        Catalogue.Format(nameof(UpdateUnknownOption), option);

    public static string UpdateUsage(int maximumWaitMinutes) =>
        Catalogue.Format(nameof(UpdateUsage), maximumWaitMinutes).ReplaceLineEndings();

    // What `localai prune` accounts for. Every row is a count of what went, not a sentence
    // about it: the same rows print under --dry-run, where nothing went at all.

    public static string PruneArchive(int jobs, int responseBodies, string megabytes) =>
        Catalogue.Format(nameof(PruneArchive), jobs, responseBodies, megabytes);

    public static string PruneDryRun => Catalogue.Get(nameof(PruneDryRun));

    public static string PruneLauncherBackups(int removed, string megabytes) =>
        Catalogue.Format(nameof(PruneLauncherBackups), removed, megabytes);

    public static string PruneOverlaysLeftAlone(string repositoryId, string reason) =>
        Catalogue.Format(nameof(PruneOverlaysLeftAlone), repositoryId, reason);

    public static string PruneRecordLeftAlone(string repositoryId, string reason) =>
        Catalogue.Format(nameof(PruneRecordLeftAlone), repositoryId, reason);

    public static string PruneQuarantine(int entries) =>
        Catalogue.Format(nameof(PruneQuarantine), entries);

    public static string PruneReclaimed(string megabytes) =>
        Catalogue.Format(nameof(PruneReclaimed), megabytes);

    public static string PruneRecordCheckoutGone(string repositoryId, string megabytes) =>
        Catalogue.Format(nameof(PruneRecordCheckoutGone), repositoryId, megabytes);

    public static string PruneRecordNeverIndexed(string repositoryId, string megabytes) =>
        Catalogue.Format(nameof(PruneRecordNeverIndexed), repositoryId, megabytes);

    public static string PruneRepositorySwept(string repositoryId, int generations, int overlays, int staleFiles, string megabytes) =>
        Catalogue.Format(nameof(PruneRepositorySwept), repositoryId, generations, overlays, staleFiles, megabytes);

    public static string PruneTelemetry(int records, string megabytes) =>
        Catalogue.Format(nameof(PruneTelemetry), records, megabytes);

    /// <summary>
    /// Not a shared CommandUnknownArgument: this is the one command in this binary whose ignored
    /// argument deleted data, so the refusal says what did not happen as well as what was not
    /// understood. The usage hole carries the correct spelling of --dry-run beside the wrong one.
    /// </summary>
    public static string PruneUnknownArgument(string argument, string usage) =>
        Catalogue.Format(nameof(PruneUnknownArgument), argument, usage);

    /// <summary>
    /// The two counts are holes because the block must not name a bound the machine does not
    /// apply: both come from the retention policy this installation actually reads. Line endings
    /// are normalised for the same reason as <see cref="PolicyUsage"/> — XML hands back LF.
    /// </summary>
    public static string PruneUsage(
        int versionsKept,
        int generationsKept,
        string runtimeRoot) =>
        Catalogue.Format(
            nameof(PruneUsage),
            versionsKept,
            generationsKept,
            runtimeRoot)
            .ReplaceLineEndings();

    public static string PruneVersions(int removed, string megabytes) =>
        Catalogue.Format(nameof(PruneVersions), removed, megabytes);

    public static string PruneVersionsNoPointer => Catalogue.Get(nameof(PruneVersionsNoPointer));

    public static string PruneVersionsPointerUnreadable(string reason) =>
        Catalogue.Format(nameof(PruneVersionsPointerUnreadable), reason);

    public static string PruneVolumeUnavailable(string worktreePath) =>
        Catalogue.Format(nameof(PruneVolumeUnavailable), worktreePath);

    public static string PruneWouldReclaim(string megabytes) =>
        Catalogue.Format(nameof(PruneWouldReclaim), megabytes);

    // What the entry point itself says: the guard's outcome, the argument refusals, and what
    // `hooks install` reports. The localai: prefix stays at the call site in the guard, where

    public static string HookEventMissing(string usage) =>
        Catalogue.Format(nameof(HookEventMissing), usage);

    public static string HookEventUnknown(string requested, string supportedEvents) =>
        Catalogue.Format(nameof(HookEventUnknown), requested, supportedEvents);

    public static string HooksChainBlocked(string hookPath, string previousPath) =>
        Catalogue.Format(nameof(HooksChainBlocked), hookPath, previousPath);

    public static string HooksChained(string suffix, string hooks) =>
        Catalogue.Format(nameof(HooksChained), suffix, hooks);

    public static string HooksExcluded => Catalogue.Get(nameof(HooksExcluded));

    public static string HooksInstalled(int hooks, string hooksDirectory) =>
        Catalogue.Format(nameof(HooksInstalled), hooks, hooksDirectory);

    public static string HooksLauncherRequired => Catalogue.Get(nameof(HooksLauncherRequired));

    public static string RepositoryOutsideGit(string directory) =>
        Catalogue.Format(nameof(RepositoryOutsideGit), directory);

    public static string RepositoryPathNotDirectory(string path) =>
        Catalogue.Format(nameof(RepositoryPathNotDirectory), path);

    public static string RunCancelled => Catalogue.Get(nameof(RunCancelled));

    public static string SyncInlineLimitInvalid => Catalogue.Get(nameof(SyncInlineLimitInvalid));

    // What `localai telemetry` reports and what `localai bootstrap --dry-run` lists. Neither
    // changes anything: one answers a question, the other names work done elsewhere.

    public static string BootstrapAlreadyConnected => Catalogue.Get(nameof(BootstrapAlreadyConnected));

    public static string BootstrapDryRun => Catalogue.Get(nameof(BootstrapDryRun));

    public static string BootstrapStepClients => Catalogue.Get(nameof(BootstrapStepClients));

    public static string BootstrapStepHooks => Catalogue.Get(nameof(BootstrapStepHooks));

    public static string BootstrapStepInitializing => Catalogue.Get(nameof(BootstrapStepInitializing));

    public static string BootstrapStepLegacyKept => Catalogue.Get(nameof(BootstrapStepLegacyKept));

    public static string BootstrapStepModels => Catalogue.Get(nameof(BootstrapStepModels));

    public static string BootstrapStepState(string repositoryId) =>
        Catalogue.Format(nameof(BootstrapStepState), repositoryId);

    public static string TelemetryByModel => Catalogue.Get(nameof(TelemetryByModel));

    public static string TelemetryByProfile => Catalogue.Get(nameof(TelemetryByProfile));

    public static string TelemetryFailureOutlier(string model, string share, int failures, int totalFailures) =>
        Catalogue.Format(nameof(TelemetryFailureOutlier), model, share, failures, totalFailures);

    public static string TelemetryNone(string runtimeRoot) =>
        Catalogue.Format(nameof(TelemetryNone), runtimeRoot);

    public static string TelemetryNoneUnreadable(string runtimeRoot) =>
        Catalogue.Format(nameof(TelemetryNoneUnreadable), runtimeRoot);

    public static string TelemetryRecorded(int attempts, string earliest, string latest) =>
        Catalogue.Format(nameof(TelemetryRecorded), attempts, earliest, latest);

    public static string TelemetrySaved(string net, string gross, string verifying) =>
        Catalogue.Format(nameof(TelemetrySaved), net, gross, verifying);

    public static string TelemetryUnreadable(int records) =>
        Catalogue.Format(nameof(TelemetryUnreadable), records);

    public static string RepositoryNotConnected(string root) =>
        Catalogue.Format(nameof(RepositoryNotConnected), root);

    public static string RepositoryState(object state, string generation) =>
        Catalogue.Format(nameof(RepositoryState), state, generation);

    public static string GenerationNone => Catalogue.Get(nameof(GenerationNone));

    // What `localai sync` says while it works. The `SYNCED` and `REFUSED` lines are not
    // here and never will be: another process parses them out of stdout, so they are a wire
    // format rather than prose, and they stay ASCII for every reader.

    /// <summary>
    /// How long the whole run took, which nothing said before. The only number a sync printed was
    /// the overlay builder's, timing its own call — 6.7 s where the run had taken 49.67 s — and a
    /// reader deciding whether there was time to rebuild before searching took it for the total
    /// (#386).
    ///
    /// Here rather than in <c>IndexText</c> because this is the CLI's account of its own
    /// orchestration; the builder is right to time only what it does. The total and nothing else:
    /// a breakdown printed once the run is over is the summary that reporting-while-it-runs
    /// exists instead of.
    /// </summary>
    public static string SyncFinished(string seconds) =>
        Catalogue.Format(nameof(SyncFinished), seconds);

    public static string SyncStoppedAfter(string seconds) =>
        Catalogue.Format(nameof(SyncStoppedAfter), seconds);

    public static string AdaptersFailed(string reasons) =>
        Catalogue.Format(nameof(AdaptersFailed), reasons);

    public static string CoverageNoCsharp => Catalogue.Get(nameof(CoverageNoCsharp));

    public static string CoverageProjectsUncovered(int uncovered, string projects) =>
        Catalogue.Format(nameof(CoverageProjectsUncovered), uncovered, projects);

    public static string EmbeddingCheckpointNotRemoved(string checkpoint, string reason) =>
        Catalogue.Format(nameof(EmbeddingCheckpointNotRemoved), checkpoint, reason);

    public static string MainlineMissing(string refs) =>
        Catalogue.Format(nameof(MainlineMissing), refs);

    public static string OverlayDegraded(string workingRoot, string reasons) =>
        Catalogue.Format(nameof(OverlayDegraded), workingRoot, reasons);

    public static string OverlayDiscarded(string worktree) =>
        Catalogue.Format(nameof(OverlayDiscarded), worktree);

    public static string RetentionRemoved(int generations, int overlays, int stagingFiles, string megabytes) =>
        Catalogue.Format(nameof(RetentionRemoved), generations, overlays, stagingFiles, megabytes);

    public static string RetentionSweepSkipped(string reason) =>
        Catalogue.Format(nameof(RetentionSweepSkipped), reason);

    public static string SemanticCheckpointNotRemoved(string checkpoint, string reason) =>
        Catalogue.Format(nameof(SemanticCheckpointNotRemoved), checkpoint, reason);

    public static string SemanticCheckpointUnusable(string checkpoint, string reason) =>
        Catalogue.Format(nameof(SemanticCheckpointUnusable), checkpoint, reason);

    public static string SemanticPhaseResumed(string checkpoint) =>
        Catalogue.Format(nameof(SemanticPhaseResumed), checkpoint);

    public static string SyncBusy(string repositoryId) =>
        Catalogue.Format(nameof(SyncBusy), repositoryId);

    public static string WorktreeGone(string worktree) =>
        Catalogue.Format(nameof(WorktreeGone), worktree);

    public static string WorktreeNotInspectable(string worktree, string reason) =>
        Catalogue.Format(nameof(WorktreeNotInspectable), worktree, reason);

    public static string WorktreeVanished(string worktree) =>
        Catalogue.Format(nameof(WorktreeVanished), worktree);

    public static string SummaryProblems(int failed, int warned) =>
        Catalogue.Format(nameof(SummaryProblems), failed, warned);

    public static string SummaryNoProblemsWithNotes(int warned) =>
        Catalogue.Format(nameof(SummaryNoProblemsWithNotes), warned);

    public static string SummaryNoProblems => Catalogue.Get(nameof(SummaryNoProblems));

    public static string JsonNotSupported => Catalogue.Get(nameof(JsonNotSupported));

    public static string CapabilitiesNeedsJson =>
        Catalogue.Get(nameof(CapabilitiesNeedsJson));

    public static string AskPromptMissing(string usage) =>
        Catalogue.Format(nameof(AskPromptMissing), usage);

    public static string ProfileUnknown(string command, string profile, string accepted) =>
        Catalogue.Format(nameof(ProfileUnknown), command, profile, accepted);

    public static string AskProfileNotSupported(string profile, string accepted) =>
        Catalogue.Format(nameof(AskProfileNotSupported), profile, accepted);

    /// <summary>
    /// A profile ask cannot run because the work belongs to a command of its own. No list of
    /// alternatives, unlike its two siblings: this reader named a profile that exists and meant
    /// it, and offering substitutes invites rerunning the log under another one, which is the
    /// defect again. The usage line carried is the command they are being sent to.
    /// </summary>
    public static string AskProfileHasOwnCommand(string profile, string usage) =>
        Catalogue.Format(nameof(AskProfileHasOwnCommand), profile, usage);

    public static string OptionValueMissing(string command, string option, string usage) =>
        Catalogue.Format(nameof(OptionValueMissing), command, option, usage);

    /// <summary>
    /// Shaped after <see cref="ProfileUnknown"/>: command, the value as typed, and the accepted
    /// set, with no usage line — the set is the whole answer. The closing clause answers "the
    /// model knows German, why will you not", which otherwise arrives as an issue.
    /// </summary>
    public static string LanguageNotSupported(
        string command,
        string language,
        string supported) =>
        Catalogue.Format(nameof(LanguageNotSupported), command, language, supported);

    public static string CommandUnknownArgument(string command, string argument, string usage) =>
        Catalogue.Format(nameof(CommandUnknownArgument), command, argument, usage);

    public static string TriageNoSource(string usage) =>
        Catalogue.Format(nameof(TriageNoSource), usage);

    public static string TriageEmptyInput => Catalogue.Get(nameof(TriageEmptyInput));

    public static string TriageOneLog(string usage) =>
        Catalogue.Format(nameof(TriageOneLog), usage);

    public static string ReadImageQuestionMissing(string usage) =>
        Catalogue.Format(nameof(ReadImageQuestionMissing), usage);

    public static string ReadImageNoImages(string usage) =>
        Catalogue.Format(nameof(ReadImageNoImages), usage);

    public static string ReadImageProfileNotSupported(string profile, string accepted) =>
        Catalogue.Format(nameof(ReadImageProfileNotSupported), profile, accepted);

    public static string TranslateLanguageMissing(string usage) =>
        Catalogue.Format(nameof(TranslateLanguageMissing), usage);

    public static string TranslateNoSource(string usage) =>
        Catalogue.Format(nameof(TranslateNoSource), usage);

    public static string TranslateEmptyInput => Catalogue.Get(nameof(TranslateEmptyInput));

    public static string TranslateOneText(string usage) =>
        Catalogue.Format(nameof(TranslateOneText), usage);

    public static string TranslateOneSource(string usage) =>
        Catalogue.Format(nameof(TranslateOneSource), usage);

    // Reading the document `--in` names, and refusing to write over it, are the same job on the
    // MCP face, so their sentences live in LocalLmText beside the reader that produces them.
    // What stays here is what names a console thing: an option, standard input, a usage line.

    public static string TranslateMarkdownAssumed(string path) =>
        Catalogue.Format(nameof(TranslateMarkdownAssumed), path);

    // Every number below is formatted invariantly by the caller before it arrives here: these
    // lines are quoted verbatim by agents and read out of redirected logs, and a decimal comma
    // would make the estimate a different number to whoever parses it.
    public static string ProgressPullPreparing(string model, int seconds) =>
        Catalogue.Format(nameof(ProgressPullPreparing), model, seconds);

    public static string ProgressPullDownloading(string model, string completed, string total) =>
        Catalogue.Format(nameof(ProgressPullDownloading), model, completed, total);

    public static string ProgressPullVerifying(int seconds) =>
        Catalogue.Format(nameof(ProgressPullVerifying), seconds);

    public static string ProgressPullStoring(int seconds) =>
        Catalogue.Format(nameof(ProgressPullStoring), seconds);

    public static string ProgressPullStatus(string status, int seconds) =>
        Catalogue.Format(nameof(ProgressPullStatus), status, seconds);

    public static string ProgressQueued(int seconds) =>
        Catalogue.Format(nameof(ProgressQueued), seconds);

    public static string ProgressRunning(int seconds) =>
        Catalogue.Format(nameof(ProgressRunning), seconds);

    /// <param name="images">
    /// What the one call carries. There is nothing to count inside it, so this is the size of
    /// what was sent rather than a position in it — the fact that says whether forty seconds is
    /// ordinary.
    /// </param>
    public static string ProgressRunningImages(int images, int seconds) =>
        Catalogue.Format(nameof(ProgressRunningImages), images, seconds);

    public static string ProgressRunningFiles(int files, int seconds) =>
        Catalogue.Format(nameof(ProgressRunningFiles), files, seconds);

    public static string ProgressTranslatingFragment(int index, int total, string left) =>
        Catalogue.Format(nameof(ProgressTranslatingFragment), index, total, left);

    public static string ProgressMinutesLeft(string minutes) =>
        Catalogue.Format(nameof(ProgressMinutesLeft), minutes);

    public static string ProgressTranslationRetry(string detail, string model) =>
        Catalogue.Format(nameof(ProgressTranslationRetry), detail, model);

    public static string ProgressTriageChoosingModel =>
        Catalogue.Get(nameof(ProgressTriageChoosingModel));

    /// <param name="percent">
    /// How much of the source the fragments already analysed covered. Whole, rounded down, so a
    /// run with one fragment left says 99 rather than a hundred it has not reached.
    /// </param>
    /// <param name="left">
    /// <see cref="ProgressMinutesLeft"/>, or empty on the first fragment, where nothing has been
    /// measured yet.
    /// </param>
    public static string ProgressTriageFragment(int index, int percent, string left) =>
        Catalogue.Format(nameof(ProgressTriageFragment), index, percent, left);

    public static string ProgressTriageLogRead(int fragments) =>
        Catalogue.Format(nameof(ProgressTriageLogRead), fragments);

    /// <param name="parts">
    /// How many partial answers this merge carries. Renamed from the triage-only form when the
    /// reducer gained a second caller: reading images one at a time ends in the same merge.
    /// </param>
    public static string ProgressMerging(int parts, int level) =>
        Catalogue.Format(nameof(ProgressMerging), parts, level);

    /// <param name="left">
    /// <see cref="ProgressMinutesLeft"/>, or empty on the first image, where nothing has been
    /// measured yet.
    /// </param>
    public static string ProgressReadingImage(int index, int total, string left) =>
        Catalogue.Format(nameof(ProgressReadingImage), index, total, left);

    public static string TranslateWriteDocument => Catalogue.Get(nameof(TranslateWriteDocument));

    public static string OutputNotWritten(string path, string reason) =>
        Catalogue.Format(nameof(OutputNotWritten), path, reason);

    public static string RepoStatusRootWithoutDirectory => Catalogue.Get(nameof(RepoStatusRootWithoutDirectory));

    public static string RepoStatusTwoRepositories => Catalogue.Get(nameof(RepoStatusTwoRepositories));

    public static string RepoStatusUnknownArgument(string argument, string usage) =>
        Catalogue.Format(nameof(RepoStatusUnknownArgument), argument, usage);

    public static string RepoStatusNotConfigured(string commonDirectory) =>
        Catalogue.Format(nameof(RepoStatusNotConfigured), commonDirectory);

    public static string PolicyMaxEmbeddingBatch(int items) =>
        Catalogue.Format(nameof(PolicyMaxEmbeddingBatch), items);

    public static string PolicyMaxEmbeddingBatchInvalid(string value, int minimum, int maximum) =>
        Catalogue.Format(nameof(PolicyMaxEmbeddingBatchInvalid), value, minimum, maximum);

    public static string RepoStatusIntervalWithoutSeconds =>
        Catalogue.Get(nameof(RepoStatusIntervalWithoutSeconds));

    public static string RepoStatusIntervalWithoutFollow =>
        Catalogue.Get(nameof(RepoStatusIntervalWithoutFollow));

    public static string RepoStatusIntervalNotACadence(string value) =>
        Catalogue.Format(nameof(RepoStatusIntervalNotACadence), value);

    public static string RepoFollowNoBuild => Catalogue.Get(nameof(RepoFollowNoBuild));

    public static string RepoFollowCompleted(string chunks) =>
        Catalogue.Format(nameof(RepoFollowCompleted), chunks);

    public static string RepoFollowCompletedPlain =>
        Catalogue.Get(nameof(RepoFollowCompletedPlain));

    public static string RepoFollowWorking(string phase, string step) =>
        Catalogue.Format(nameof(RepoFollowWorking), phase, step);

    public static string RepoFollowStepProgress(
        string phase,
        string step,
        string done,
        string total) =>
        Catalogue.Format(nameof(RepoFollowStepProgress), phase, step, done, total);

    public static string RepoFollowFailed(string workingRoot) =>
        Catalogue.Format(nameof(RepoFollowFailed), workingRoot);

    public static string RepoFollowStopped(string phase, string seconds) =>
        Catalogue.Format(nameof(RepoFollowStopped), phase, seconds);

    public static string RepoFollowProgress(
        string phase,
        string done,
        string total,
        string perSecond,
        string etaSeconds) =>
        Catalogue.Format(
            nameof(RepoFollowProgress), phase, done, total, perSecond, etaSeconds);

    public static string SemanticCommandFailed(string operation, string reason) =>
        Catalogue.Format(nameof(SemanticCommandFailed), operation, reason);

    public static string PathNotRelative(string path) =>
        Catalogue.Format(nameof(PathNotRelative), path);

    public static string PositionNotFromOne(int line, int column) =>
        Catalogue.Format(nameof(PositionNotFromOne), line, column);

    public static string SemanticOperationUnknown(string operation) =>
        Catalogue.Format(nameof(SemanticOperationUnknown), operation);

    public static string NoCurrentGeneration => Catalogue.Get(nameof(NoCurrentGeneration));

    public static string SemanticEvaluateNoSuite(string path) =>
        Catalogue.Format(nameof(SemanticEvaluateNoSuite), path);

    public static string NativeOperationUnknown(string operation, string operations) =>
        Catalogue.Format(nameof(NativeOperationUnknown), operation, operations);

    /// <summary>
    /// Said to the one person who ever reads this command's own output: somebody who found the
    /// registered line in <c>settings.json</c> and ran it to see what it does. The refusal the
    /// gate answers a client with is not in this catalogue at all — see
    /// <c>TextSearchRefusal</c> for why it is English on every machine.
    /// </summary>
    public static string AgentGateNeedsPayload => Catalogue.Get(nameof(AgentGateNeedsPayload));

    public static string RepoPruneRemoved(string branch, string mainline, string head, string worktree) =>
        Catalogue.Format(nameof(RepoPruneRemoved), branch, mainline, head, worktree);

    public static string RepoPruneRemovedNoWorktree(string branch, string mainline, string head) =>
        Catalogue.Format(nameof(RepoPruneRemovedNoWorktree), branch, mainline, head);

    public static string RepoPruneWouldRemove(string branch, string mainline, string worktree) =>
        Catalogue.Format(nameof(RepoPruneWouldRemove), branch, mainline, worktree);

    public static string RepoPruneWouldRemoveNoWorktree(string branch, string mainline) =>
        Catalogue.Format(nameof(RepoPruneWouldRemoveNoWorktree), branch, mainline);

    public static string RepoPruneKeptCurrent(string branch) =>
        Catalogue.Format(nameof(RepoPruneKeptCurrent), branch);

    public static string RepoPruneKeptNoCommits(string branch) =>
        Catalogue.Format(nameof(RepoPruneKeptNoCommits), branch);

    public static string RepoPruneKeptUnmerged(string branch, string commits, string mainline) =>
        Catalogue.Format(nameof(RepoPruneKeptUnmerged), branch, commits, mainline);

    public static string RepoPruneKeptDirty(string branch, string worktree) =>
        Catalogue.Format(nameof(RepoPruneKeptDirty), branch, worktree);

    public static string RepoPruneKeptLocked(string branch, string worktree) =>
        Catalogue.Format(nameof(RepoPruneKeptLocked), branch, worktree);

    public static string RepoPruneKeptWorktreeUnreadable(string branch, string worktree, string reason) =>
        Catalogue.Format(nameof(RepoPruneKeptWorktreeUnreadable), branch, worktree, reason);

    public static string RepoPruneKeptUnanswerable(string branch, string mainline, string reason) =>
        Catalogue.Format(nameof(RepoPruneKeptUnanswerable), branch, mainline, reason);

    public static string RepoPruneWorktreeRefused(string branch, string worktree, string reason) =>
        Catalogue.Format(nameof(RepoPruneWorktreeRefused), branch, worktree, reason);

    public static string RepoPruneBranchRefused(string branch, string reason) =>
        Catalogue.Format(nameof(RepoPruneBranchRefused), branch, reason);

    public static string RepoPruneSummary(int branches, int worktrees) =>
        Catalogue.Format(nameof(RepoPruneSummary), branches, worktrees);

    public static string RepoPruneWouldSummary(int branches, int worktrees) =>
        Catalogue.Format(nameof(RepoPruneWouldSummary), branches, worktrees);

    public static string RepoPruneNothing(string mainline) =>
        Catalogue.Format(nameof(RepoPruneNothing), mainline);

    public static string RepoPruneMainlineMissing(string candidates) =>
        Catalogue.Format(nameof(RepoPruneMainlineMissing), candidates);

    public static string RepoPruneUnknownArgument(string argument, string usage) =>
        Catalogue.Format(nameof(RepoPruneUnknownArgument), argument, usage);

    public static string RepoPruneDryRun => Catalogue.Get(nameof(RepoPruneDryRun));

    public static string RepoPruneAutomationOffer => Catalogue.Get(nameof(RepoPruneAutomationOffer));

    public static string RepoPruneRootWithoutDirectory => Catalogue.Get(nameof(RepoPruneRootWithoutDirectory));

    public static string RepoPruneUsage => Catalogue.Get(nameof(RepoPruneUsage));

    public static string AgentWorktreeCreated(string path, string branch) =>
        Catalogue.Format(nameof(AgentWorktreeCreated), path, branch);

    public static string AgentWorktreeBranchElsewhere(string branch, string existing, string target) =>
        Catalogue.Format(nameof(AgentWorktreeBranchElsewhere), branch, existing, target);

    public static string AgentWorktreeBranchInMainCheckout(string branch, string checkout) =>
        Catalogue.Format(nameof(AgentWorktreeBranchInMainCheckout), branch, checkout);

    public static string AgentWorktreePathHoldsAnotherBranch(string path, string branch) =>
        Catalogue.Format(nameof(AgentWorktreePathHoldsAnotherBranch), path, branch);

    public static string AgentWorktreeRootUnknown(string repository) =>
        Catalogue.Format(nameof(AgentWorktreeRootUnknown), repository);

    public static string AgentWorktreeRootNotWritable(string directory, string reason) =>
        Catalogue.Format(nameof(AgentWorktreeRootNotWritable), directory, reason);

    public static string AgentWorktreeOperationUnknown(string operation, string usage) =>
        Catalogue.Format(nameof(AgentWorktreeOperationUnknown), operation, usage);

    public static string AgentWorktreeBranchMissing(string usage) =>
        Catalogue.Format(nameof(AgentWorktreeBranchMissing), usage);

    public static string AgentWorktreePathMissing(string usage) =>
        Catalogue.Format(nameof(AgentWorktreePathMissing), usage);

    public static string AgentWorktreePayloadFieldMissing(string field) =>
        Catalogue.Format(nameof(AgentWorktreePayloadFieldMissing), field);

    public static string AgentWorktreeBranchNotUsable(string branch) =>
        Catalogue.Format(nameof(AgentWorktreeBranchNotUsable), branch);

    public static string AgentWorktreePathOccupied(string path) =>
        Catalogue.Format(nameof(AgentWorktreePathOccupied), path);

    public static string AgentWorktreeGitRefused(string reason) =>
        Catalogue.Format(nameof(AgentWorktreeGitRefused), reason);

    public static string AgentWorktreeNotAWorktree(string path) =>
        Catalogue.Format(nameof(AgentWorktreeNotAWorktree), path);

    public static string AgentWorktreeHoldsWork(string path) =>
        Catalogue.Format(nameof(AgentWorktreeHoldsWork), path);

    public static string AgentWorktreeRemoved(string path) =>
        Catalogue.Format(nameof(AgentWorktreeRemoved), path);
}
