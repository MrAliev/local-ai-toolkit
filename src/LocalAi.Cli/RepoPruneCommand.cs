using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Cli;

/// <summary>
/// Removes what a merged pull request leaves in a repository: a local branch whose every commit
/// is already in the mainline, and the worktree that branch was checked out in.
///
/// The hard part is "already in the mainline" after a squash merge, which is how this repository
/// merges. Measured on git 2.55.0.windows.5, over a branch of two commits squashed into main:
/// <c>branch --merged</c> does not list it, <c>rev-list --count main..branch</c> says 2, and
/// <c>git cherry</c> says <c>+ +</c> — all three call finished work unmerged, because a squash
/// carries the patch id of none of the commits it replaced. <c>git cherry</c> answers correctly
/// only for a branch of exactly one commit, which is what makes it look right when first tried.
///
/// So containment is asked as a question about content: would merging this branch change the
/// mainline at all. <c>git merge-tree --write-tree</c> answers it in one call, and the answer is
/// the sentence printed. A branch with no commit of its own answers that question the same way —
/// it is sitting on the mainline — so it is excluded separately rather than deleted as finished.
///
/// Nothing here reaches the network. Every check is local, so the command behaves the same
/// offline, over a VPN, and in a repository with no remote at all.
/// </summary>
internal static class RepoPruneCommand
{
    private const int GitTimeoutMs = 120_000;

    /// <summary>
    /// Turns a branch into what should happen to it, and the sentence saying why.
    /// </summary>
    private sealed record Verdict(
        string Branch,
        string Head,
        string? Worktree,
        bool Removable,
        string? Reason);

