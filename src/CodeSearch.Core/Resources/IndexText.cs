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

    public static string GenerationNotPublished(string workingRoot) =>
        Catalogue.Format(nameof(GenerationNotPublished), workingRoot);

    public static string GenerationWithoutSemanticIndex(string generationId, string workingRoot) =>
        Catalogue.Format(nameof(GenerationWithoutSemanticIndex), generationId, workingRoot);

    public static string SemanticIndexUnreadable(string generationId, string workingRoot) =>
        Catalogue.Format(nameof(SemanticIndexUnreadable), generationId, workingRoot);

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
}
