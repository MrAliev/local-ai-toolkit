using LocalAi.Contracts.Localization;

namespace CodeSearch.Core.Resources;

/// <summary>
/// What this assembly refuses with, and what it says while it builds, in the language the reader's
/// machine is set to.
///
/// These sentences do not stay inside the process that throws them: the MCP tools print them as
/// the <c>{ex.Message}</c> half of <c>semantic_navigation_not_ready:</c> and its siblings, and
/// <c>codesearch --json</c> puts them in <c>error.message</c>. Until this catalogue existed they
/// were the last place a Russian reader got a Russian answer with an English cause.
///
/// The rule for what belongs here is <c>CodeSearchText</c>'s, unchanged: anything left of the
/// first colon is a field name or a code and stays English, and so does any status token,
/// identifier, path or command to the right of one. Sentences and clauses are translated. There
/// is deliberately not a second rule.
///
/// Not everything this assembly throws is here. A refusal no entry point can produce has no
/// reader, and a string translated for nobody is cost with a parity test guarding it — so
/// <c>SearchQualityProfile</c>'s <c>MinVectorScore</c> refusal and both of
/// <c>SearchReadinessGate</c>'s stay English literals: the first is reachable only by code
/// calling <c>Resolve</c> directly, and the second by a type nothing in <c>src/</c> constructs.
/// </summary>
public static class IndexText
{
    public static TextCatalogue Catalogue { get; } = new(
        "CodeSearch.Core.Resources.IndexText",
        typeof(IndexText).Assembly);

    /// <summary>
    /// The narration of a build, in the order a run produces it.
    ///
    /// Two of the sixteen lines this class could have taken are deliberately still literals in
    /// <c>IndexBuilder</c>: the format-version and model-change lines in <c>LoadReusable</c>. Under
    /// <c>localai sync</c> — the only face that follows the reader — a previous index is copied in
    /// only when <c>CanReuseCorpusFrom</c> agrees, and that compares both the embedding model and
    /// the index format version, so neither branch can be reached. The one face that does reach
    /// them, <c>codesearch index</c>, pins English for its whole output. That is the same rule the
    /// class comment above already states: a line no reader-following entry point can print is a
    /// translation for nobody, with a parity test guarding it.
    ///
    /// Durations and rates arrive already formatted, to one decimal and invariantly, so a machine
    /// that would write <c>10,2</c> still writes <c>10.2</c> here. Counts need nothing:
    /// <c>TextCatalogue.Format</c> is invariant.
    /// </summary>
    public static string BuildScanned(string root, int files) =>
        Catalogue.Format(nameof(BuildScanned), root, files);

    public static string BuildPartitioned(int unchanged, int changed) =>
        Catalogue.Format(nameof(BuildPartitioned), unchanged, changed);

    public static string BuildChunked(int files, int chunks) =>
        Catalogue.Format(nameof(BuildChunked), files, chunks);

    public static string BuildReusingIndex(int chunks, int files) =>
        Catalogue.Format(nameof(BuildReusingIndex), chunks, files);

    public static string BuildIndexNotReusable(string reason) =>
        Catalogue.Format(nameof(BuildIndexNotReusable), reason);

    public static string BuildCheckpointRestored(int restored, int total) =>
        Catalogue.Format(nameof(BuildCheckpointRestored), restored, total);

    public static string BuildCheckpointIgnored(string path, string reason) =>
        Catalogue.Format(nameof(BuildCheckpointIgnored), path, reason);

    public static string BuildEmbedded(int done, int total, string rate, string minutesLeft) =>
        Catalogue.Format(nameof(BuildEmbedded), done, total, rate, minutesLeft);

    public static string BuildBatchHalved(int chunks, string reason) =>
        Catalogue.Format(nameof(BuildBatchHalved), chunks, reason);

    /// <param name="refusal">
    /// The refusal's own message, taken from the exception rather than re-derived from a code.
    /// It is already composed, catalogued and length-bounded by the layer that owns the
    /// backend's vocabulary, so nothing here has to learn which of the seven states this was.
    /// </param>
    public static string BuildBackendStalled(string refusal) =>
        Catalogue.Format(nameof(BuildBackendStalled), refusal);

