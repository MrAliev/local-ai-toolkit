using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Indexing;
using LocalAi.Repository;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalAi.Cli;

/// <summary>
/// Another sync already holds this repository's gate. A named outcome, not a failure: the
/// other run is doing the same work, its phase and ETA are in the progress file, and the
/// caller's next commit will sync again anyway. Hooks and MCP callers must not queue for
/// the minutes a full generation build can take.
/// </summary>
public sealed class RepositorySyncBusyException(string repositoryId) : Exception(
    CliText.SyncBusy(repositoryId))
{
    public string RepositoryId { get; } = repositoryId;
}

public sealed record CodeSearchSyncResult(
    string RepositoryId,
    string GenerationId,
    string BaseIndexPath,
    int OverlaysBuilt,
    bool GenerationChanged,
    // Reported rather than counted silently: fewer overlays than worktrees is otherwise
    // indistinguishable from a sync that decided they were all up to date.
    int WorktreesSkipped = 0,
    // Non-null when the run refused rather than built: the work was over the caller's inline
    // limit, and the number is the files it would have had to re-read.
    int? RefusedFiles = null,
    // Already formatted to one decimal, invariantly, because it goes on the wire. Null on the
    // paths that return before any work: a refusal and a busy gate did nothing, and a duration
    // beside them would imply they did.
    string? ElapsedSeconds = null);

public static class CodeSearchSyncCommand
{
    public const string DefaultModel = "qwen3-embedding:8b-q8_0";
    public const int DefaultDimension = 4096;
    public const int CurrentNormalizationVersion = 4;

    /// <summary>
    /// Version 2 cuts adapter-covered files on their definitions instead of on a line window.
    /// Version 3 also cuts on the declarations an adapter names without reporting a body span.
    /// </summary>
    /// <remarks>
    /// Chunk boundaries are part of the generation identity, so this is not a migration: the
    /// first sync after the upgrade rebuilds every repository's base generation from scratch.
    /// For a corpus the size of IntelWash that is roughly 26 000 chunks, on the order of an hour
    /// at the rate this machine embeds — a deliberate rebuild, announced in the release notes,
    /// and it must read as one in `index_status` rather than as drift.
    /// </remarks>
    public const int CurrentChunkFormatVersion = 3;

    // Bump whenever semantic extraction changes even if the SIDX binary format does not.
    // Generations are immutable, so changing relationships without changing this value
    // would keep serving the previous semantic graph for an already indexed commit.
    public const int CurrentSemanticGenerationVersion = 11;

    // Long enough to ride out another run's short publish transaction, far too short to
    // queue behind its embedding phase: a blocked caller exits with the named busy outcome.
    private static readonly TimeSpan SyncGateWaitBudget = TimeSpan.FromSeconds(5);

    internal sealed record SemanticBuildResult(
        SemanticIndex Index,
        IReadOnlyList<SemanticAdapterStatus> AdapterStatuses,
        IReadOnlyList<string>? UncoveredProjects = null);

    /// <param name="runtimeRoot">
    /// The installation this sync publishes into and reads its policy from. Null means the
    /// machine's own, which is the only value production uses; naming one is what lets a test
    /// run a whole sync without touching the real runtime.
    /// </param>
    /// <param name="embedder">
    /// Null means the broker, which is the only value production uses; naming one is what lets a
    /// test run a sync to its end without an embedding backend.
    /// </param>
    public static async Task<CodeSearchSyncResult> ExecuteAsync(
        string workingRoot,
        TextWriter error,
        string model = DefaultModel,
        CancellationToken cancellationToken = default,
        bool includeOverlays = true,
        string? runtimeRoot = null,
        bool requireSemantics = false,
        int? refuseInlineOverFiles = null,
        IEmbeddingClient? embedder = null,
        bool allWorktrees = false)
    {
        // Before the gate, so the figure is what the caller waited rather than what the work
        // took: a run that spent four minutes queued behind another one waited four minutes.
        var elapsed = Stopwatch.StartNew();
        var requested = RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot);
        var batchProfiles = new EmbeddingBatchProfileStore(
            runtimeRoot ?? RuntimeIndexLayout.DefaultRuntimeRoot);
        // What this machine allows, read beside the profile it keeps: the profile is what
        // was proved here, the ceiling is what may be attempted here at all.
        var maxBatchItems = new ModelResidencyPolicyStore(
                runtimeRoot ?? RuntimeIndexLayout.DefaultRuntimeRoot)
            .Read()
            .MaxEmbeddingBatchItems;
        // The gate comes before the first write of any shared state — progress included.
        // Everything below (progress, manifest, generation directories, checkpoints, the
        // current pointer, prune) is single-writer while the lease is held, which is what
        // lets the failure path stamp Failed without asking whose progress it stamps (#199).
        using var syncLease = RepositorySyncGate.TryAcquire(
            requested.RepositoryId,
            SyncGateWaitBudget,
            cancellationToken)
            ?? throw new RepositorySyncBusyException(requested.RepositoryId);
        var progressStore = new RepositoryIndexProgressStore(
            requested.RepositoryRuntimeRoot);
        var manifestStore = new RepositoryManifestStore(
            requested.RepositoryRuntimeRoot);
        error = TextWriter.Synchronized(error);
        using var reporter = new SyncProgressReporter(progressStore.Save, error);
        var lastProgress = new RepositoryIndexProgress(
            requested.RepositoryId,
            RepositoryIndexProgressPhase.Planning,
            requested.WorkingRoot.Value,
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            Pause: null,
            // Planning is one piece of work — listing what is in the tree and hashing every
            // candidate — and it is the first thing a reader ever sees said about a build.
            Step: IndexBuildStep.ReadingRepository);
        reporter.Report(lastProgress);
        // Every embedding record this run writes, where a follow loop sees one an interval (#774).
        var embedded = EmbeddedChunks.None;

        // Before the lease does any work, and long before the semantic phase: this is the only
        // point where declining is free. Scanning and hashing the tree costs seconds and needs
        // no model; everything after it — Roslyn loading the solution, the SCIP adapters, the
        // embedding — is what a bounded caller cannot afford to have started.
        //
        // Counted in files because chunk counts do not exist yet: C# is cut on the definitions
        // the semantic phase produces, so asking for them here would mean paying for it.
        if (refuseInlineOverFiles is { } fileLimit)
        {
            var currentBase = new GenerationStore(requested.RepositoryRuntimeRoot).ReadCurrent();
            var changedFiles = IndexBuilder.CountChangedFiles(
                requested.WorkingRoot.Value,
                currentBase is null
                    ? null
                    : new GenerationStore(requested.RepositoryRuntimeRoot)
                        .IndexPath(currentBase.GenerationId));
            if (changedFiles > fileLimit)
            {
                // Planning wrote a record before the count was known. Nothing ran, so nothing is
                // owed a progress line: left behind, it reads to every later caller as a build
                // that stopped part-way, and the installed instructions tell an agent to relay
                // that as a build needing to be started again.
                progressStore.Clear();
                return new CodeSearchSyncResult(
                    requested.RepositoryId,
                    string.Empty,
                    string.Empty,
                    0,
                    false,
                    0,
                    changedFiles);
            }
        }

        void ReportProgress(
            RepositoryIndexProgressPhase phase,
            string root,
            IndexBuildProgress progress)
        {
            lastProgress = new RepositoryIndexProgress(
                requested.RepositoryId,
                phase,
                root,
                progress.ProcessedChunks,
                progress.TotalChunks,
                progress.ChunksPerSecond,
                progress.EstimatedRemaining,
                DateTimeOffset.UtcNow,
                progress.Pause);
            embedded = embedded.After(lastProgress);
            reporter.Report(lastProgress);
        }

        // Phases that do not count chunks still have to be announced, or the last embedding
        // report stands as the whole story for as long as they run. The counters are cleared with
        // the phase rather than carried into it: a stale "1415/1415" is worse than no number.
        void ReportPhase(RepositoryIndexProgressPhase phase, string root) =>
            ReportStep(phase, root, step: null, done: 0, total: 0);

        // Which piece of work inside the phase, for the phases whose chunk counters mean nothing.
        // One body for both, because they differ in one field and two bodies are two places to
        // forget the next one; and one write rather than a phase report followed by a step report,
        // which would leave the record, for the moment between them, saying the phase had no step
        // — which is what a reader is told by a build too old to have one.
        void ReportStep(
            RepositoryIndexProgressPhase phase,
            string root,
            IndexBuildStep? step,
            int done,
            int total)
        {
            lastProgress = lastProgress with
            {
                Phase = phase,
                WorkingRoot = root,
                ProcessedChunks = 0,
                TotalChunks = 0,
                ChunksPerSecond = 0,
                EstimatedRemaining = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                // Reaching a new phase is the build moving, so whatever it was waiting for is
                // over. Carrying the pause across would leave a reader told the build is waiting
                // for a backend it has already been answered by.
                Pause = null,
                Step = step,
                StepDone = done,
                StepTotal = total,
            };
            reporter.Report(lastProgress);
        }

