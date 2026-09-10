using System.Diagnostics;
using System.Text.Json;
using CodeSearch.Core.Indexing;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Cli;

/// <summary>
/// Makes and drops the worktree a branch is worked in, at
/// <c>&lt;parent of the repository&gt;\worktrees\&lt;repository&gt;\&lt;branch&gt;</c>.
///
/// Beside the repository rather than inside it. A worktree in the working tree is an untracked
/// second checkout that only an ignore rule keeps out of <c>git status</c> and out of the index --
/// <see cref="FileScanner"/> lists files with <c>git ls-files --others --exclude-standard</c>, so
/// that rule was load-bearing for indexing and not merely for tidiness -- and JetBrains' IDEs
/// document that nesting one there makes them read the project as multi-root and breaks their
/// worktree integration. Outside, the repository does not have to know anything about us.
///
/// Two callers, one behaviour. A client's hook sends a JSON payload on standard input; a person
/// types the branch. Standard output carries the created path and nothing else, on both faces,
/// because that is the client's contract for <c>WorktreeCreate</c> -- a property of the command
/// rather than a rule to remember. Every sentence goes to standard error.
/// </summary>
internal static class AgentWorktreeCommand
{
    /// <summary>The folder under the root that holds one repository's worktrees.</summary>
    private const string WorktreesDirectory = "worktrees";

    /// <summary>Where a root of somebody's own is named, per repository or per machine.</summary>
    private const string RootSetting = "localai.worktreeRoot";

    /// <summary>
    /// The branch prefixes a client's one-word slug may carry.
    ///
    /// A client sends `feature-auth`, never `feature/auth`: its field is a slug. Taken verbatim,
    /// every worktree a client opens would carry a branch that breaks the naming rule, and a
    /// person later asking for `feature/auth` would get a second worktree for the same work.
    /// </summary>
    private static readonly string[] BranchTypes =
        ["fix", "feature", "test", "docs", "refactor", "chore"];

    private const int GitTimeoutMs = 120_000;