    /// <param name="minutesLeft">
    /// What is left of the waiting budget, F1 and invariant. The one figure that moves from line
    /// to line, which is what tells a reader the build is alive rather than hung.
    /// </param>
    public static string BuildBackendWaiting(string minutesLeft) =>
        Catalogue.Format(nameof(BuildBackendWaiting), minutesLeft);

    /// <param name="seconds">
    /// How long the outage actually lasted, measured rather than budgeted, F1 and invariant.
    /// </param>
    public static string BuildBackendAnswered(string seconds) =>
        Catalogue.Format(nameof(BuildBackendAnswered), seconds);

    /// <summary>
    /// What a build says when the embedding backend went away under it. The figures live here
    /// rather than beside the exception because only the build loop knows them, and they are the
    /// whole of what #389 asked for: a refusal that said nothing but "the model backend was not
    /// available" left a reader with no idea whether nine minutes of work had survived.
    /// </summary>
    public static string BuildEmbeddingStopped(int done, int total) =>
        Catalogue.Format(nameof(BuildEmbeddingStopped), done, total);

    public static string BuildEmbeddingStoppedAtStart(int total) =>
        Catalogue.Format(nameof(BuildEmbeddingStoppedAtStart), total);

    /// <param name="chunks">
    /// What the finished index would hold — fresh plus reused — rather than what is left to
    /// embed. Beside a ceiling, the smaller of the two reads as a limit that was not reached.
    /// </param>
    /// <param name="ceiling">
    /// How many chunks of this width one vector block can hold, computed from
    /// <c>Array.MaxLength</c> rather than written down, so the sentence cannot drift from the
    /// limit it describes.
    /// </param>
    public static string BuildTooManyChunks(int chunks, int ceiling, int dimensions) =>
        Catalogue.Format(nameof(BuildTooManyChunks), chunks, ceiling, dimensions);

    /// <summary>
    /// What the semantic phase says while it works, which was nothing at all: 36.72 seconds from
    /// the start of a sync to its first line, all of it inside <c>dotnet restore</c> and
    /// <c>MSBuildWorkspace.OpenSolutionAsync</c>, and hidden indexing is indistinguishable from a
    /// hung machine (#387).
    ///
    /// Both of those are single opaque calls with no per-project callback, so what is honest to
    /// print is that they started and what they cost — not a fraction nobody can compute.
    /// </summary>
    public static string RoslynLoading(string solutionPath, int timeoutMinutes) =>
        Catalogue.Format(nameof(RoslynLoading), solutionPath, timeoutMinutes);

    public static string RoslynRestored(string seconds) =>
        Catalogue.Format(nameof(RoslynRestored), seconds);

    /// <param name="seconds">
    /// How long this step has been running, F1 and invariant. The only figure the line carries:
    /// a step with no per-item callback cannot honestly offer a fraction or an estimate.
    /// </param>
    public static string StepStillRunning(string seconds) =>
        Catalogue.Format(nameof(StepStillRunning), seconds);

    public static string RoslynAnalysing(int analysed, int projects) =>
        Catalogue.Format(nameof(RoslynAnalysing), analysed, projects);

    public static string RoslynAnalysed(int projects, int files, string seconds) =>
        Catalogue.Format(nameof(RoslynAnalysed), projects, files, seconds);

    public static string RoslynOpened(int projects, string seconds) =>
        Catalogue.Format(nameof(RoslynOpened), projects, seconds);

    public static string EmbeddingBrokerNotStarted(string detail) =>
        Catalogue.Format(nameof(EmbeddingBrokerNotStarted), detail);

    public static string EmbeddingBackendUnavailable(string model, string detail) =>
        Catalogue.Format(nameof(EmbeddingBackendUnavailable), model, detail);

    public static string BuildSaved(string indexPath, int chunks, int files, string seconds) =>
        Catalogue.Format(nameof(BuildSaved), indexPath, chunks, files, seconds);

    public static string BuildOverlayScanned(string workingRoot, int files, int inBase) =>
        Catalogue.Format(nameof(BuildOverlayScanned), workingRoot, files, inBase);

