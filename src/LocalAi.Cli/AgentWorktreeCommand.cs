using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSearch.Core.Indexing;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Cli;

/// <summary>
/// Makes and drops the worktree a branch is worked in, at
/// <c>&lt;repository&gt;\.worktrees\&lt;name&gt;</c>.
///
/// The place is the point. Claude Code's own default is <c>&lt;repository&gt;\.claude\worktrees</c>,
/// which belongs to one client and means Codex opening the same branch gets a different directory
/// — two checkouts of one branch, each with its own uncommitted state. In the repository's root
/// both arrive at the same one, and so does a person with a terminal.
///
/// Two callers, one behaviour. A client's hook sends a JSON payload on standard input; a person
/// types the name. Standard output carries the created path and nothing else, on both faces,
/// because that is the client's contract for <c>WorktreeCreate</c> — and making it a property of
/// the command rather than a rule to remember is what stops the next message added here from
/// breaking it. Every sentence goes to standard error, removal included.
/// </summary>
internal static class AgentWorktreeCommand
{
    /// <summary>
    /// The folder worktrees live in, and the rules that keep git from seeing them.
    ///
    /// A second checkout inside the working tree is an untracked directory: it turns up in
    /// <c>git status</c>, is swept into <c>git add -A</c>, and trips everything that refuses to
    /// act on a dirty tree. <c>.localai/</c> joins it because the same root holds this
    /// repository's indexes and cache, and a repository is better served gaining both rules in
    /// one edit than growing an untracked cache directory later.
    /// </summary>
    private const string WorktreesDirectory = ".worktrees";

    private static readonly string[] IgnoreRules = [".worktrees/", ".localai/"];

    /// <summary>Seconds. A git that has not answered by now is not going to.</summary>
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
        var name = operand;
        if (name is null)
        {
            if (!inputRedirected)
            {
                streams.Error.WriteLine(
                    CliText.AgentWorktreeNameMissing(CliUsage.AgentWorktreeCreate));
                return 2;
            }

            var payload = Payload(input);
            name = Field(payload, "name");
            root ??= Field(payload, "cwd");
            if (name is null)
            {
                streams.Error.WriteLine(CliText.AgentWorktreePayloadFieldMissing("name"));
                return 2;
            }
        }

        if (!IsOneDirectoryName(name))
        {
            streams.Error.WriteLine(CliText.AgentWorktreeNameNotUsable(name));
            return 2;
        }

        var checkout = Checkout(root ?? Environment.CurrentDirectory);
        if (checkout is null)
        {
            streams.Error.WriteLine(
                CliText.RepositoryOutsideGit(root ?? Environment.CurrentDirectory));
            return 2;
        }

        var target = Path.GetFullPath(Path.Combine(checkout, WorktreesDirectory, name));

        // Before the directory exists, so git never sees an untracked second checkout -- not even
        // for the moment between creating it and writing the rule.
        Ignore(checkout, streams);

        if (Registered(checkout).Any(path => SamePath(path, target)))
        {
            // The second client asking for what the first one made is the case this command
            // exists for, not a collision. It gets the path and a zero exit.
            streams.Out.WriteLine(target);
            return 0;
        }

