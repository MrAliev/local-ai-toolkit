using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The line that decides whether a sync pays 36 seconds or nothing.
///
/// Editing two files of 715 in this repository cost 49.67 seconds, of which 36.72 was opening a
/// solution of 26 projects, analysing it and running the SCIP adapters; a sync with nothing to do
/// cost 1.33. The whole of that difference was one comparison: the overlay was refused because the
/// corpus hash had changed, and every save changes the corpus hash (#385).
///
/// It is checked here rather than through a sync because everything around it needs an embedding
/// backend and the suite never starts one — the argument `ACommandWritesWhereItIsToldTests` makes
/// for the same shape of gap.
/// </summary>
public sealed class ASemanticOverlayOutlivesAnEditItCannotSeeTests : IDisposable
{
    private const string Repository = "repository";
    private const string BaseTree = "base-tree";
    private const string HeadTree = "head-tree";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-semantic-overlay-" + Guid.NewGuid().ToString("N"));

    public ASemanticOverlayOutlivesAnEditItCannotSeeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void An_overlay_built_before_a_prose_edit_still_answers_after_it()
    {
        var beforeProse = Identity(dirtyHash: "corpus-1", semanticDirtyHash: "semantic-1");
        var path = Save(beforeProse);

        var afterProse = beforeProse with { DirtyHash = "corpus-2" };

        Assert.True(CodeSearchSyncCommand.IsCurrentSemanticOverlay(
            RuntimeIndexLayout.SemanticOverlayPath(afterProse, Generation().Id),
            afterProse,
            Generation()));
        // The same file, which is why there was nothing to rebuild.
        Assert.Equal(path, RuntimeIndexLayout.SemanticOverlayPath(afterProse, Generation().Id));
    }

    [Fact]
    public void An_overlay_built_before_a_code_edit_does_not()
    {
        var beforeCode = Identity(dirtyHash: "corpus-1", semanticDirtyHash: "semantic-1");
        Save(beforeCode);

        var afterCode = beforeCode with
        {
            DirtyHash = "corpus-2",
            SemanticDirtyHash = "semantic-2",
        };

        Assert.False(CodeSearchSyncCommand.IsCurrentSemanticOverlay(
            RuntimeIndexLayout.SemanticOverlayPath(afterCode, Generation().Id),
            afterCode,
            Generation()));
    }

    /// <summary>
    /// Moving the branch is not an edit the semantic layer cannot see: the head tree is part of
    /// the directory, so the overlay written at one commit is not even looked for at another.
    /// </summary>
    [Fact]
    public void An_overlay_from_another_commit_is_not_reached_for()
    {
        var here = Identity(dirtyHash: null, semanticDirtyHash: null);
        Save(here);

        var elsewhere = here with { HeadTree = "another-tree" };

        Assert.NotEqual(
            RuntimeIndexLayout.SemanticOverlayPath(here, Generation().Id),
            RuntimeIndexLayout.SemanticOverlayPath(elsewhere, Generation().Id));
        Assert.False(CodeSearchSyncCommand.IsCurrentSemanticOverlay(
            RuntimeIndexLayout.SemanticOverlayPath(elsewhere, Generation().Id),
            elsewhere,
            Generation()));
    }

    private string Save(WorkingIndexIdentity identity)
    {
        var path = RuntimeIndexLayout.SemanticOverlayPath(identity, Generation().Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new SemanticIndexOverlay
        {
            RepositoryId = identity.RepositoryId,
            GenerationId = Generation().Id,
            BaseGitTree = BaseTree,
            GitTree = identity.HeadTree,
            DirtyHash = identity.SemanticDirtyHash,
            DeletedDocuments = [],
            Changed = new SemanticIndex
            {
                RepositoryId = identity.RepositoryId,
                GenerationId = Generation().Id,
                GitTree = identity.HeadTree,
                DirtyHash = identity.SemanticDirtyHash,
                BaseCommit = "commit",
                IndexedAtUtc = DateTime.UnixEpoch,
                Documents = [],
                Symbols = [],
                Occurrences = [],
                Relationships = [],
            },
        }.Save(path);
        return path;
    }

    private WorkingIndexIdentity Identity(string? dirtyHash, string? semanticDirtyHash) =>
        new(
            FsPath.From(_root),
            FsPath.From(_root),
            Repository,
            FsPath.From(Path.Combine(_root, "runtime")),
            "commit",
            HeadTree,
            dirtyHash,
            semanticDirtyHash);

    private static GenerationIdentity Generation() =>
        new(
            Repository,
            "commit",
            BaseTree,
            "model",
            4096,
            CodeSearchSyncCommand.CurrentChunkFormatVersion,
            CodeIndex.CurrentVersion,
            CodeSearchSyncCommand.CurrentNormalizationVersion,
            1,
            CodeSearchSyncCommand.CurrentSemanticGenerationVersion);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