    public static string BuildOverlayDivergence(int changed, int deleted) =>
        Catalogue.Format(nameof(BuildOverlayDivergence), changed, deleted);

    public static string BuildOverlayReuse(int reused, int toEmbed) =>
        Catalogue.Format(nameof(BuildOverlayReuse), reused, toEmbed);

    public static string BuildOverlaySaved(
        string overlayPath,
        int chunks,
        int files,
        int deletions,
        string seconds) =>
        Catalogue.Format(
            nameof(BuildOverlaySaved), overlayPath, chunks, files, deletions, seconds);

    /// <summary>
    /// Six reasons a navigation answer is not the precise one, each in two halves.
    ///
    /// The command that repairs each of them is a key of its own, and the whole-sentence
    /// accessors join the halves back with a single space — so every face that prints the sentence
    /// prints exactly what it always did, to the byte. The MCP navigation tools are the one caller
    /// that prints a half: while a build holds the repository's sync gate that command exits 75
    /// <c>sync_busy</c>, and a block naming the running build stands where it was.
    ///
    /// The hole numbering differs between a half and the whole on three of them, deliberately: the
    /// generation id belongs to the fact, so the remedy's only hole is the working root.
    /// </summary>
    public static string GenerationNotPublished(string workingRoot) =>
        Sentence(GenerationNotPublishedFact, GenerationNotPublishedRemedy(workingRoot));

    public static string GenerationNotPublishedFact =>
        Catalogue.Get(nameof(GenerationNotPublishedFact));

    public static string GenerationNotPublishedRemedy(string workingRoot) =>
        Catalogue.Format(nameof(GenerationNotPublishedRemedy), workingRoot);

    public static string GenerationWithoutSemanticIndex(string generationId, string workingRoot) =>
        Sentence(
            GenerationWithoutSemanticIndexFact(generationId),
            GenerationWithoutSemanticIndexRemedy(workingRoot));

    public static string GenerationWithoutSemanticIndexFact(string generationId) =>
        Catalogue.Format(nameof(GenerationWithoutSemanticIndexFact), generationId);

    public static string GenerationWithoutSemanticIndexRemedy(string workingRoot) =>
        Catalogue.Format(nameof(GenerationWithoutSemanticIndexRemedy), workingRoot);

    /// <summary>
    /// The pointer names a generation, and that generation will not load — its directory gone,
    /// its manifest gone or unparseable, its checksum not matching. One sentence for all of them,
    /// because none changes what the reader does.
    ///
    /// Until this existed the read threw out of the navigation gateway instead of degrading: the
    /// one call in <c>LoadCurrent</c> that sat outside every try.
    /// </summary>
    public static string GenerationUnreadable(string generationId, string workingRoot) =>
        Sentence(
            GenerationUnreadableFact(generationId),
            GenerationUnreadableRemedy(workingRoot));

    public static string GenerationUnreadableFact(string generationId) =>
        Catalogue.Format(nameof(GenerationUnreadableFact), generationId);

    public static string GenerationUnreadableRemedy(string workingRoot) =>
        Catalogue.Format(nameof(GenerationUnreadableRemedy), workingRoot);

    public static string SemanticIndexUnreadable(string generationId, string workingRoot) =>
        Sentence(
            SemanticIndexUnreadableFact(generationId),
            SemanticIndexUnreadableRemedy(workingRoot));

    public static string SemanticIndexUnreadableFact(string generationId) =>
        Catalogue.Format(nameof(SemanticIndexUnreadableFact), generationId);

    public static string SemanticIndexUnreadableRemedy(string workingRoot) =>
        Catalogue.Format(nameof(SemanticIndexUnreadableRemedy), workingRoot);

    /// <summary>
    /// The overlay this worktree needs has not been built. Ordinary after any edit — and until
    /// #409 it was reported as the generation's own semantic index being unreadable, which is a
    /// different file, at a different path, that had just loaded successfully a line earlier.
    /// </summary>
    public static string SemanticOverlayNotBuilt(string generationId, string workingRoot) =>
        Sentence(
            SemanticOverlayNotBuiltFact(generationId),
            SemanticOverlayNotBuiltRemedy(workingRoot));