    public static int Execute(
        string[] args,
        CommandStreams streams,
        TextReader input,
        bool inputRedirected)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);

        return args switch
        {
            ["create", .. var rest] => Create(rest, streams, input, inputRedirected),
            ["remove", .. var rest] => Remove(rest, streams, input, inputRedirected),
            [var unknown, ..] => Unknown(unknown, streams),
            [] => Usage(streams),
        };
    }

    private static int Usage(CommandStreams streams)
    {
        streams.Error.WriteLine(CliUsage.AgentWorktree);
        return 2;
    }

    private static int Unknown(string operation, CommandStreams streams)
    {
        streams.Error.WriteLine(
            CliText.AgentWorktreeOperationUnknown(operation, CliUsage.AgentWorktree));
        return 2;
    }

    private static int Create(
        string[] rest,
        CommandStreams streams,
        TextReader input,
        bool inputRedirected)
    {
        var (operand, root) = Parse(rest);
        var asked = operand;
        var fromClient = false;
        if (asked is null)
        {
            if (!inputRedirected)
            {
                streams.Error.WriteLine(
                    CliText.AgentWorktreeBranchMissing(CliUsage.AgentWorktreeCreate));
                return 2;
            }

            var payload = Payload(input);
            asked = Field(payload, "name");
            root ??= Field(payload, "cwd");
            if (asked is null)
            {
                streams.Error.WriteLine(CliText.AgentWorktreePayloadFieldMissing("name"));
                return 2;
            }

            fromClient = true;
        }

        var checkout = Checkout(root ?? Environment.CurrentDirectory);
        if (checkout is null)
        {
            streams.Error.WriteLine(
                CliText.RepositoryOutsideGit(root ?? Environment.CurrentDirectory));
            return 2;
        }

        var branch = fromClient ? BranchFromSlug(checkout, asked) : asked.Trim();
        if (!IsUsableBranch(checkout, branch))
        {
            streams.Error.WriteLine(CliText.AgentWorktreeBranchNotUsable(asked));
            return 2;
        }

        var home = Home(checkout);
        if (home is null)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeRootUnknown(checkout));
            return 2;
        }

        var target = Path.GetFullPath(Path.Combine(home, Flatten(branch)));
        var registered = Registered(checkout);

        // The branch first: git keeps a branch in one worktree at a time, so if it has one
        // anywhere, that one is the answer -- or, for the main checkout, the refusal.
        if (registered.FirstOrDefault(one => Names(one, branch)) is { } owner)
        {
            if (SamePath(owner.Path, checkout))
            {
                streams.Error.WriteLine(
                    CliText.AgentWorktreeBranchInMainCheckout(branch, checkout));
                return 2;
            }

            if (!SamePath(owner.Path, target))
            {
                streams.Error.WriteLine(CliText.AgentWorktreeBranchElsewhere(
                    branch,
                    Path.GetFullPath(owner.Path),
                    target));
            }

            streams.Out.WriteLine(Path.GetFullPath(owner.Path));
            return 0;
        }

        // Then the directory. Two branches can flatten to one name -- `feature/hooks` and
        // `feature-hooks` -- and Windows compares paths without case, so `fix/a` and `Fix/A` do
        // too. Handing back the other branch's worktree would put the work on the wrong branch.
        if (registered.FirstOrDefault(one => SamePath(one.Path, target)) is { } holder)
        {
            streams.Error.WriteLine(
                CliText.AgentWorktreePathHoldsAnotherBranch(target, BranchOf(holder) ?? branch));
            return 2;
        }

        if (Directory.Exists(target))
        {
            streams.Error.WriteLine(CliText.AgentWorktreePathOccupied(target));
            return 2;
        }

        try
        {
            Directory.CreateDirectory(home);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            streams.Error.WriteLine(
                CliText.AgentWorktreeRootNotWritable(home, failure.Message));
            return 2;
        }

        if (Add(checkout, target, branch) is { } refused)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeGitRefused(refused));
            return 2;
        }

        streams.Error.WriteLine(CliText.AgentWorktreeCreated(target, branch));
        streams.Out.WriteLine(target);
        return 0;
    }

    /// <summary>
    /// Attaches to the branch when it exists, and lets git create it otherwise -- from the
    /// remote's default branch where that ref is at hand, which is the "start clean" default the
    /// client applies itself, and from local HEAD when it is not.
    /// </summary>
    private static string? Add(string checkout, string target, string branch)
    {
        if (Git(checkout, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).Ok)
        {
            return Git(checkout, "worktree", "add", target, branch) is { Ok: false } refused
                ? refused.Error
                : null;
        }

        var head = Git(checkout, "rev-parse", "--verify", "--quiet", "origin/HEAD");
        var start = head.Ok && head.Output.Length > 0 ? head.Output : "HEAD";
        return Git(checkout, "worktree", "add", "-b", branch, target, start) is { Ok: false } failed
            ? failed.Error
            : null;
    }

    private static int Remove(
        string[] rest,
        CommandStreams streams,
        TextReader input,
        bool inputRedirected)
    {
        var (operand, _) = Parse(rest);
        var named = operand;
        if (named is null)
        {
            if (!inputRedirected)
            {
                streams.Error.WriteLine(
                    CliText.AgentWorktreePathMissing(CliUsage.AgentWorktreeRemove));
                return 2;
            }

            // `worktree_path` is what the client sends -- observed from this machine on
            // 2026-09-11 and stated by the hooks reference. `path` is read as well because an
            // earlier payload recorded here carried that spelling, and a removal that refuses
            // because a field was renamed leaves a worktree behind on every session exit.
            var payload = Payload(input);
            named = Field(payload, "worktree_path") ?? Field(payload, "path");
            if (named is null)
            {
                streams.Error.WriteLine(
                    CliText.AgentWorktreePayloadFieldMissing("worktree_path"));
                return 2;
            }
        }

        var target = Path.GetFullPath(named);
        if (!Directory.Exists(target))
        {
            // Nothing to do rather than a fault: a client may well ask twice, and the second ask
            // is not an error to report at the person.
            return 0;
        }

        var checkout = Checkout(target);
        if (checkout is null ||
            SamePath(checkout, target) ||
            !Registered(checkout).Any(one => SamePath(one.Path, target)))
        {
            streams.Error.WriteLine(CliText.AgentWorktreeNotAWorktree(target));
            return 2;
        }

        // Asked here rather than left to `git worktree remove` refusing, so the reason given is
        // the one that is true: git's own refusal covers several causes in one sentence.
        var state = Git(target, "status", "--porcelain");
        if (!state.Ok || state.Output.Length > 0)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeHoldsWork(target));
            return 2;
        }

        if (Git(checkout, "worktree", "remove", target) is { Ok: false } refused)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeGitRefused(refused.Error));
            return 2;
        }

        streams.Error.WriteLine(CliText.AgentWorktreeRemoved(target));
        return 0;
    }

    /// <summary>
    /// Where this repository's worktrees live: a configured root, or the sibling of the
    /// repository. Null when the repository is at the top of a drive and has no sibling.
    /// </summary>
    private static string? Home(string checkout)
    {
        var configured = Git(checkout, "config", "--get", RootSetting);
        var root = configured.Ok && configured.Output.Length > 0
            ? Path.IsPathRooted(configured.Output)
                ? configured.Output
                : Path.Combine(checkout, configured.Output)
            : Path.GetDirectoryName(checkout.TrimEnd('/', '\\')) is { } parent
                ? Path.Combine(parent, WorktreesDirectory)
                : null;
        return root is null
            ? null
            : Path.GetFullPath(Path.Combine(root, Path.GetFileName(checkout.TrimEnd('/', '\\'))));
    }

    /// <summary>
    /// What a client's slug means as a branch. A slug carrying a known type gains the slash the
    /// naming rule wants; anything else is taken as it is, so a generated name like
    /// <c>bold-oak-a3f2</c> stays itself and an existing branch stays reachable.
    /// </summary>
    private static string BranchFromSlug(string checkout, string slug)
    {
        var name = slug.Trim();
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return name;
        }

        if (Git(checkout, "rev-parse", "--verify", "--quiet", "refs/heads/" + name).Ok)
        {
            return name;
        }

        var separator = name.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0 &&
            BranchTypes.Contains(name[..separator], StringComparer.Ordinal) &&
            separator + 1 < name.Length)
        {
            return name[..separator] + "/" + name[(separator + 1)..];
        }

        return name;
    }

    private static string Flatten(string branch) => branch.Replace('/', '-');

    /// <summary>
    /// Whether git will take this as a branch, and Windows as a directory.
    ///
    /// <c>check-ref-format</c> is asked in its full-ref form rather than with <c>--branch</c>:
    /// <c>--branch</c> first expands the previous-checkout syntax <c>@{-1}</c>, and this string
    /// arrives from a client. The two remaining checks are the places where git's rules and the
    /// file system's differ: a leading dash reads as an option, and characters legal in a ref are
    /// not all legal in a file name.
    /// </summary>
    private static bool IsUsableBranch(string checkout, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            branch.StartsWith('-') ||
            Flatten(branch).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return Git(checkout, "check-ref-format", "refs/heads/" + branch).Ok;
    }

    /// <summary>The main checkout, not whichever worktree the caller happens to sit in.</summary>
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

    private static IReadOnlyList<GitWorktree> Registered(string checkout)
    {
        var listed = Git(checkout, "worktree", "list", "--porcelain");
        return listed.Ok ? WorktreeInventory.ParsePorcelain(listed.Output) : [];
    }

    /// <summary>Porcelain records a branch as <c>refs/heads/&lt;name&gt;</c>.</summary>
    private static string? BranchOf(GitWorktree worktree) =>
        worktree.Branch is null
            ? null
            : worktree.Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? worktree.Branch["refs/heads/".Length..]
                : worktree.Branch;

    private static bool Names(GitWorktree worktree, string branch) =>
        string.Equals(BranchOf(worktree), branch, StringComparison.Ordinal);

    private static bool SamePath(string one, string other) =>
        string.Equals(
            Path.GetFullPath(one).TrimEnd('/', '\\'),
            Path.GetFullPath(other).TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);

    private static JsonElement? Payload(TextReader input)
    {
        try
        {
            var text = input.ReadToEnd();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Field(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } document &&
        document.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static (string? Operand, string? Root) Parse(string[] rest)
    {
        string? operand = null;
        string? root = null;
        for (var index = 0; index < rest.Length; index++)
        {
            if (rest[index] == "--root" && index + 1 < rest.Length)
            {
                root = rest[++index];
            }
            else if (operand is null && !rest[index].StartsWith("--", StringComparison.Ordinal))
            {
                operand = rest[index];
            }
        }

        return (operand, root);
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
