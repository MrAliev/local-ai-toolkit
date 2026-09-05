using System.Collections.Concurrent;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Mcp;

/// <summary>
/// Finds the build running behind an answer, cheaply enough to ask on every answer.
///
/// `index_status` can afford <see cref="RuntimeIndexLayout.Inspect"/> — five git processes and a
/// content hash of every dirty file — because a person asks for a status now and then. A line
/// above every search and every navigation answer cannot: the figures are worth having only if
/// reading them costs about nothing. So the repository is identified once per working root, and
/// after that an answer costs one read of a small file.
///
/// The identity is asked of git rather than worked out from the `.git` entry here. A linked
/// worktree's `.git` is a file pointing at a gitdir which points at the main repository's common
/// directory, and that rule already has one home; a second copy of it is how a live worktree came
/// to hash to a key matching no directory.
///
/// Everything it cannot answer is silence. A directory that is no repository, a runtime that has
/// recorded no build, a progress file it cannot parse — none of those are worth putting above an
/// answer the reader did ask for, and none of them is a reason to fail a search that had one
/// ready.
/// </summary>
public sealed class RepositoryProgressReader
{
    private readonly string? _runtimeRoot;
    private readonly Func<string, string?> _commonDirectory;

    /// <summary>
    /// Working root to repository id. Keyed by <see cref="FsPath.IdentityKey"/> rather than by the
    /// spelling a caller passed, so `R:\LocalAi` and `r:\localai\` are one entry.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _identified =
        new(StringComparer.Ordinal);

    public RepositoryProgressReader(
        string? runtimeRoot = null,
        Func<string, string?>? commonDirectory = null)
    {
        _runtimeRoot = runtimeRoot;
        _commonDirectory = commonDirectory ?? AskGit;
    }

    public RepositoryIndexProgress? Read(string? root)
    {
        try
        {
            if (Identify(RepoLocator.ResolveWorkingRoot(root)) is not { } repositoryId)
            {
                return null;
            }

            var runtimeRoot = string.IsNullOrWhiteSpace(_runtimeRoot)
                ? RuntimeIndexLayout.DefaultRuntimeRoot
                : _runtimeRoot;
            return new RepositoryIndexProgressStore(
                    FsPath.From(runtimeRoot).Combine("repositories", repositoryId))
                .Read();
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException or
                InvalidOperationException)
        {
            return null;
        }
    }

    private string? Identify(FsPath workingRoot)
    {
        if (_identified.TryGetValue(workingRoot.IdentityKey, out var known))
        {
            return known;
        }

        var commonDirectory = _commonDirectory(workingRoot.Value);
        if (string.IsNullOrWhiteSpace(commonDirectory))
        {
            // Deliberately not remembered. A directory that is no repository yet becomes one, and
            // git answering nothing can also be git being unavailable for a moment; caching that
            // would silence this repository's figures for the life of the server.
            return null;
        }

        var repositoryId = RepositoryIdentity.FromCommonDirectory(commonDirectory).Id;
        _identified[workingRoot.IdentityKey] = repositoryId;
        return repositoryId;
    }

    private static string? AskGit(string workingRoot) =>
        RepoLocator.GitOutput(
            workingRoot,
            "rev-parse --path-format=absolute --git-common-dir");
}