        // A step that is still running, said by moving the timestamp and nothing else.
        //
        // This used to be a second call to ReportPhase, which was harmless while a record carried
        // nothing but a phase name: recording the same phase again moved only the timestamp. With
        // a step in the record it would erase, on every heartbeat line the console prints, the
        // step that line is about — so a reader would watch `analysed 12/26 projects` flicker
        // back to `no step recorded` every few seconds.
        void Touch()
        {
            lastProgress = lastProgress with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            reporter.Report(lastProgress);
        }

        try
        {
            var worktrees = ReadWorktrees(requested.WorkingRoot.Value);
            var mainline = ResolveMainline(
                requested,
                manifestStore.Read()?.DevRef);
            var generationIdentity = new GenerationIdentity(
                requested.RepositoryId,
                mainline.Identity.HeadCommit,
                mainline.Identity.HeadTree,
                model,
                DefaultDimension,
                CurrentChunkFormatVersion,
                CodeIndex.CurrentVersion,
                CurrentNormalizationVersion,
                1,
                CurrentSemanticGenerationVersion);
            var commonDirectory = RepositoryIdentity.FromCommonDirectory(
                RepoLocator.GitOutputOrThrow(
                    requested.WorkingRoot.Value,
                    "rev-parse --path-format=absolute --git-common-dir",
                    "The git common directory"))
                .CommonDirectory;
            manifestStore.Save(new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                null,
                null,
                model,
                DefaultDimension,
                CurrentChunkFormatVersion,
                CodeIndex.CurrentVersion,
                RepositoryIndexState.Initializing,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(item.Path, item.Head, item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow));
            var store = new GenerationStore(requested.RepositoryRuntimeRoot);
            var current = store.ReadCurrent();
            var requestedCurrent = WorktreeGenerationPublications.ReadCurrent(requested);
            var reusableCurrent = !includeOverlays || allWorktrees ? current : requestedCurrent ?? current;
            var generationChanged = !string.Equals(
                reusableCurrent?.GenerationId,
                generationIdentity.Id,
                StringComparison.Ordinal);

            var generation = TryReadManifest(store, generationIdentity.Id);
            error.WriteLine($"SYNC_BASE generation={generationIdentity.Id} action={(generation is null ? "build" : "reuse")}");
            if (generation is null)
            {
                // A generation that exists and cannot be read has to be moved out of the way
                // before it can be rebuilt: PublishIndex returns the existing manifest when the
                // directory is there, which means reading it again and failing the same way.
                store.QuarantineUnreadable(generationIdentity.Id);
                generation = await BuildGenerationAsync(
                    store,
                    reusableCurrent,
                    generationIdentity,
                    mainline.Identity,
                    progress => ReportProgress(
                        RepositoryIndexProgressPhase.EmbeddingBase,
                        mainline.Identity.WorkingRoot.Value,
                        progress),
                    phase => ReportPhase(phase, mainline.Identity.WorkingRoot.Value),
                    (phase, step, done, total) => ReportStep(
                        phase,
                        mainline.Identity.WorkingRoot.Value,
                        step,
                        done,
                        total),
                    Touch,
                    batchProfiles,
                    maxBatchItems,
                    runtimeRoot,
                    requireSemantics,
                    error,
                    embedder,
                    cancellationToken);
            }

            // Replacing the shared base must not turn one worktree's refresh into maintenance
            // of every linked tree. Other trees can refresh against the new base on demand.
            var targets = includeOverlays
                ? allWorktrees
                    ? worktrees
                    : worktrees.Where(
                        item => FsPath.From(item.Path) == requested.WorkingRoot).ToArray()
                : [];
            var overlaysBuilt = 0;
            var present = SelectPresentWorktrees(
                targets.Where(item => !item.IsPrunable),
                path => error.WriteLine(CliText.WorktreeGone(path)));
            var worktreesSkipped = present.Skipped;
            var orderedWorktrees = present.Worktrees.OrderBy(item =>
                FsPath.From(item.Path) == requested.WorkingRoot ? 0 : 1).ToArray();
            error.WriteLine($"SYNC_PLAN requested={JsonSerializer.Serialize(requested.WorkingRoot.Value)} " +
                $"generation={generation.Identity.Id} pending={JsonSerializer.Serialize(orderedWorktrees.Select(item => item.Path))} " +
                $"scope={(!includeOverlays ? "base-only" : allWorktrees ? "all-worktrees" : "requested-worktree")} worktrees={orderedWorktrees.Length}");
            var activeReady = false;
            var maintenanceIncomplete = false;
            void PublishReady(WorkingIndexIdentity identity)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isRequested = identity.WorkingRoot == requested.WorkingRoot;
                WorkingIndexIdentity latest;
                try
                {
                    latest = RuntimeIndexLayout.Inspect(identity.WorkingRoot.Value, runtimeRoot);
                }
                catch (Exception exception) when (!isRequested &&
                    !Directory.Exists(identity.WorkingRoot.Value) &&
                    exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine(CliText.WorktreeVanished(identity.WorkingRoot.Value));
                    worktreesSkipped++;
                    return;
                }
                if (latest != identity)
                {
                    if (isRequested)
                        throw new InvalidOperationException("worktree_changed: refusing to publish a changed working tree.");
                    error.WriteLine($"WORKTREE_SKIPPED root={JsonSerializer.Serialize(identity.WorkingRoot.Value)} reason=changed");
                    worktreesSkipped++;
                    maintenanceIncomplete = true;
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var published = WorktreeGenerationPublications.Publish(identity, generation, current);
                if (published)
                    error.WriteLine($"WORKTREE_READY root={JsonSerializer.Serialize(identity.WorkingRoot.Value)} " +
                        $"generation={generation.Identity.Id}");
                if (isRequested)
                {
                    activeReady = true;
                    if (published)
                        error.WriteLine($"ACTIVE_READY root={JsonSerializer.Serialize(identity.WorkingRoot.Value)} " +
                            $"generation={generation.Identity.Id} seconds={Seconds(elapsed)} maintenance=pending");
                }
            }
            foreach (var worktree in orderedWorktrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkingIndexIdentity identity;
                try
                {
                    identity = RuntimeIndexLayout.Inspect(worktree.Path, runtimeRoot);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException or
                        UnauthorizedAccessException &&
                    !Directory.Exists(worktree.Path))
                {
                    error.WriteLine(
                        CliText.WorktreeVanished(worktree.Path));
                    worktreesSkipped++;
                    continue;
                }

                if (string.Equals(
                        identity.HeadTree,
                        generation.Identity.DevTree,
                        StringComparison.Ordinal) &&
                    identity.DirtyHash is null)
                {
                    error.WriteLine($"WORKTREE_PLAN root={JsonSerializer.Serialize(identity.WorkingRoot.Value)} corpus=base semantics=base");
                    PublishReady(identity);
                    continue;
                }

                var overlayPath = RuntimeIndexLayout.OverlayPath(
                    identity,
                    generation.Identity.Id);
                var semanticOverlayPath = RuntimeIndexLayout.SemanticOverlayPath(
                    identity,
                    generation.Identity.Id);
                var builtOverlay = false;
                var semanticOverlayCurrent = IsCurrentSemanticOverlay(
                    semanticOverlayPath, identity, generation.Identity);
                var corpusOverlayPresent = File.Exists(overlayPath);
                error.WriteLine($"WORKTREE_PLAN root={JsonSerializer.Serialize(identity.WorkingRoot.Value)} " +
                    $"corpus={(corpusOverlayPresent ? "reuse" : "build")} semantics={(semanticOverlayCurrent ? "reuse" : "build")}");

                // Semantics first here too, and for the second of the two reasons the base
                // generation does it: a file changed on a branch has to be cut on the same
                // boundaries as the same file in the base generation. Built the other way round,
                // a branch would re-chunk by line window everything it touched, and the shape of
                // a hit would depend on whether the file happened to be in an overlay.
                SemanticIndex? worktreeSemantics = null;
                // Kept for the definition catalogue below, which needs the same base to
                // materialise against. Reading it twice is a second parse of a file that is
                // 16.1 MB on this repository and larger on the tree that reported #519.
                SemanticIndex? baseSemantics = null;
                if (!semanticOverlayCurrent)
                {
                    ReportPhase(
                        RepositoryIndexProgressPhase.SemanticOverlay,
                        identity.WorkingRoot.Value);
                    // How wide this has to be, decided before anything is opened. Nothing at
                    // all means it could not be narrowed -- a language whose declarations
                    // cannot be read, a file outside every project, an earlier version git
                    // could not produce -- and then the whole solution is reopened as before.
                    var baseSemanticIndex = SemanticIndex.Load(
                        store.SemanticIndexPath(generation.Identity.Id));
                    baseSemantics = baseSemanticIndex;
                    // A save laid over the overlay of the commit it was made against, where that
                    // commit has one; otherwise the whole worktree measured against the base
                    // generation, which on a branch a hundred commits ahead means recomputing a
                    // hundred files that are already written down beside it (#519).
                    var layered = PlanLayeredSemanticRebuild(
                        identity.WorkingRoot.Value,
                        identity.HeadTree,
                        semanticOverlayPath,
                        baseSemanticIndex);
                    var plan = layered?.Plan ?? PlanScopedSemanticRebuild(
                        identity.WorkingRoot.Value,
                        generation.Identity.DevTree);
                    void SemanticStep(IndexBuildStep step, int done, int total) =>
                        ReportStep(
                            RepositoryIndexProgressPhase.SemanticOverlay,
                            identity.WorkingRoot.Value,
                            step,
                            done,
                            total);
                    var semanticBuild = plan is null
                        ? await BuildSemanticIndexAsync(
                            identity.WorkingRoot.Value,
                            identity,
                            generation.Identity,
                            runtimeRoot,
                            error,
                            Touch,
                            SemanticStep,
                            cancellationToken)
                        : await BuildScopedSemanticIndexAsync(
                            identity.WorkingRoot.Value,
                            plan,
                            identity,
                            generation.Identity,
                            error,
                            Touch,
                            SemanticStep,
                            cancellationToken);

                    // A failed adapter aborts the base generation and deliberately does not
                    // abort this. The base is immutable: boundaries cut by the window because
                    // an indexer was broken would be frozen into a generation that is never
                    // rebuilt while the tree stands still. An overlay is not — it is rebuilt
                    // whenever the branch moves or a file changes, so the degradation lasts as
                    // long as the breakage does. And the alternative is worse in both
                    // directions: refusing the overlay fails the post-commit hook on every
                    // commit until someone repairs a Node package, and answering from the base
                    // alone would answer about code the branch has already changed.
                    //
                    // It is a degradation, not a non-event, so it is said out loud rather than
                    // left to be inferred from the adapter line above.
                    WarnDegradedSemanticOverlay(
                        semanticBuild.AdapterStatuses,
                        identity.WorkingRoot.Value,
                        error);
                    // What the narrow rebuild actually looked at: the documents it produced, and
                    // the paths it was asked about, so a file that is gone is recognised as gone
                    // rather than left behind by an index that no longer mentions it.
                    var recomputed = plan is null
                        ? []
                        : semanticBuild.Index.Documents
                            .Select(document => document.RelPath)
                            .Concat(plan.ChangedPaths);
                    ReportStep(RepositoryIndexProgressPhase.SemanticOverlay,
                        identity.WorkingRoot.Value, IndexBuildStep.CopyingGeneration, 0, 0);
                    var semanticOverlay = plan is null
                        // The dirty paths this layer can see, not every dirty path: feeding it
                        // the corpus list would record a saved `.md` as a document this overlay
                        // speaks for, which it does not (#385).
                        ? SemanticIndexOverlay.Create(
                            baseSemanticIndex,
                            semanticBuild.Index,
                            RuntimeIndexLayout.GetDirtyPaths(identity.WorkingRoot.Value)
                                .Where(SemanticCorpusPolicy.ChangesSemantics))
                        : layered is null
                            ? SemanticIndexOverlay.CreateScoped(
                                baseSemanticIndex,
                                semanticBuild.Index,
                                recomputed)
                            // Everything the save did not touch comes from the commit's own
                            // overlay rather than from the base, which is what stops the branch's
                            // other files being recomputed for a save that never reached them.
                            : SemanticIndexOverlay.Layer(
                                layered.Committed,
                                baseSemanticIndex,
                                semanticBuild.Index,
                                recomputed);
                    semanticOverlay.Save(semanticOverlayPath);
                    // Which route this took, said out loud. The corpus layer prints the same
                    // accounting a few lines below, and until now the semantic layer printed
                    // nothing a reader could tell the two routes apart by.
                    error.WriteLine(layered is null
                        ? IndexText.SemanticOverlayFromBase(
                            semanticOverlay.Changed.Documents.Count)
                        // What came across is a subtraction, not a count of its own: the rebuild
                        // writes a document for every file in every project Roslyn opened, which
                        // is the plan's projects and everything they reference, so its own count
                        // is not the number of files it was asked about.
                        : IndexText.SemanticOverlayLayered(
                            layered.Plan.ChangedPaths.Count,
                            semanticOverlay.Changed.Documents.Count -
                            semanticBuild.Index.Documents.Count));
                    // Only a rebuild that looked at the whole worktree can stand in for it. A
                    // narrowed one holds the definitions of the projects it opened and no others,
                    // and the corpus overlay below cuts every changed file by these -- so a file
                    // outside the plan would be chunked by line window while the same file in the
                    // base generation is chunked by symbol. Left null, it is materialised from the
                    // overlay just written, which speaks for all of them.
                    worktreeSemantics = plan is null ? semanticBuild.Index : null;
                    builtOverlay = true;
                }

                if (!corpusOverlayPresent)
                {
                    ReportStep(RepositoryIndexProgressPhase.EmbeddingOverlay,
                        identity.WorkingRoot.Value, IndexBuildStep.ReadingRepository, 0, 0);
                    // Only now, and only if the corpus overlay is actually being built: an
                    // up-to-date semantic overlay still has to be materialised against the base
                    // index to answer "what are this worktree's definitions", and that reads the
                    // whole base SIDX. Doing it unconditionally would put that cost on every
                    // sync that has nothing to do.
                    var definitions = SymbolDefinitionCatalog.FromSemanticIndex(
                        worktreeSemantics ?? SemanticIndexOverlay
                            .Load(semanticOverlayPath)
                            .Materialize(baseSemantics ?? SemanticIndex.Load(
                                store.SemanticIndexPath(generation.Identity.Id))));
                    var embeddingCheckpointPath = overlayPath + ".embedding-checkpoint";
                    var builder = new IndexBuilder(
                        embedder ?? new BrokerEmbeddingClient(
                            generation.Identity.EmbeddingModel,
                            BrokerClientFactory.CreateDefault()),
                        error.WriteLine,
                        progress => ReportProgress(
                            RepositoryIndexProgressPhase.EmbeddingOverlay,
                            identity.WorkingRoot.Value,
                            progress),
                        definitions,
                        batchProfiles: batchProfiles,
                        maxEmbeddingBatchItems: maxBatchItems);
                    await builder.BuildOverlayAsync(
                        identity.WorkingRoot.Value,
                        store.IndexPath(generation.Identity.Id),
                        overlayPath,
                        cancellationToken,
                        new IndexBuildContext(
                            identity.WorkingRoot.Value,
                            identity.HeadCommit,
                            identity.HeadTree,
                            identity.RepositoryId,
                            generation.Identity.Id,
                            identity.DirtyHash),
                        embeddingCheckpointPath,
                        generation.Identity.EmbeddingDimension);
                    DeleteEmbeddingCheckpoint(embeddingCheckpointPath, error);
                    builtOverlay = true;
                }

                if (builtOverlay)
                {
                    if (DiscardDriftedOverlay(
                            identity,
                            overlayPath,
                            semanticOverlayPath,
                            runtimeRoot,
                            error))
                    {
                        worktreesSkipped++;
                        maintenanceIncomplete |= Directory.Exists(identity.WorkingRoot.Value);
                        continue;
                    }
                    else
                    {
                        overlaysBuilt++;
                    }
                }
                PublishReady(identity);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (includeOverlays && !activeReady)
                throw new InvalidOperationException("active_worktree_not_ready: the previous repository generation was preserved.");
            if (maintenanceIncomplete)
                throw new InvalidOperationException("worktree_changed: maintenance is incomplete; the previous repository generation was preserved.");
            ReportStep(
                RepositoryIndexProgressPhase.Publishing,
                requested.WorkingRoot.Value,
                IndexBuildStep.SwitchingOver,
                0,
                0);
            // A global epoch change invalidates every per-worktree publication. A scoped
            // refresh therefore publishes its own readiness without moving that epoch while
            // other live trees still depend on it. Full maintenance retains its old contract.
            var advanceGlobal = current is null || !includeOverlays || allWorktrees ||
                worktrees.Count(item => !item.IsPrunable) == 1;
            GenerationManifest repositoryGeneration;
            if (advanceGlobal)
            {
                store.SetCurrent(generation, current);
                repositoryGeneration = generation;
            }
            else
            {
                // SetCurrent normally supplies this final stale-run guard. Keep it even
                // when the pointer must remain byte-for-byte unchanged.
                if (!store.TryReadCurrent(out var observed) || observed != current)
                    throw new InvalidOperationException("current_pointer_changed: cannot complete a worktree from a stale sync.");
                repositoryGeneration = store.ReadManifest(current!.GenerationId);
            }
            var manifest = new RepositoryManifest(
                requested.RepositoryId,
                commonDirectory,
                mainline.Ref,
                repositoryGeneration.Identity.Id,
                repositoryGeneration.Identity.DevTree,
                repositoryGeneration.Identity.EmbeddingModel,
                repositoryGeneration.Identity.EmbeddingDimension,
                repositoryGeneration.Identity.ChunkFormatVersion,
                repositoryGeneration.Identity.IndexFormatVersion,
                RepositoryIndexState.Current,
                worktrees
                    .Where(item => !item.IsPrunable)
                    .Select(item => new RepositoryWorktree(
                        item.Path,
                        item.Head,
                        item.Branch))
                    .ToArray(),
                DateTimeOffset.UtcNow);
            manifestStore.Save(manifest);
            PruneSupersededGenerations(
                requested.RepositoryRuntimeRoot.Value,
                runtimeRoot,
                error,
                ReachableOverlays(worktrees, runtimeRoot, error));

            reporter.Report(lastProgress = lastProgress with
            {
                Phase = RepositoryIndexProgressPhase.Completed,
                ProcessedChunks = lastProgress.TotalChunks,
                EstimatedRemaining = TimeSpan.Zero,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ChunksEmbedded = embedded.Total,
            });

            error.WriteLine(CliText.SyncFinished(Seconds(elapsed)));
            return new CodeSearchSyncResult(
                requested.RepositoryId,
                generation.Identity.Id,
                store.IndexPath(generation.Identity.Id),
                overlaysBuilt,
                generationChanged,
                worktreesSkipped,
                ElapsedSeconds: Seconds(elapsed));
        }
        catch
        {
            // The fact first, then Program.cs prints the cause. A run that stopped after four
            // hundred seconds is precisely the one whose cost the reader wants named, and it is
            // the run that never reaches the line above.
            error.WriteLine(CliText.SyncStoppedAfter(Seconds(elapsed)));
            try
            {
                reporter.Report(lastProgress with
                {
                    Phase = RepositoryIndexProgressPhase.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception progressError) when (
                progressError is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                // Progress is observational and cannot replace the indexing failure.
            }

            throw;
        }
    }

    /// <summary>
    /// The identity check overlays owe their name to (#197).
    ///
    /// The base generation is immune to edits during its build: it reads a materialized
    /// CommitSnapshot, never the live worktree. Overlays are the opposite — they read the
    /// working tree for minutes and land at final paths derived from the identity captured
    /// before the build, dirty hash included. An edit landing mid-build produced a mixed
    /// overlay whose name promised an exact snapshot: the moment the worktree returned to
    /// that state, search answered confidently with content the state never contained.
    ///
    /// Re-inspecting after the build and discarding on drift closes that at the cost of an
    /// honest retry — the next sync, which the edit's own hook already scheduled, rebuilds
    /// from the new state. A worktree that vanished mid-build counts as drifted: its
    /// artifacts are unnamed-state artifacts either way.
    /// </summary>
    internal static bool DiscardDriftedOverlay(
        WorkingIndexIdentity captured,
        string overlayPath,
        string semanticOverlayPath,
        string? runtimeRoot,
        TextWriter error)
    {
        var drifted = false;
        // Separately, because the two overlays are keyed by different hashes. A saved `.md`
        // renames the corpus overlay out from under this build and leaves the semantic one
        // describing this worktree exactly — and discarding it anyway costs the solution load
        // #385 exists to avoid, on the very overlay a pristine tree shares (#385, found in
        // review: the flag is read in two places and only one was moved).
        var semanticDrifted = false;
        try
        {
            var live = RuntimeIndexLayout.Inspect(captured.WorkingRoot.Value, runtimeRoot);
            var moved =
                !string.Equals(live.HeadCommit, captured.HeadCommit, StringComparison.Ordinal) ||
                !string.Equals(live.HeadTree, captured.HeadTree, StringComparison.Ordinal);
            drifted = moved ||
                !string.Equals(live.DirtyHash, captured.DirtyHash, StringComparison.Ordinal);
            semanticDrifted = moved ||
                !string.Equals(
                    live.SemanticDirtyHash,
                    captured.SemanticDirtyHash,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A worktree nobody can inspect names no state at all, so both artifacts are
            // unnamed-state artifacts.
            drifted = true;
            semanticDrifted = true;
        }

        if (!drifted)
        {
            return false;
        }

        foreach (var artifact in semanticDrifted
                     ? new[]
                     {
                         overlayPath,
                         semanticOverlayPath,
                         overlayPath + ".embedding-checkpoint",
                     }
                     : [overlayPath, overlayPath + ".embedding-checkpoint"])
        {
            if (File.Exists(artifact))
            {
                File.Delete(artifact);
            }
        }

        error.WriteLine(
            CliText.OverlayDiscarded(captured.WorkingRoot.Value));
        return true;
    }

    /// <summary>
    /// What the repository can still ask about: every live worktree, keyed the way its overlay
    /// directory is, paired with the tree its HEAD points at. Anything else under a kept
    /// generation belongs to a commit nobody is on or a worktree that is gone.
    /// </summary>
    private static IReadOnlySet<(string WorktreeId, string HeadTree)>? ReachableOverlays(
        IReadOnlyList<GitWorktree> worktrees,
        string? runtimeRoot,
        TextWriter error)
    {
        var reachable = new HashSet<(string, string)>();
        foreach (var worktree in worktrees.Where(item => !item.IsPrunable))
        {
            try
            {
                var identity = RuntimeIndexLayout.Inspect(worktree.Path, runtimeRoot);
                reachable.Add((
                    RuntimeIndexLayout.WorktreeKey(identity.WorkingRoot),
                    identity.HeadTree));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                // One worktree that cannot be inspected makes the whole set incomplete, and an
                // incomplete set reads as "these are gone". Inspect hashes the dirty working
                // files, so the throw lands exactly on a worktree with uncommitted work — the
                // one whose overlay matters most. Abandoning the sweep costs disk until the
                // next sync; guessing costs that worktree its index.
                error.WriteLine(CliText.WorktreeNotInspectable(
                    worktree.Path,
                    exception.Message));
                return null;
            }
        }

        return reachable;
    }

    /// <summary>
    /// Drops generations this repository has outgrown, immediately after publishing the one that
    /// superseded them.
    ///
    /// Publishing a new generation is the exact moment an old one stops being reachable, and it
    /// is the only moment the count can exceed the retention bound — so this is where the bound
    /// belongs. Left to <c>localai prune</c> alone it was invisible: superseded generations and
    /// their overlays reached several hundred megabytes on a repository that had simply been
    /// committed to for a while.
    ///
    /// Scoped to this repository and to generations only. Installed versions and installer
    /// backups are the larger share of a grown runtime, and they stay with the explicit command
    /// on purpose: deleting binaries as a side effect of indexing is a worse surprise than a
    /// large directory.
    ///
    /// Never fails the sync. The index is published and correct by this point; a retention sweep
    /// that could not run is worth reporting and nothing more.
    /// </summary>
    internal static void PruneSupersededGenerations(
        string repositoryRuntimeRoot,
        string? runtimeRoot,
        TextWriter error,
        IReadOnlySet<(string WorktreeId, string HeadTree)>? reachable = null)
    {
        try
        {
            var policy = new RuntimeRetentionPolicyStore(
                string.IsNullOrWhiteSpace(runtimeRoot)
                    ? RuntimeRetentionPolicyStore.DefaultRuntimeRoot
                    : runtimeRoot).Read();
            var result = GenerationRetention.Prune(
                repositoryRuntimeRoot,
                policy,
                DateTimeOffset.UtcNow,
                reachable: reachable);
            if (result.ActionCount > 0)
            {
                error.WriteLine(CliText.RetentionRemoved(
                    result.GenerationsRemoved.Count,
                    result.OverlaysRemoved.Count,
                    result.StagingRemoved.Count,
                    // One decimal, invariantly: integer division printed "0 MB" for every sweep
                    // under a megabyte, which reads as a contradiction beside a non-zero count.
                    (result.BytesReclaimed / 1024.0 / 1024.0)
                        .ToString("F1", CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error.WriteLine(
                CliText.RetentionSweepSkipped(exception.Message));
        }
    }

    private static Mainline ResolveMainline(
        WorkingIndexIdentity requested,
        string? configuredRef)
    {
        // Materialised, so the message below names the refs this loop actually tried. It used
        // to name dev and main from a literal, which is wrong for a repository whose manifest
        // configures neither.
        var candidates = new[]
            {
                configuredRef,
                "refs/heads/dev",
                "refs/heads/main"
            }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var commit = GitValue(
                requested.WorkingRoot.Value,
                "rev-parse",
                "--verify",
                $"{candidate}^{{commit}}");
            var tree = GitValue(
                requested.WorkingRoot.Value,
                "rev-parse",
                "--verify",
                $"{candidate}^{{tree}}");
            if (commit is null || tree is null)
            {
                continue;
            }

            return new Mainline(
                candidate!,
                requested with
                {
                    HeadCommit = commit,
                    HeadTree = tree,
                    DirtyHash = null
                });
        }

        throw new InvalidOperationException(
            CliText.MainlineMissing(string.Join(", ", candidates)));
    }

    /// <summary>
    /// The manifest, or null when this generation cannot be read at all.
    ///
    /// The four ways it refuses are one situation to a caller that is deciding whether to rebuild:
    /// the directory is gone, the manifest file is gone, the manifest is not valid JSON, or the
    /// index it names does not match its checksum. Only the first two were ever caught, so the
    /// other two left the sync as an unexpected failure and did so again on every run after it.
    /// </summary>
    /// <summary>
    /// One decimal, invariant, as every other duration this product prints: these lines are
    /// quoted verbatim by agents and parsed by tests, so a Russian reader must not be handed a
    /// comma or a group separator.
    /// </summary>
    private static string Seconds(Stopwatch elapsed) =>
        elapsed.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static GenerationManifest? TryReadManifest(GenerationStore store, string generationId)
    {
        try
        {
            return store.ReadManifest(generationId);
        }
        catch (Exception error) when (
            error is DirectoryNotFoundException or FileNotFoundException or
                InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static async Task<GenerationManifest> BuildGenerationAsync(
        GenerationStore store,
        GenerationPointer? current,
        GenerationIdentity generation,
        WorkingIndexIdentity dev,
        Action<IndexBuildProgress> progress,
        Action<RepositoryIndexProgressPhase> phase,
        Action<RepositoryIndexProgressPhase, IndexBuildStep, int, int> step,
        Action touch,
        EmbeddingBatchProfileStore batchProfiles,
        int maxBatchItems,
        string? runtimeRoot,
        bool requireSemantics,
        TextWriter error,
        IEmbeddingClient? embedder,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(
            dev.RepositoryRuntimeRoot.Value,
            "staging");
        Directory.CreateDirectory(stagingRoot);
        var workIndex = Path.Combine(
            stagingRoot,
            generation.Id + "." + Guid.NewGuid().ToString("N") + ".cidx");
        // Named for the generation and nothing else, because it has to be findable by the run
        // that resumes this one. The corpus file keeps its per-run suffix: it is rebuilt from the
        // embedding checkpoint, not reused as a file.
        var workSemanticIndex = Path.Combine(
            stagingRoot,
            generation.Id + ".sidx");
        var semanticAdapterStatusPath = workSemanticIndex + ".adapters.json";
        var embeddingCheckpointPath = Path.Combine(
            stagingRoot,
            generation.Id + ".embedding-checkpoint");
        try
        {
            // Reuse is an optimisation, so a previous generation that cannot be read costs this
            // build its head start and nothing else. It used to cost the build: `ReadManifest`
            // validates the checksum, so a `base.cidx` whose bytes no longer match its manifest
            // made this line throw `InvalidDataException` out of the sync entirely — where
            // `Program.cs` reported the one recoverable state it has as `unexpected_failure`, and
            // every sync after it took the same path (#390).
            if (current is not null && TryReadManifest(store, current.GenerationId) is { } previous)
            {
                var reusable = store.IndexPath(previous.Identity.Id);
                if (generation.CanReuseCorpusFrom(previous.Identity) &&
                    File.Exists(reusable))
                {
                    File.Copy(reusable, workIndex);
                }
            }

            using var snapshot = CommitSnapshot.Create(dev.WorkingRoot.Value, dev.HeadCommit);

            // Semantics first, deliberately. Roslyn loads the whole solution here and the SCIP
            // adapters shell out per language, which is minutes on a large repository — but
            // embedding is tens of minutes, and a failed adapter aborts the generation outright.
            // Running this second meant paying for the entire corpus before finding out the build
            // could not be published, which is the wrong order to learn that in.
            //
            // An interrupted build resumes both phases: the embedding checkpoint for the corpus,
            // and the staged semantic index for this one.
            phase(RepositoryIndexProgressPhase.SemanticBase);
            var semanticIndex = ResumeSemanticPhase(
                workSemanticIndex,
                semanticAdapterStatusPath,
                generation,
                dev,
                error);
            if (semanticIndex is null)
            {
                var built = await BuildSemanticIndexAsync(
                    snapshot.Root,
                    dev,
                    generation,
                    runtimeRoot,
                    error,
                    touch,
                    (built, done, total) => step(
                        RepositoryIndexProgressPhase.SemanticBase,
                        built,
                        done,
                        total),
                    cancellationToken);
                EnsureSemanticAdaptersSucceeded(built.AdapterStatuses);
                built.Index.Save(workSemanticIndex);
                WriteSemanticAdapterStatuses(semanticAdapterStatusPath, built.AdapterStatuses);
                semanticIndex = built;
            }

            ReportCsharpSemanticCoverage(
                snapshot.Root,
                semanticIndex.Index,
                semanticIndex.UncoveredProjects,
                requireSemantics,
                error);

            phase(RepositoryIndexProgressPhase.EmbeddingBase);
            var builder = new IndexBuilder(
                embedder ?? new BrokerEmbeddingClient(
                    generation.EmbeddingModel,
                    BrokerClientFactory.CreateDefault()),
                error.WriteLine,
                progress,
                // The definition bodies the phase above just produced. This is the whole reason
                // it runs first: a file the adapters covered is cut on its definitions instead
                // of on a line window, and a file they did not is cut exactly as before.
                SymbolDefinitionCatalog.FromSemanticIndex(semanticIndex.Index),
                batchProfiles: batchProfiles,
                maxEmbeddingBatchItems: maxBatchItems);
            await builder.BuildAsync(
                snapshot.Root,
                workIndex,
                force: false,
                cancellationToken,
                new IndexBuildContext(
                    dev.WorkingRoot.Value,
                    dev.HeadCommit,
                    dev.HeadTree,
                    dev.RepositoryId,
                    generation.Id),
                embeddingCheckpointPath,
                generation.EmbeddingDimension);
            var header = CodeIndex.Load(workIndex, withVectors: false);
            if (header.Dim != generation.EmbeddingDimension)
            {
                throw new InvalidDataException(
                    $"Embedding dimension {header.Dim} does not match expected " +
                    $"{generation.EmbeddingDimension}.");
            }

            // Copying a half-gigabyte corpus and hashing it twice is not instant either, and
            // nothing inside PublishIndex has a callback to count from — so the record names the
            // work rather than counting it.
            step(
                RepositoryIndexProgressPhase.PublishingGeneration,
                IndexBuildStep.CopyingGeneration,
                0,
                0);
            var published = store.PublishIndex(
                workIndex,
                generation,
                workSemanticIndex,
                semanticAdapterStatuses: semanticIndex.AdapterStatuses);
            DeleteEmbeddingCheckpoint(embeddingCheckpointPath, error);
            DeleteSemanticCheckpoint(workSemanticIndex, semanticAdapterStatusPath, error);
            return published;
        }
        finally
        {
            if (File.Exists(workIndex))
            {
                File.Delete(workIndex);
            }

            // The semantic checkpoint deliberately survives here. It is what an interrupted build
            // resumes from; the published generation deletes it, and retention collects one no
            // build came back for.
        }
    }

    /// <summary>
    /// The staged semantic index of an interrupted build, when it is the one this build needs.
    /// </summary>
    /// <remarks>
    /// On a repository the size of IntelWash the semantic phase is minutes, and it ran before
    /// embedding precisely so a failure is cheap. Without this, every resume paid those minutes
    /// again to produce a file identical to the one it had just deleted.
    ///
    /// Matching the generation id is the whole test, and it is sufficient rather than convenient:
    /// the id is derived from the repository, the git tree, the embedding model and dimension,
    /// and the chunk, normalization and semantic format versions. Two builds that agree on it
    /// are indexing the same tree with the same rules, so their semantic indexes are the same
    /// index. The repository and tree are compared anyway, because a file whose name says one
    /// generation and whose content says another is a corrupt file, not a stale one.
    ///
    /// Anything unreadable is treated as absent: the phase runs again and overwrites it. A
    /// checkpoint is an optimisation, and no optimisation is allowed to fail a build.
    /// </remarks>
    internal static SemanticBuildResult? ResumeSemanticPhase(
        string path,
        string statusPath,
        GenerationIdentity generation,
        WorkingIndexIdentity dev,
        TextWriter error)
    {
        if (!File.Exists(path) || !File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var index = SemanticIndex.Load(path);
            if (index.GenerationId != generation.Id ||
                index.RepositoryId != dev.RepositoryId ||
                index.GitTree != dev.HeadTree)
            {
                return null;
            }

            var statuses = JsonSerializer.Deserialize<SemanticAdapterStatus[]>(
                File.ReadAllText(statusPath),
                LocalAiJson.Strict);
            if (statuses is null)
            {
                return null;
            }

            // A checkpoint is only written after the adapters were checked, so this holds unless
            // the file was edited. Checked anyway: publishing a generation whose adapters failed
            // is the one thing the semantic phase exists to prevent.
            EnsureSemanticAdaptersSucceeded(statuses);
            error.WriteLine(
                CliText.SemanticPhaseResumed(Path.GetFileName(path)));
            return new SemanticBuildResult(index, statuses);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException)
        {
            error.WriteLine(
                CliText.SemanticCheckpointUnusable(path, exception.Message));
            return null;
        }
    }

    /// <summary>
    /// Recomputable: these are the adapters' own results for a staged semantic index, and a build
    /// that cannot find them runs the adapters again.
    /// </summary>
    internal static void WriteSemanticAdapterStatuses(
        string path,
        IReadOnlyList<SemanticAdapterStatus> statuses) =>
        AtomicFile.WriteJson(path, statuses, LocalAiJson.Strict, AtomicWrite.Recomputable);

    private static void DeleteSemanticCheckpoint(
        string path,
        string statusPath,
        TextWriter error)
    {
        foreach (var file in new[] { path, statusPath })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine(
                    CliText.SemanticCheckpointNotRemoved(file, exception.Message));
            }
        }
    }

    /// <summary>
    /// Says which worktree is about to be cut by line window, and why, when an adapter failed
    /// during an overlay build. The base generation refuses to publish in that situation; a
    /// branch carries on, and the difference has to be visible to whoever reads the sync output.
    /// </summary>
    /// <param name="error">
    /// Where the warning goes, for the reason <see cref="CommandStreams"/> gives. This one matters
    /// most of the three: the test that reads it asserts that nothing was written when no adapter
    /// failed, and on a process-wide stream that assertion could only ever be true by luck (#405).
    /// </param>
    internal static void WarnDegradedSemanticOverlay(
        IReadOnlyList<SemanticAdapterStatus> statuses,
        string workingRoot,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(error);
        var failures = statuses
            .Where(status => status.State == SemanticAdapterState.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        error.WriteLine(CliText.OverlayDegraded(
            workingRoot,
            string.Join("; ", failures.Select(failure => $"{failure.Name}: {failure.Message}"))));
    }

    /// <summary>
    /// Says so when semantic indexing did not cover the C# this repository has.
    ///
    /// Two shapes of the same silence. A workspace whose projects all failed to load still
    /// returns, the fallback writes an index with nothing in it, and sync exits 0 -- which is how
    /// a split between the Microsoft.CodeAnalysis package versions survived a green build and
    /// 1875 tests. And a repository with no solution file has one of its projects chosen and the
    /// rest left out, which reads exactly the same from outside: an index that is not empty, a
    /// status that says precise, and navigation that answers from text for most of the tree.
    ///
    /// Coverage is judged on what the loader reports it left out, not by counting .cs files
    /// against indexed ones. A repository legitimately holds C# no project compiles -- this one
    /// keeps test fixtures under tests/Fixtures -- and a check that warned about those would be
    /// ignored within a week.
    /// </summary>
    /// <param name="error">
    /// Where the warning goes. Required rather than defaulted to <c>Console.Error</c>, for the
    /// reason <see cref="CommandStreams"/> gives: a test that replaced the process's stream to
    /// read this warning replaced it for every test running beside it (#405).
    /// </param>
    internal static void ReportCsharpSemanticCoverage(
        string sourceRoot,
        SemanticIndex index,
        IReadOnlyList<string>? uncoveredProjects,
        bool requireSemantics,
        TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(error);

        if (uncoveredProjects is { Count: > 0 })
        {
            Report(
                CliText.CoverageProjectsUncovered(
                    uncoveredProjects.Count,
                    string.Join(", ", uncoveredProjects)),
                requireSemantics,
                error);
            return;
        }

        if (index.Documents.Any(document => IsCsharp(document.RelPath)) ||
            !FileScanner.Enumerate(sourceRoot).Any(IsCsharp))
        {
            return;
        }

        Report(CliText.CoverageNoCsharp, requireSemantics, error);
    }

    private static void Report(string message, bool requireSemantics, TextWriter error)
    {
        if (requireSemantics)
        {
            throw new InvalidOperationException(message);
        }

        error.WriteLine("WARNING: " + message);
    }

    private static bool IsCsharp(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    internal static void EnsureSemanticAdaptersSucceeded(
        IReadOnlyList<SemanticAdapterStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var failures = statuses
            .Where(status => status.State == SemanticAdapterState.Failed)
            .ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        var details = string.Join(
            "; ",
            failures.Select(failure => $"{failure.Name}: {failure.Message}"));
        throw new InvalidOperationException(CliText.AdaptersFailed(details));
    }

    private static void DeleteEmbeddingCheckpoint(string path, TextWriter error)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(
                CliText.EmbeddingCheckpointNotRemoved(path, exception.Message));
        }
    }

    /// <summary>
    /// The C# phase's narration and its record of being alive are the same event, so they are
    /// composed once and cannot come apart.
    ///
    /// The line is for the person watching the console. The second half is for everybody who is
    /// not: <c>ReportPhase</c> wrote <c>progress.json</c> when the phase began and never again, so
    /// <c>index_status</c> printed <c>SemanticOverlay - not counted, recorded 4 min ago</c> in
    /// front of a build that was working — the same reading a build whose process died four
    /// minutes ago produces, and <c>SyncNotice</c> prints that age precisely so a dead build looks
    /// dead (#419).
    ///
    /// Every line, not only the heartbeat's: the per-project line of a long analysis is evidence
    /// of life too, and it is the one that arrives while <c>WhileTheStepRunsAsync</c> is not
    /// running. What the record then means is "the build last said something this long ago".
    ///
    /// Nothing but the timestamp moves, and that is now load-bearing rather than incidental. This
    /// used to call <c>ReportPhase</c> again, which was harmless while a record carried nothing
    /// but a phase name; the step is written by the work itself, so re-reporting the phase here
    /// would erase it on every line the console prints. <c>Touch</c> is the operation this wants:
    /// the record says the build spoke, and what it was doing is left to whoever knows.
    ///
    /// The chunk counters stay the zeroes <c>ReportPhase</c> set, because
    /// <c>RepositoryIndexProgressPhaseExtensions.CountsChunks</c> exists to keep a phase that is
    /// not embedding from showing chunk figures, and filling them with projects would make that
    /// guard lie. The project count travels as the step's own pair instead.
    /// </summary>
    /// <summary>
    /// Keeps the progress record moving while an external adapter runs.
    ///
    /// <c>scip-typescript</c> and <c>scip-python</c> are each a single awaited call, and the
    /// policy allows either of them three hundred seconds. Nothing between the call and its return
    /// wrote anything, so <c>recorded 4 min ago</c> grew for the whole of a healthy run — which is
    /// exactly how a killed build reads, and the reason the pause reporting exists at all.
    /// </summary>
    internal static Task<T> WhileTheAdapterRunsAsync<T>(
        Task<T> adapter,
        IndexBuildStep name,
        Action<IndexBuildStep, int, int>? step,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancellationToken) =>
        // The same ticker the Roslyn steps use, rather than a second one beside it. What a tick
        // does here is re-report the step: the adapter has no counter, so the figures stay zero
        // and only the record's timestamp moves — which is the whole of what a reader watching
        // "recorded N ago" needs.
        QuietStep.WhileItRunsAsync(
            adapter,
            _ => step?.Invoke(name, 0, 0),
            time,
            wait,
            cancellationToken);

    internal static Action<string> RoslynNarration(TextWriter error, Action stillRunning) =>
        message =>
        {
            error.WriteLine($"Roslyn: {message}");
            stillRunning();
        };

    /// <summary>
    /// A save laid over the overlay of the commit it was made against: what the save actually
    /// touched, and the committed overlay whose records everything else comes from.
    ///
    /// This is the narrow case and the common one -- a file is saved, the commit under it was
    /// indexed when it was made, and only the saved file has moved since. The alternative below
    /// measures against the base generation instead, which on a branch a hundred commits ahead
    /// means recomputing a hundred files that are already written down.
    ///
    /// Nothing at all when there is no committed overlay to layer on, when the save cannot be
    /// judged from source, or when what it reaches is most of the tree anyway.
    /// </summary>
    internal static LayeredSemanticRebuild? PlanLayeredSemanticRebuild(
        string workingRoot,
        string headTree,
        string semanticOverlayPath,
        SemanticIndex baseIndex)
    {
        if (SemanticOverlayReuse.CommittedBeside(semanticOverlayPath) is not { } committedPath)
        {
            return null;
        }

        SemanticIndexOverlay committed;
        try
        {
            committed = SemanticIndexOverlay.Load(committedPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Written by an older build, or left half-written by one that died. Neither is a
            // reason to refuse the sync; both are a reason to do the wide thing instead.
            return null;
        }

        if (committed.DirtyHash is not null ||
            !string.Equals(
                committed.GenerationId,
                baseIndex.GenerationId,
                StringComparison.Ordinal) ||
            !string.Equals(committed.BaseGitTree, baseIndex.GitTree, StringComparison.Ordinal) ||
            !string.Equals(committed.GitTree, headTree, StringComparison.Ordinal))
        {
            return null;
        }

        var dirty = RuntimeIndexLayout.GetDirtyPaths(workingRoot)
            .Where(SemanticCorpusPolicy.ChangesSemantics)
            .ToArray();
        if (dirty.Length == 0)
        {
            // This worktree's identity was taken from a dirty list that had something semantic in
            // it, so an empty one now is git failing to answer or the tree moving under the
            // build, not a save with nothing in it. GetDirtyPaths cannot tell those apart -- it
            // returns an empty list for a git that did not run -- and reading it as "nothing
            // changed" here would write the commit's own records out under the save's name and
            // lose the save without a word.
            return null;
        }

        var saved = SemanticOverlayReuse.ChangedSince(
            [SemanticLayer.Of(committed), SemanticLayer.Of(baseIndex)],
            workingRoot,
            dirty);
        if (saved.Count == 0)
        {
            // The commit's own records, under the name this state is asked for by. Reached when
            // something changed a file the semantic layer reads without changing what it reads in
            // it -- a byte-order mark, a line ending, a file touched and saved unaltered.
            return new LayeredSemanticRebuild(
                committed,
                new ScopedRebuildPlan([], false, [], []));
        }

        var graph = ProjectGraph.Load(workingRoot);
        if (ScopedRebuild.Plan(
                saved,
                graph,
                path => ReadWorkingCopy(workingRoot, path),
                path => RuntimeIndexLayout.GetTextAt(workingRoot, headTree, path)) is not { } plan)
        {
            return null;
        }

        return ScopedRebuild.NarrowerThanTheSolution(plan, graph)
            ? new LayeredSemanticRebuild(committed, plan)
            : null;
    }

    /// <summary>
    /// How wide this worktree's semantic rebuild has to be, or nothing when it cannot be
    /// narrowed and the whole solution has to be reopened as it is today.
    /// </summary>
    internal static ScopedRebuildPlan? PlanScopedSemanticRebuild(
        string workingRoot,
        string baseTree)
    {
        var changed = RuntimeIndexLayout.GetPathsChangedSince(workingRoot, baseTree);
        if (changed is null)
        {
            return null;
        }

        var semantic = changed.Where(SemanticCorpusPolicy.ChangesSemantics).ToArray();
        if (semantic.Length == 0)
        {
            return null;
        }

        var graph = ProjectGraph.Load(workingRoot);
        if (ScopedRebuild.Plan(
                semantic,
                graph,
                path => ReadWorkingCopy(workingRoot, path),
                path => RuntimeIndexLayout.GetTextAt(workingRoot, baseTree, path)) is not { } plan)
        {
            return null;
        }

        // The same guard the layered plan gets, and it was missing here. A plan measured against
        // the base generation covers everything the branch has changed, so its reach is a
        // superset of the layered one's — rejecting that as too wide and then running this
        // instead is choosing the wider of the two, which is the 373-project route #519 measured.
        return ScopedRebuild.NarrowerThanTheSolution(plan, graph) ? plan : null;
    }

    private static string? ReadWorkingCopy(string workingRoot, string relativePath)
    {
        var root = Path.GetFullPath(workingRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(full);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The C# analysis over the projects a change reaches, rather than the solution.
    ///
    /// Neither the XAML supplement nor the SCIP adapters run here, and that is not a shortcut:
    /// a plan exists only when every changed file is C#, so no XAML, TypeScript or Python
    /// document is among the paths this rebuild speaks for, and
    /// <see cref="SemanticIndexOverlay.CreateScoped"/> leaves every path it was not told about
    /// exactly as the base has it. Skipping them is what the narrowing means, not a corner cut
    /// -- and on this repository they are 4.8 s and 5.6 s of a 53 s sync.
    /// </summary>
    private static async Task<SemanticBuildResult> BuildScopedSemanticIndexAsync(
        string sourceRoot,
        ScopedRebuildPlan plan,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation,
        TextWriter error,
        Action stillRunning,
        Action<IndexBuildStep, int, int> step,
        CancellationToken cancellationToken)
    {
        var roslyn = RoslynNarration(error, stillRunning);
        await using var loaded = await RoslynSolutionLoader.LoadScopedAsync(
            sourceRoot,
            // The entry points rather than every project the change reaches: Roslyn opens what a
            // named project references anyway, and each name costs its own restore.
            plan.EntryPoints,
            roslyn,
            step,
            cancellationToken: cancellationToken);
        var identity = new SemanticIndexBuildIdentity(
            generation.RepositoryId,
            generation.Id,
            snapshot.HeadTree,
            snapshot.SemanticDirtyHash,
            snapshot.HeadCommit,
            CommitTimestamp(snapshot.WorkingRoot.Value, snapshot.HeadCommit));
        var index = loaded is null
            ? EmptySemanticIndex(generation) with
            {
                GitTree = identity.GitTree,
                DirtyHash = identity.DirtyHash,
                BaseCommit = identity.BaseCommit,
            }
            : await loaded.BuildIndexAsync(
                sourceRoot,
                identity,
                roslyn,
                step,
                cancellationToken);
        return new SemanticBuildResult(
            index,
            [
                Skipped("typescript", "Only C# changed; this rebuild speaks for C# alone."),
                Skipped("python", "Only C# changed; this rebuild speaks for C# alone."),
            ]);
    }

    private static async Task<SemanticBuildResult> BuildSemanticIndexAsync(
        string sourceRoot,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation,
        string? runtimeRoot,
        TextWriter error,
        Action stillRunning,
        Action<IndexBuildStep, int, int> step,
        CancellationToken cancellationToken)
    {
        // One sink for the whole phase, named rather than repeated: the prefix is what marks a
        // line as belonging to the C# analysis, and a second copy is how one step comes to print
        // without it.
        var roslyn = RoslynNarration(error, stillRunning);
        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            sourceRoot,
            roslyn,
            step,
            cancellationToken: cancellationToken);
        // Built once and used by both branches, rather than spelled out in each. What this index
        // was built for is the state of the files the semantic layer reads, which is what
        // everything downstream compares it against (#385) — and the two branches disagreeing
        // about that is not hypothetical: the empty one was left on the corpus hash when the other
        // moved, so every repository with no loadable C# solution — every pure TypeScript or
        // Python tree, the population the SCIP adapters exist for — rebuilt its semantics on each
        // sync and answered no navigation at all. One expression cannot drift from itself.
        var identity = new SemanticIndexBuildIdentity(
            generation.RepositoryId,
            generation.Id,
            snapshot.HeadTree,
            snapshot.SemanticDirtyHash,
            snapshot.HeadCommit,
            CommitTimestamp(snapshot.WorkingRoot.Value, snapshot.HeadCommit));

        SemanticIndex languageIndex;
        if (loaded is null)
        {
            var empty = EmptySemanticIndex(generation) with
            {
                GitTree = identity.GitTree,
                DirtyHash = identity.DirtyHash,
                BaseCommit = identity.BaseCommit,
            };
            languageIndex = new XamlSemanticIndexer().Supplement(empty, sourceRoot);
        }
        else
        {
            var csharp = await loaded.BuildIndexAsync(
                sourceRoot,
                identity,
                roslyn,
                step,
                cancellationToken);
            languageIndex = new XamlSemanticIndexer().Supplement(csharp, sourceRoot);
        }

        var built = await RunScipAdaptersAsync(
            languageIndex,
            sourceRoot,
            runtimeRoot,
            error,
            step,
            cancellationToken);
        return built with { UncoveredProjects = loaded?.UncoveredProjects };
    }

    /// <summary>
    /// Whether the overlay already on disk still describes this worktree's semantics.
    ///
    /// Internal rather than private because it is the line that decides whether a sync costs
    /// 36 seconds or none, and it cannot be reached from a test any other way: everything around
    /// it needs an embedding backend, and the suite never starts one.
    /// </summary>
    internal static bool IsCurrentSemanticOverlay(
        string path,
        WorkingIndexIdentity snapshot,
        GenerationIdentity generation)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var overlay = SemanticIndexOverlay.Load(path);
            return string.Equals(overlay.RepositoryId, snapshot.RepositoryId, StringComparison.Ordinal) &&
                   string.Equals(overlay.GenerationId, generation.Id, StringComparison.Ordinal) &&
                   string.Equals(overlay.BaseGitTree, generation.DevTree, StringComparison.Ordinal) &&
                   string.Equals(overlay.GitTree, snapshot.HeadTree, StringComparison.Ordinal) &&
                   // The semantic hash: this overlay describes what the semantic layer knows, and
                   // an edit that layer cannot see leaves it true. Comparing the corpus hash here
                   // is what made every saved `.md` pay for a solution load (#385).
                   string.Equals(
                       overlay.DirtyHash,
                       snapshot.SemanticDirtyHash,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <param name="step">
    /// Which adapter is running, recorded before its process starts rather than after it returns:
    /// each is allowed 300 seconds by policy, and what a reader had for that stretch was a phase
    /// name and nothing else. It does not keep the record's age fresh through the run — nothing
    /// here writes while the process is alive, which is issue #491.
    /// </param>
    private static async Task<SemanticBuildResult> RunScipAdaptersAsync(
        SemanticIndex index,
        string sourceRoot,
        string? runtimeRoot,
        TextWriter error,
        Action<IndexBuildStep, int, int>? step,
        CancellationToken cancellationToken)
    {
        // The adapter policy belongs to the installation, so it is read from the same one the
        // generation is published into. Reading it from the machine's own instead would let the
        // operator's configuration decide what an isolated sync indexes.
        var policy = new SemanticIndexingPolicyStore(
            string.IsNullOrWhiteSpace(runtimeRoot)
                ? SemanticIndexingPolicyStore.DefaultRuntimeRoot
                : runtimeRoot).Read();
        var statuses = new List<SemanticAdapterStatus>();
        var files = FileScanner.Enumerate(sourceRoot)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        if (!policy.Enabled)
        {
            statuses.Add(Skipped("typescript", "Semantic external adapters are disabled."));
            statuses.Add(Skipped("python", "Semantic external adapters are disabled."));
            return new SemanticBuildResult(index, statuses);
        }

        var importer = new ScipImporter(policy.ImportLimits());
        var runner = new ScipAdapterRunner(importer);
        // Through the policy rather than a list of its own. Its comment already promised one
        // list "consulted both by the adapter choosing files and by the boundary" while the
        // adapter carried two copies -- which is exactly how the boundary comes to disagree with
        // what was actually read.
        var hasTypeScript = files.Any(SemanticCorpusPolicy.IsTypeScriptSource);
        var typeScript = policy.TypeScript;
        if (!hasTypeScript)
        {
            statuses.Add(Skipped("typescript", "No TypeScript or JavaScript files detected."));
        }
        else if (!typeScript.Enabled)
        {
            statuses.Add(Skipped("typescript", "Adapter is disabled by policy."));
        }
        else
        {
            var arguments = typeScript.Arguments.ToList();
            string? syntheticWorkspace = null;
            var adapterRoot = sourceRoot;
            try
            {
                if (arguments.SequenceEqual(["index"], StringComparer.Ordinal))
                {
                    var projects = files
                        .Where(path => string.Equals(
                            Path.GetFileName(path),
                            "tsconfig.json",
                            StringComparison.OrdinalIgnoreCase))
                        .Select(path => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
                            sourceRoot,
                            path.Replace('/', Path.DirectorySeparatorChar))))!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (projects.Length == 0)
                    {
                        syntheticWorkspace = CreateSyntheticTypeScriptWorkspace(
                            sourceRoot,
                            files.Where(IsTypeScriptSourceFile).ToArray());
                        adapterRoot = syntheticWorkspace;
                        projects = [syntheticWorkspace];
                    }

                    // scip-typescript 0.4.0 compares its project root with
                    // slash-normalized source paths using ordinal equality. Windows
                    // backslashes make it walk to the drive root and encode the
                    // temporary absolute path into every local symbol.
                    arguments.AddRange(projects.Select(project =>
                        project.Replace('\\', '/')));
                }

                step?.Invoke(IndexBuildStep.ScipTypeScript, 0, 0);
                var result = await WhileTheAdapterRunsAsync(
                    runner.RunAsync(
                        index,
                        adapterRoot,
                        Spec("typescript", typeScript, arguments, policy),
                        cancellationToken),
                    IndexBuildStep.ScipTypeScript,
                    step,
                    TimeProvider.System,
                    Task.Delay,
                    cancellationToken);
                index = result.Index;
                statuses.Add(result.Status);
                ReportAdapter(result.Status, error);
            }
            finally
            {
                if (syntheticWorkspace is not null &&
                    Directory.Exists(syntheticWorkspace))
                {
                    Directory.Delete(syntheticWorkspace, recursive: true);
                }
            }
        }

        var hasPython = files.Any(SemanticCorpusPolicy.IsPythonSource);
        var python = policy.Python;
        if (!hasPython)
        {
            statuses.Add(Skipped("python", "No Python files detected."));
        }
        else if (!python.Enabled)
        {
            statuses.Add(Skipped("python", "Adapter is disabled by policy."));
        }
        else
        {
            step?.Invoke(IndexBuildStep.ScipPython, 0, 0);
            var result = await WhileTheAdapterRunsAsync(
                runner.RunAsync(
                    index,
                    sourceRoot,
                    Spec("python", python, python.Arguments, policy),
                    cancellationToken),
                IndexBuildStep.ScipPython,
                step,
                TimeProvider.System,
                Task.Delay,
                cancellationToken);
            index = result.Index;
            statuses.Add(result.Status);
            ReportAdapter(result.Status, error);
        }

        return new SemanticBuildResult(index, statuses);
    }

    private static ScipAdapterSpec Spec(
        string name,
        ScipLanguageAdapterPolicy adapter,
        IReadOnlyList<string> arguments,
        SemanticIndexingPolicy policy) =>
        new(
            name,
            adapter.Executable,
            arguments,
            adapter.OutputFile,
            TimeSpan.FromSeconds(policy.TimeoutSeconds),
            policy.MaximumProcessOutputBytes,
            adapter.UnspecifiedPositionEncoding);

    internal static string CreateSyntheticTypeScriptWorkspace(
        string sourceRoot,
        IReadOnlyList<string> sourceFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        if (sourceFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "A synthetic TypeScript project requires at least one source file.");
        }

        var sourceRootFull = Path.GetFullPath(sourceRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"localai-scip-typescript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var relativeFiles = new List<string>(sourceFiles.Count);
            foreach (var path in sourceFiles)
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source path is not canonical: '{path}'.");
                }

                var source = Path.GetFullPath(Path.Combine(sourceRootFull, relative));
                var sourcePrefix = sourceRootFull + Path.DirectorySeparatorChar;
                if (!source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(source))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript source is unavailable: '{path}'.");
                }

                var target = Path.GetFullPath(Path.Combine(workspace, relative));
                var workspacePrefix = workspace + Path.DirectorySeparatorChar;
                if (!target.StartsWith(
                        workspacePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Synthetic TypeScript target escapes its workspace: '{path}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                relativeFiles.Add(path.Replace('\\', '/'));
            }

            var config = new
            {
                compilerOptions = new
                {
                    allowJs = true,
                    checkJs = false,
                    noEmit = true,
                    skipLibCheck = true,
                    target = "ES2020",
                    rootDir = ".",
                },
                files = relativeFiles,
            };
            File.WriteAllText(
                Path.Combine(workspace, "tsconfig.json"),
                JsonSerializer.Serialize(config),
                new UTF8Encoding(false));
            return workspace;
        }
        catch
        {
            Directory.Delete(workspace, recursive: true);
            throw;
        }
    }

    private static bool IsTypeScriptSourceFile(string path) =>
        SemanticCorpusPolicy.IsTypeScriptSource(path);

    private static SemanticAdapterStatus Skipped(string name, string message) =>
        new(name, SemanticAdapterState.Skipped, message, 0);

    private static void ReportAdapter(SemanticAdapterStatus status, TextWriter error) =>
        error.WriteLine(AdapterLine(status));

    /// <summary>
    /// An English literal rather than a catalogue key, deliberately: every element of it is a
    /// token, a number, or the adapter's own English message, and a translated clause welded to
    /// <c>SCIP typescript: succeeded</c> would be the odd thing on the line.
    ///
    /// The duration was measured by <c>ScipAdapterRunner</c> all along and dropped here. Printing
    /// it is what would have let the 36.72 seconds of silence be measured phase by phase instead
    /// of as one lump (#387), and it costs one interpolation.
    /// </summary>
    internal static string AdapterLine(SemanticAdapterStatus status) =>
        $"SCIP {status.Name}: {status.State.ToString().ToLowerInvariant()} in " +
        (status.DurationMilliseconds / 1000d).ToString("F1", CultureInfo.InvariantCulture) +
        $"s — {status.Message}";

    private static SemanticIndex EmptySemanticIndex(GenerationIdentity generation) =>
        new()
        {
            RepositoryId = generation.RepositoryId,
            GenerationId = generation.Id,
            GitTree = generation.DevTree,
            DirtyHash = null,
            BaseCommit = generation.DevCommit,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    private static DateTime CommitTimestamp(string root, string commit)
    {
        var value = GitValue(root, "show", "-s", "--format=%cI", commit);
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.UtcDateTime
            : DateTime.UnixEpoch;
    }

    /// <summary>
    /// Drops the worktrees that are no longer on disk, and says which.
    ///
    /// A worktree can be removed while this runs — it belongs to somebody else, and a sync of a
    /// large repository takes tens of minutes. Losing the whole run to that, after an hour of
    /// embedding, is the wrong answer: that overlay is gone either way, and every other
    /// worktree still deserves its own.
    ///
    /// Only absence is tolerated. A worktree that exists but cannot be read still stops the
    /// run, because publishing a generation with overlays quietly missing is worse than
    /// failing.
    /// </summary>
    internal static (IReadOnlyList<GitWorktree> Worktrees, int Skipped) SelectPresentWorktrees(
        IEnumerable<GitWorktree> targets,
        Action<string> report)
    {
        var present = new List<GitWorktree>();
        var skipped = 0;
        foreach (var worktree in targets)
        {
            if (Directory.Exists(worktree.Path))
            {
                present.Add(worktree);
                continue;
            }

            report(worktree.Path);
            skipped++;
        }

        return (present, skipped);
    }

    private static IReadOnlyList<GitWorktree> ReadWorktrees(string root)
    {
        var output = RepoLocator.GitOutputOrThrow(
            root,
            "worktree list --porcelain",
            "This repository's worktree list");
        return WorktreeInventory.ParsePorcelain(output);
    }

    private static string? GitValue(string root, params string[] arguments)
    {
        var output = RepoLocator.GitOutputBytes(root, arguments);
        return output is null
            ? null
            : Encoding.UTF8.GetString(output).Trim();
    }

    private sealed record Mainline(
        string Ref,
        WorkingIndexIdentity Identity);
}

/// <summary>
/// A narrow rebuild and the committed overlay it is laid over. The two travel together because
/// neither is usable without the other: the plan says what to recompute, and the overlay is where
/// everything else comes from.
/// </summary>
internal sealed record LayeredSemanticRebuild(
    SemanticIndexOverlay Committed,
    ScopedRebuildPlan Plan);
