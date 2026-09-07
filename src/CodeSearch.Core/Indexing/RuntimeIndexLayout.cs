using System.Security.Cryptography;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// The three paths are FsPath rather than string because two of them become directory names in
/// the runtime — the worktree key is hashed from <paramref name="WorkingRoot"/>, and
/// <paramref name="RepositoryRuntimeRoot"/> is where that repository's generations live. A
/// string carries no evidence that it was normalised, and a comparison that forgets still
/// compiles.
/// </summary>
/// <param name="DirtyHash">
/// What the corpus contains beyond the commit: every uncommitted file the index may hold, hashed
/// by content. Null when the worktree is clean.
/// </param>
/// <param name="SemanticDirtyHash">
/// The same, over the subset of those files a change to which would change what the semantic layer
/// knows — <see cref="SemanticCorpusPolicy"/> draws that line. Null when none of them is dirty,
/// which includes a clean worktree.
///
/// Two hashes rather than one because the two overlays answer different questions and cost
/// different amounts. Editing prose changes the corpus and nothing the semantic layer could see,
/// and keying both overlays by one hash made every such edit pay for a solution load, an analysis
/// and the SCIP adapters — 36.72 seconds of a 49.67-second sync (#385).
/// </param>
public sealed record WorkingIndexIdentity(
    FsPath WorkingRoot,
    FsPath RepositoryRoot,
    string RepositoryId,
    FsPath RepositoryRuntimeRoot,
    string HeadCommit,
    string HeadTree,
    string? DirtyHash,
    string? SemanticDirtyHash);

public static class RuntimeIndexLayout
{
    /// <summary>
    /// The installation directory used when a caller does not name one. Deferred to the one
    /// definition every LocalAi component already shares rather than spelled out again here:
    /// two answers to "which installation" is the problem this parameter exists to remove.
    /// </summary>
    public static string DefaultRuntimeRoot => ModelResidencyPolicyStore.DefaultRuntimeRoot;

    /// <summary>
    /// Inspects a working tree against a runtime root.
    ///
    /// <paramref name="runtimeRoot"/> exists so a caller can say which installation to look at
    /// rather than always being handed the machine's own. Tests are the pressing case: they
    /// wrote into the real %LOCALAPPDATA%\LocalAi and shared its broker, so an unrelated index
    /// build running at the same moment could fail them — which reads as a flaky test rather
    /// than as two things using one directory.
    /// </summary>
    public static WorkingIndexIdentity Inspect(
        string workingRoot,
        string? runtimeRoot = null)
    {
        var root = RepoLocator.ResolveWorkingRoot(workingRoot);
        var repositoryRoot = RepoLocator.ResolveRoot(root.Value);
        var commonDirectory = RepoLocator.GitOutputOrThrow(
            root.Value,
            "rev-parse --path-format=absolute --git-common-dir",
            "The git common directory");
        var identity = RepositoryIdentity.FromCommonDirectory(commonDirectory);
        var repositoryRuntimeRoot = FsPath
            .From(string.IsNullOrWhiteSpace(runtimeRoot) ? DefaultRuntimeRoot : runtimeRoot)
            .Combine("repositories", identity.Id);
        var head = RepoLocator.GitOutputOrThrow(
            root.Value,
            "rev-parse HEAD",
            "Git HEAD");
        var tree = RepoLocator.GitOutputOrThrow(
            root.Value,
            "rev-parse HEAD^{tree}",
            "The git HEAD tree");
        var dirtyPaths = GetDirtyPaths(root.Value);
        var dirtyHash = dirtyPaths.Count == 0
            ? null
            : DirtyCorpusPolicy.ComputeWorkingContentHash(root.Value, dirtyPaths);
        // The same hash over the subset that can change what the semantic layer knows, so that an
        // edit it cannot see leaves its overlay where it is. ComputeWorkingContentHash rather than
        // a second kind of hash: it already answers for a file that was deleted, which a semantic
        // change very much is.
        var semanticDirtyPaths = dirtyPaths
            .Where(SemanticCorpusPolicy.ChangesSemantics)
            .ToList();
        var semanticDirtyHash = semanticDirtyPaths.Count == 0
            ? null
            : DirtyCorpusPolicy.ComputeWorkingContentHash(root.Value, semanticDirtyPaths);

        return new WorkingIndexIdentity(
            root,
            repositoryRoot,
            identity.Id,
            repositoryRuntimeRoot,
            head,
            tree,
            dirtyHash,
            semanticDirtyHash);
    }

    public static string ResolveBaseIndexPath(
        string repositoryRoot,
        string? runtimeRoot = null)
    {
        var identity = Inspect(repositoryRoot, runtimeRoot);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent();
        return current is null
            ? RepoLocator.LegacyIndexPathFor(identity.RepositoryRoot.Value, runtimeRoot)
            : store.IndexPath(current.GenerationId);
    }

    public static string OverlayPath(
        WorkingIndexIdentity identity,
        string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var worktreeId = WorktreeKey(identity.WorkingRoot);
        return Path.Combine(
            identity.RepositoryRuntimeRoot.Value,
            "overlays",
            generationId,
            worktreeId,
            identity.HeadTree,
            (identity.DirtyHash ?? "clean") + ".cidx");
    }

    /// <summary>
    /// Beside the corpus overlay and in the same directory — same generation, same worktree, same
    /// head tree — but named by the semantic hash rather than the corpus one.
    ///
    /// That is the difference #385 is about. Two states of one worktree that a saved `.md` apart
    /// share this file, correctly: they differ from the base generation in exactly the same way as
    /// far as the semantic layer is concerned, so one overlay is the true answer for both, and the
    /// sync that finds it already there skips the solution load, the analysis and the adapters.
    /// </summary>
    public static string SemanticOverlayPath(
        WorkingIndexIdentity identity,
        string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        return Path.Combine(
            identity.RepositoryRuntimeRoot.Value,
            "overlays",
            generationId,
            WorktreeKey(identity.WorkingRoot),
            identity.HeadTree,
            (identity.SemanticDirtyHash ?? "clean") + ".sidx");
    }

    public static List<string> GetDirtyPaths(string workingRoot)
    {
        var root = RepoLocator.ResolveWorkingRoot(workingRoot).Value;
        var changed = RepoLocator.GitOutputBytes(
            root,
            ["diff", "--name-only", "-z", "HEAD", "--"]);
        var untracked = RepoLocator.GitOutputBytes(
            root,
            ["ls-files", "--others", "--exclude-standard", "-z"]);
        return ParseNullPaths(changed)
            .Concat(ParseNullPaths(untracked))
            .Where(path => DirtyCorpusPolicy.IsAllowed(path, gitIgnored: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ParseNullPaths(byte[]? output)
    {
        if (output is null || output.Length == 0)
        {
            return [];
        }

        return Encoding.UTF8.GetString(output)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The directory name an overlay for this worktree lives under. Exposed because retention
    /// has to recognise the same key rather than compute its own version of it.
    ///
    /// Takes an <see cref="FsPath"/> so the normalisation cannot be skipped: git prints forward
    /// slashes on Windows and Inspect yields backslashes, so the same worktree hashed to two
    /// different keys depending on who asked — and a key that matches no directory reads as
    /// "this worktree is gone", which is how live overlays came to be deleted.
    /// </summary>
    public static string WorktreeKey(FsPath workingRoot) => Hash(workingRoot.IdentityKey);

    /// <summary>For a working root arriving as text — a command-line argument, git output.</summary>
    public static string WorktreeKey(string workingRoot) => WorktreeKey(FsPath.From(workingRoot));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