        if (Directory.Exists(target))
        {
            streams.Error.WriteLine(CliText.AgentWorktreePathOccupied(target));
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var added = Add(checkout, target, name);
        if (added is not null)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeGitRefused(added));
            return 2;
        }

        streams.Out.WriteLine(target);
        return 0;
    }

    /// <summary>
    /// Attaches the worktree to the branch of that name when there is one, and lets git create it
    /// otherwise -- from the remote's default branch when that ref is at hand, which is the
    /// "start clean" default this replaces, and from local HEAD when it is not.
    /// </summary>
    private static string? Add(string checkout, string target, string name)
    {
        if (Git(checkout, "rev-parse", "--verify", "--quiet", "refs/heads/" + name).Ok)
        {
            return Git(checkout, "worktree", "add", target, name) is { Ok: false } refused
                ? refused.Error
                : null;
        }

        var head = Git(checkout, "rev-parse", "--verify", "--quiet", "origin/HEAD");
        var start = head.Ok && head.Output.Length > 0 ? head.Output : "HEAD";
        return Git(checkout, "worktree", "add", "-b", name, target, start) is { Ok: false } failed
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

            named = Field(Payload(input), "path");
            if (named is null)
            {
                streams.Error.WriteLine(CliText.AgentWorktreePayloadFieldMissing("path"));
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
            !Registered(checkout).Any(path => SamePath(path, target)))
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
    /// Adds the two rules to the repository's own <c>.gitignore</c>, once each.
    ///
    /// Appended rather than rewritten, in the shape <c>HookInstaller.Exclude</c> settled on: the
    /// file belongs to the repository, and reading it as lines and joining them back rewrote
    /// every line of a CRLF file to add two patterns -- harmless to git and the whole of the
    /// reader's next diff. No header of ours goes in: `.gitignore` is committed, an uninstall
    /// deliberately does not take these rules back out, and a marker nothing ever reads again
    /// would outlive the installation that wrote it.
    ///
    /// A failure to write is said and carried on from. Refusing a worktree because a comment file
    /// is read-only would cost more than the untracked entries do.
    /// </summary>
    private static void Ignore(string checkout, CommandStreams streams)
    {
        var path = Path.Combine(checkout, ".gitignore");
        string existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeIgnoreNotWritten(
                string.Join(", ", IgnoreRules),
                path,
                failure.Message));
            return;
        }

        var newLine = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = existing.Length == 0
            ? []
            : existing.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList();
        var missing = IgnoreRules
            .Where(rule => !lines.Any(line => Means(line, rule)))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        lines.AddRange(missing);
        try
        {
            File.WriteAllText(
                path,
                string.Join(newLine, lines) + newLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            streams.Error.WriteLine(CliText.AgentWorktreeIgnoreNotWritten(
                string.Join(", ", missing),
                path,
                failure.Message));
            return;
        }

        streams.Error.WriteLine(CliText.AgentWorktreeIgnoreAdded(
            string.Join(", ", missing),
            path));
    }

    /// <summary>Every way of writing one rule, so a hand-written one is recognised, not doubled.</summary>
    private static bool Means(string line, string rule)
    {
        var written = line.Trim();
        var bare = rule.TrimEnd('/');
        return written == rule ||
            written == "/" + rule ||
            written == bare ||
            written == "/" + bare;
    }

    /// <summary>
    /// The name becomes a directory under <c>.worktrees</c>, and it arrives from a client. A
    /// separator or <c>..</c> in it would put the worktree anywhere on the disk.
    /// </summary>
    private static bool IsOneDirectoryName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 &&
            trimmed is not ("." or "..") &&
            trimmed == Path.GetFileName(trimmed) &&
            trimmed.IndexOfAny(['/', '\\']) < 0 &&
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
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

        var (parent, tail) = (
            Path.GetDirectoryName(common.Trim().TrimEnd('/', '\\')),
            Path.GetFileName(common.Trim().TrimEnd('/', '\\')));
        return tail == ".git" && parent is not null ? Path.GetFullPath(parent) : null;
    }

    private static IReadOnlyList<string> Registered(string checkout)
    {
        var listed = Git(checkout, "worktree", "list", "--porcelain");
        return listed.Ok
            ? WorktreeInventory.ParsePorcelain(listed.Output).Select(one => one.Path).ToArray()
            : [];
    }

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
            return new GitRun(
                process.ExitCode == 0,
                output.Result.Trim(),
                error.Result.Trim());
        }
        catch (Exception failure) when (failure is IOException or SystemException)
        {
            return new GitRun(false, string.Empty, failure.Message);
        }
    }
}
