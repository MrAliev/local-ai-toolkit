using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum RepositoryIndexState
{
    NotConfigured,
    Initializing,
    Current,
    DirtyPending,
    DirtyCurrent,
    Updating,
    Stale,
    Failed
}

/// <summary>
/// Where an index build currently is.
///
/// Embedding is the loudest phase but not the only slow one. A generation also has to build its
/// semantic graph — Roslyn loads the solution, the SCIP adapters run — and then copy the corpus
/// into place and checksum it. Those took two and a half minutes on a real repository while the
/// last reported phase was still <see cref="EmbeddingBase"/> at its final chunk, which reads as a
/// finished build that is refusing to publish. Every phase that can take minutes now says so.
/// </summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum RepositoryIndexProgressPhase
{
    Planning,
    EmbeddingBase,
    SemanticBase,
    PublishingGeneration,
    EmbeddingOverlay,
    SemanticOverlay,
    Publishing,
    Completed,
    Failed
}

/// <summary>
/// Why a build is not moving, or null while it is. Both the status and the reason in one field,
/// because a pause with no reason is the thing this exists to stop: a reader could already see
/// that the counters had stopped, and could not tell a machine that was working from one that was
/// gone. The phase says what the build is doing; this says whether it is doing it.
///
/// A token rather than a sentence. It is written by a build and read by another program, possibly
/// on a different language setting, so the surface that shows it to a person is where it becomes
/// words — the same rule the phase follows.
///
/// One member, because one pause has been observed: five of them in a single build on the owner's
/// machine, each lasting minutes. A second member belongs here when a second is seen.
/// </summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum IndexBuildPause
{
    /// <summary>The embedding backend refused, and the build is waiting for it to come back.</summary>
    BackendUnavailable
}

/// <summary>
/// Which piece of work a phase is on, or null where nothing recorded one.
///
/// The phase alone was never enough for the phases that do not count chunks. `SemanticBase` is
/// five pieces of work in a row with three different answers to "how far": a `dotnet restore`
/// with no per-item callback that <c>RoslynSolutionLoader.RestoreTimeout</c> allows ten minutes,
/// an <c>OpenSolutionAsync</c> that is one call, an analysis that counts projects because it
/// awaits them in turn, and two SCIP adapters that each get 300 seconds by
/// <c>SemanticIndexingPolicy.Default</c>. For all of it a reader saw the phase name, a timestamp
/// and `not counted in this phase`, which answers neither "what is it doing" nor "is it moving".
///
/// A step rather than a unit, because a unit would speak only for the one piece that counts
/// anything and leave the rest — most of the minutes on a cold machine — as silent as before.
/// The count, where a step has one, then belongs to the step.
///
/// A token rather than a sentence, and read by another program possibly set to another language,
/// exactly as the phase and <see cref="IndexBuildPause"/> are: the surface that shows it to a
/// person is where it becomes words.
///
/// The embedding phases have no member here. Their phase is their step, they count chunks, and
/// <c>CountsChunks</c> already says so — a second name for one thing is how two faces come to
/// disagree about it.
/// </summary>
[JsonConverter(typeof(StrictJsonStringEnumConverter))]
public enum IndexBuildStep
{
    /// <summary>Listing what is in the repository and hashing it, before anything is chunked.</summary>
    ReadingRepository,

    /// <summary>`dotnet restore` for the solution the semantic phase is about to open.</summary>
    RestoringDependencies,

    /// <summary>Opening the solution, or the one project a repository without a solution has.</summary>
    OpeningProjects,

    /// <summary>
    /// Walking the projects for definitions and references. The one step of a semantic build with
    /// a figure of its own: the project count is known before the first iteration.
    /// </summary>
    AnalysingProjects,

    /// <summary>The scip-typescript adapter.</summary>
    ScipTypeScript,

    /// <summary>The scip-python adapter.</summary>
    ScipPython,

    /// <summary>Copying the corpus into the generation and checksumming it.</summary>
    CopyingGeneration,

    /// <summary>Moving the pointer, after which answers come from the new generation.</summary>
    SwitchingOver,
}

public static class RepositoryIndexProgressPhaseExtensions
{
    /// <summary>
    /// Whether the phase's chunk counters mean anything. Outside embedding they are the frozen
    /// tally of the last phase that had one, and printing "1415/1415, 0 remaining" beside a phase
    /// that is not counting chunks is how a build in progress comes to look like a finished one.
    /// </summary>
    public static bool CountsChunks(this RepositoryIndexProgressPhase phase) =>
        phase is RepositoryIndexProgressPhase.EmbeddingBase
            or RepositoryIndexProgressPhase.EmbeddingOverlay;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryIndexProgress(
    string RepositoryId,
    RepositoryIndexProgressPhase Phase,
    string WorkingRoot,
    int ProcessedChunks,
    int TotalChunks,
    double ChunksPerSecond,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset UpdatedAtUtc,
    // Optional so a progress record written by an older build still deserialises: absent means
    // the build was running, which is what every record written before this field meant.
    IndexBuildPause? Pause = null,
    // The same rule again, and the same reason. Absent means a build that did not know to write
    // it, which is a state the surfaces name rather than guess at: the phase is there and the
    // step is not.
    //
    // The counters are the step's own and are read only where the step declares it has them.
    // They are deliberately not ProcessedChunks and TotalChunks: those two belong to embedding,
    // CountsChunks is the guard that keeps a finished tally from standing beside a phase that
    // counts nothing, and filling them with projects would make that guard lie.
    IndexBuildStep? Step = null,
    int StepDone = 0,
    int StepTotal = 0,
    // On the Completed record only: what the build embedded, base and overlays together (#774).
    // Absent from a build that did not know to write it, and left out rather than written as
    // null, because a reader built before it refuses a member it does not know.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ChunksEmbedded = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryWorktree(
    string Path,
    string Head,
    string? Branch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryManifest(
    string RepositoryId,
    string CommonDirectory,
    string DevRef,
    string? CurrentGenerationId,
    string? PublishedGitTree,
    string EmbeddingModel,
    int EmbeddingDimension,
    int ChunkFormatVersion,
    int IndexFormatVersion,
    RepositoryIndexState State,
    IReadOnlyList<RepositoryWorktree> ActiveWorktrees,
    DateTimeOffset UpdatedAtUtc);
