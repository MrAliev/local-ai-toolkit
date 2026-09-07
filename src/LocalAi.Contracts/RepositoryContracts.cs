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
    IndexBuildPause? Pause = null);

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