    public static string SemanticOverlayNotBuiltFact(string generationId) =>
        Catalogue.Format(nameof(SemanticOverlayNotBuiltFact), generationId);

    public static string SemanticOverlayNotBuiltRemedy(string workingRoot) =>
        Catalogue.Format(nameof(SemanticOverlayNotBuiltRemedy), workingRoot);

    /// <summary>
    /// The overlay is there and will not load. Rare, and a real fault — which is precisely why it
    /// must not share a sentence with the everyday state above it.
    /// </summary>
    public static string SemanticOverlayUnreadable(string workingRoot) =>
        Sentence(SemanticOverlayUnreadableFact, SemanticOverlayUnreadableRemedy(workingRoot));

    public static string SemanticOverlayUnreadableFact =>
        Catalogue.Get(nameof(SemanticOverlayUnreadableFact));

    public static string SemanticOverlayUnreadableRemedy(string workingRoot) =>
        Catalogue.Format(nameof(SemanticOverlayUnreadableRemedy), workingRoot);

    /// <summary>
    /// One space, in one place. The join is what makes the split invisible to every face that did
    /// not ask for the halves, so it is not spelled three times.
    /// </summary>
    private static string Sentence(string fact, string remedy) => fact + " " + remedy;

    public static string NoSymbolAtPosition => Catalogue.Get(nameof(NoSymbolAtPosition));

    public static string SnapshotMismatch => Catalogue.Get(nameof(SnapshotMismatch));

    public static string OverlayBaseMismatch => Catalogue.Get(nameof(OverlayBaseMismatch));

    public static string ModelNotCalibrated(string model) =>
        Catalogue.Format(nameof(ModelNotCalibrated), model);

    public static string IndexPredatesSnapshotIds(string workingRoot) =>
        Catalogue.Format(nameof(IndexPredatesSnapshotIds), workingRoot);

    public static string IndexBelongsToAnotherRepository(string workingRoot) =>
        Catalogue.Format(nameof(IndexBelongsToAnotherRepository), workingRoot);

    public static string OverlayMissing(string workingRoot) =>
        Catalogue.Format(nameof(OverlayMissing), workingRoot);

    public static string IndexNotBuilt(string indexPath) =>
        Catalogue.Format(nameof(IndexNotBuilt), indexPath);

    public static string ChunkIdMalformed => Catalogue.Get(nameof(ChunkIdMalformed));

    public static string ChunkIdTampered => Catalogue.Get(nameof(ChunkIdTampered));

    public static string ChunkWrongRepository => Catalogue.Get(nameof(ChunkWrongRepository));

    public static string ChunkStaleGeneration => Catalogue.Get(nameof(ChunkStaleGeneration));

    public static string ChunkStaleWorktree => Catalogue.Get(nameof(ChunkStaleWorktree));

    public static string ChunkStaleOverlay => Catalogue.Get(nameof(ChunkStaleOverlay));

    public static string ChunkOutOfRange => Catalogue.Get(nameof(ChunkOutOfRange));

    public static string ChunkSourceStaleContent => Catalogue.Get(nameof(ChunkSourceStaleContent));

    public static string ChunkSourceStaleRange => Catalogue.Get(nameof(ChunkSourceStaleRange));

    public static string ChunkPathOutsideRoot => Catalogue.Get(nameof(ChunkPathOutsideRoot));

    public static string ChunkPathReparsePoint => Catalogue.Get(nameof(ChunkPathReparsePoint));

    public static string ChunkSourceMissing => Catalogue.Get(nameof(ChunkSourceMissing));

    public static string ChunkSourceUnavailable => Catalogue.Get(nameof(ChunkSourceUnavailable));

    /// <summary>
    /// The two asides that stand in for a hit's snippet when the file has moved on since it was
    /// indexed. Both reach a reader through <c>search_code</c>, whose report otherwise follows
    /// them — so as English literals they were the one English paragraph in a Russian answer.
    /// </summary>
    public static string SnapshotChangedSnippet => Catalogue.Get(nameof(SnapshotChangedSnippet));

    public static string StaleRangeSnippet => Catalogue.Get(nameof(StaleRangeSnippet));
}