    public static int Execute(string[] args, CommandStreams streams, string? runtimeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? root = null;
        var dryRun = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--root" when index + 1 < args.Length:
                    root = args[++index];
                    break;
                case "--root":
                    streams.Error.WriteLine(CliText.RepoPruneRootWithoutDirectory);
                    return 2;
                case "--help":
                    streams.Error.WriteLine(CliText.RepoPruneUsage);
                    return 2;
                default:
                    // An argument silently ignored here would delete: the same reason
                    // `localai prune` refuses one rather than carrying on.
                    streams.Error.WriteLine(CliText.RepoPruneUnknownArgument(
                        args[index],
                        CliUsage.RepoPrune));
                    return 2;
            }
        }

        return Run(root ?? Environment.CurrentDirectory, dryRun, automatic: false,
            streams.Error, runtimeRoot);
    }

    /// <summary>
    /// The same removal, run from the <c>post-merge</c> hook — the one event that means the
    /// mainline moved.
    ///
    /// Off until the repository asks for it with <c>git config localai.pruneMerged true</c>. The
    /// hook was installed to keep an index in sync, and deleting branches is not something it may
    /// start doing because the tool updated. Read with git's own boolean parsing, so <c>on</c> and
    /// <c>yes</c> mean what git says they mean rather than being silently false.
    ///
    /// Silent when it removes nothing: this runs in the middle of somebody's <c>git pull</c>, and
    /// the reasons a branch was kept are for the person who asked, not for the person who typed
    /// pull. Never throws — a cleanup that failed must not fail the hook, and the index sync that
    /// follows in the same invocation is the part that matters.
    /// </summary>
    internal static void OnPostMerge(string root, string? runtimeRoot, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (Checkout(root) is not { } checkout ||
                Git(checkout, "config", "--get", "--type=bool", "localai.pruneMerged")
                    is not { Ok: true, Output: "true" })
            {
                return;
            }

            Run(root, dryRun: false, automatic: true, error, runtimeRoot);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private static int Run(
        string start,
        bool dryRun,
        bool automatic,
        TextWriter error,
        string? runtimeRoot)
    {
        var checkout = Checkout(start);
        if (checkout is null)
        {
            error.WriteLine(CliText.RepositoryOutsideGit(start));
            return 2;
        }

        var candidates = MainlineCandidates(checkout, runtimeRoot);
        var mainline = candidates.FirstOrDefault(
            candidate => Git(checkout, "rev-parse", "--verify", "--quiet", candidate + "^{commit}").Ok);
        if (mainline is null)
        {
            error.WriteLine(CliText.RepoPruneMainlineMissing(string.Join(", ", candidates)));
            return 2;
        }

        var mainlineTree = Git(checkout, "rev-parse", mainline + "^{tree}");
        if (!mainlineTree.Ok)
        {
            error.WriteLine(CliText.RepoPruneMainlineMissing(string.Join(", ", candidates)));
            return 2;
        }

        var shortMainline = Short(mainline);
        var current = Git(start, "symbolic-ref", "--quiet", "--short", "HEAD");
        var worktrees = Worktrees(checkout);
        var verdicts = Branches(checkout)
            .Where(branch => !string.Equals(branch, shortMainline, StringComparison.Ordinal))
            .Select(branch => Judge(
                checkout,
                branch,
                shortMainline,
                mainlineTree.Output,
                current.Ok ? current.Output : null,
                worktrees))
            .ToArray();

        if (dryRun)
        {
            // First, not last: a notice at the end of a report of deletions is read after them.
            error.WriteLine(CliText.RepoPruneDryRun);
        }

        var removedBranches = 0;
        var removedWorktrees = 0;
        foreach (var verdict in verdicts)
        {
            if (!verdict.Removable)
            {
                // Why a branch was kept is for somebody who asked. On a pull it would be noise
                // on every branch anybody is still working on.
                if (verdict.Reason is not null && !automatic)
                {
                    error.WriteLine(verdict.Reason);
                }

                continue;
            }

            if (dryRun)
            {
                error.WriteLine(verdict.Worktree is null
                    ? CliText.RepoPruneWouldRemoveNoWorktree(verdict.Branch, shortMainline)
                    : CliText.RepoPruneWouldRemove(verdict.Branch, shortMainline, verdict.Worktree));
                removedBranches++;
                if (verdict.Worktree is not null)
                {
                    removedWorktrees++;
                }

                continue;
            }

            if (verdict.Worktree is not null)
            {
                var removal = Git(checkout, "worktree", "remove", verdict.Worktree);
                if (!removal.Ok)
                {
                    error.WriteLine(CliText.RepoPruneWorktreeRefused(
                        verdict.Branch,
                        verdict.Worktree,
                        removal.Error));
                    continue;
                }

                removedWorktrees++;
            }

            // Forced, because a squash merge leaves git convinced the branch is unmerged — that
            // is the whole reason this command exists, and `-d` would refuse every branch it is
            // meant to take. The containment check above is what makes it safe.
            var deletion = Git(checkout, "branch", "-D", verdict.Branch);
            if (!deletion.Ok)
            {
                // The half-done state: git refuses to delete a branch held by a worktree, so the
                // order is forced and this is where it can stop between the two steps.
                error.WriteLine(
                    CliText.RepoPruneBranchRefused(verdict.Branch, deletion.Error));
                continue;
            }

            removedBranches++;
            error.WriteLine(verdict.Worktree is null
                ? CliText.RepoPruneRemovedNoWorktree(verdict.Branch, shortMainline, Abbreviate(verdict.Head))
                : CliText.RepoPruneRemoved(
                    verdict.Branch,
                    shortMainline,
                    Abbreviate(verdict.Head),
                    verdict.Worktree));
        }

        if (dryRun)
        {
            error.WriteLine(CliText.RepoPruneWouldSummary(removedBranches, removedWorktrees));
            return 0;
        }

        if (removedBranches == 0 && removedWorktrees == 0)
        {
            // A pull that cleaned nothing contributes nothing: a line on every pull is a hook
            // people uninstall.
            if (!automatic)
            {
                error.WriteLine(CliText.RepoPruneNothing(shortMainline));
            }

            return 0;
        }

        // Nothing here deletes an overlay itself. The sweep below gives retention no live set, so
        // overlays under a kept generation stay, a removed worktree's among them; the sync that
        // follows on the hook path, or the next one run by hand, passes that set and drops them.
        if (!automatic)
        {
            Sweep(checkout, runtimeRoot, error);
            error.WriteLine(CliText.RepoPruneSummary(removedBranches, removedWorktrees));
            if (!Git(checkout, "config", "--get", "--type=bool", "localai.pruneMerged").Ok)
            {
                // Only after a run that removed something, and only while the setting is unset:
                // the moment the reader has just seen what it would do for them.
                error.WriteLine(CliText.RepoPruneAutomationOffer);
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether this branch is finished with, and the sentence to print when it is not.
    ///
    /// Ordered by cost: the cheap local questions first, and the content comparison — the only
    /// one that spawns a merge — last, for the branches that survived everything else.
    /// </summary>
    private static Verdict Judge(
        string checkout,
        string branch,
        string mainline,
        string mainlineTree,
        string? current,
        IReadOnlyList<GitWorktree> worktrees)
    {
        var head = Git(checkout, "rev-parse", branch);
        var worktree = worktrees.FirstOrDefault(one => Names(one, branch));
        var path = worktree is null ? null : Path.GetFullPath(worktree.Path);

        if (string.Equals(branch, current, StringComparison.Ordinal))
        {
            return new(branch, head.Output, path, false, CliText.RepoPruneKeptCurrent(branch));
        }

        if (worktree is not null && path is not null)
        {
            if (Locked(checkout, path))
            {
                return new(branch, head.Output, path, false, CliText.RepoPruneKeptLocked(branch, path));
            }

            if (Directory.Exists(path))
            {
                var state = Git(path, "status", "--porcelain");
                if (!state.Ok)
                {
                    return new(branch, head.Output, path, false,
                        CliText.RepoPruneKeptWorktreeUnreadable(branch, path, state.Error));
                }

                if (state.Output.Length > 0)
                {
                    return new(branch, head.Output, path, false,
                        CliText.RepoPruneKeptDirty(branch, path));
                }
            }
            else if (!VolumeExists(path))
            {
                // A worktree on an unmounted volume reads exactly like one deleted by hand, and
                // taking that as deleted would drop a branch whose work nobody has looked at.
                return new(branch, head.Output, path, false,
                    CliText.RepoPruneKeptWorktreeUnreadable(branch, path, path));
            }
        }

        var ahead = Git(checkout, "rev-list", "--count", mainline + ".." + branch);
        if (!ahead.Ok)
        {
            return new(branch, head.Output, path, false,
                CliText.RepoPruneKeptUnanswerable(branch, mainline, ahead.Error));
        }

        if (ahead.Output == "0")
        {
            // Sitting on the mainline rather than merged into it. Every branch created a moment
            // ago looks like this, and deleting them would be the first thing this command did.
            return new(branch, head.Output, path, false, CliText.RepoPruneKeptNoCommits(branch));
        }

        var merged = Git(checkout, "merge-tree", "--write-tree", mainline, branch);
        var tree = merged.Output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        if (!merged.Ok)
        {
            // A conflict, a ref git will not merge, or a git too old for --write-tree. Says the
            // question could not be answered rather than answering it.
            return new(branch, head.Output, path, false,
                CliText.RepoPruneKeptUnanswerable(branch, mainline, merged.Error));
        }

        return string.Equals(tree, mainlineTree, StringComparison.Ordinal)
            ? new(branch, head.Output, path, true, null)
            : new(branch, head.Output, path, false,
                CliText.RepoPruneKeptUnmerged(branch, ahead.Output, mainline));
    }

    /// <summary>
    /// The refs to try as the mainline, in the order the index tries them: the manifest's own
    /// choice first, then dev, then main. One list rather than two, because a repository whose
    /// manifest names dev must not have its branches compared against a stale main.
    /// </summary>
    private static string[] MainlineCandidates(string checkout, string? runtimeRoot)
    {
        string? configured = null;
        try
        {
            var identity = RuntimeIndexLayout.Inspect(checkout, runtimeRoot);
            configured = new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Read()?.DevRef;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or InvalidOperationException
                or InvalidDataException)
        {
            // A repository the index has never seen has no manifest, and that is ordinary.
        }

        return [.. new[] { configured, "refs/heads/dev", "refs/heads/main" }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The retention sweep that already exists, run without a live set: generations past the
    /// bound go with every overlay under them, and overlays under a kept generation stay until
    /// the next sync, which knows which worktrees are left.
    /// </summary>
    private static void Sweep(string checkout, string? runtimeRoot, TextWriter error)
    {
        try
        {
            var identity = RuntimeIndexLayout.Inspect(checkout, runtimeRoot);
            CodeSearchSyncCommand.PruneSupersededGenerations(
                identity.RepositoryRuntimeRoot.Value,
                runtimeRoot,
                error);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or InvalidOperationException
                or InvalidDataException)
        {
            // The branches are gone either way; a sweep that could not run is not a failure of
            // the removal that preceded it.
        }
    }

    private static bool Locked(string checkout, string path) =>
        Git(checkout, "worktree", "list", "--porcelain") is { Ok: true } listed &&
        listed.Output
            .Replace("\r\n", "\n")
            .Split("\n\n")
            .Any(block =>
                block.Contains("worktree " + path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                block.Contains("locked", StringComparison.Ordinal));

    private static bool VolumeExists(string path)
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(path));
        return !string.IsNullOrEmpty(volume) && Directory.Exists(volume);
    }

    private static IReadOnlyList<string> Branches(string checkout)
    {
        var listed = Git(checkout, "for-each-ref", "--format=%(refname:short)", "refs/heads/");
        return listed.Ok
            ? [.. listed.Output
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)]
            : [];
    }

    private static IReadOnlyList<GitWorktree> Worktrees(string checkout)
    {
        var listed = Git(checkout, "worktree", "list", "--porcelain");
        return listed.Ok ? WorktreeInventory.ParsePorcelain(listed.Output) : [];
    }

    private static bool Names(GitWorktree worktree, string branch) =>
        worktree.Branch is { } reference &&
        string.Equals(Short(reference), branch, StringComparison.Ordinal);

    private static string Short(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    /// <summary>
    /// Echoes git's own "Deleted branch x (was e3e3319)": the short sha is the only handle left
    /// for recovering work from a branch that no longer has a name.
    /// </summary>
    private static string Abbreviate(string head) =>
        head.Length >= 7 ? head[..7] : head;

    private static string? Checkout(string start)
    {
        var common = RepoLocator.GitOutput(
            Directory.Exists(start) ? start : Environment.CurrentDirectory,
            "rev-parse --path-format=absolute --git-common-dir");
        if (string.IsNullOrWhiteSpace(common))
        {
            return null;
        }

        var trimmed = common.Trim().TrimEnd('/', '\\');
        var parent = Path.GetDirectoryName(trimmed);
        return Path.GetFileName(trimmed) == ".git" && parent is not null
            ? Path.GetFullPath(parent)
            : null;
    }

    private readonly record struct GitRun(bool Ok, string Output, string Error);

    private static GitRun Git(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ChildProcessText.Utf8,
                StandardErrorEncoding = ChildProcessText.Utf8,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return new GitRun(false, string.Empty, string.Empty);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return new GitRun(false, string.Empty, string.Empty);
            }

            Task.WaitAll([output, error], GitTimeoutMs);
            return new GitRun(process.ExitCode == 0, output.Result.Trim(), error.Result.Trim());
        }
        catch (Exception failure) when (failure is IOException or SystemException)
        {
            return new GitRun(false, string.Empty, failure.Message);
        }
    }
}
