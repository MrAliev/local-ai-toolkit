using System.Diagnostics;
using System.Text;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// What a merged pull request leaves on the machine: a local branch whose work is already in the
/// mainline, and the worktree it was checked out in. Nothing removes them today, so they pile up
/// until somebody does it by hand.
///
/// The hard part is recognising "already in the mainline" after a squash merge, which is how this
/// repository merges. Measured on git 2.55.0.windows.5, on a branch of two commits squashed into
/// main:
///
///   git branch --merged main          does not list it
///   git rev-list --count main..branch says 2
///   git cherry main branch            says `+ +`, meaning "not there"
///   git merge-tree --write-tree       exits 0 and prints main's own tree
///
/// Only the last one is true. `cherry` compares patch ids commit by commit, and a squash has the
/// patch id of none of them -- it answers correctly only when the branch had exactly one commit,
/// which is what made it look right at first. So containment is asked as "would merging this
/// change the mainline at all", and a branch that has no commit of its own is excluded
/// separately, because a branch sitting exactly on main answers that question the same way.
/// </summary>
public sealed class AMergedBranchLeavesNothingBehindTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-prune-" + Guid.NewGuid().ToString("N"));

    private readonly string _repository;
    private readonly string _runtime;

    public AMergedBranchLeavesNothingBehindTests()
    {
        _repository = Path.Combine(_root, "sample-repo");
        _runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(_runtime);
        Git("init --quiet --initial-branch=main .");
        Git("config user.email tests@localai");
        Git("config user.name Tests");
        Write("README.md", "sample\n");
        Git("add README.md");
        Git("commit --quiet -m initial");
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -- what goes ------------------------------------------------------------

    [Fact]
    public void A_squash_merged_branch_and_its_worktree_both_go()
    {
        var worktree = MergedBranchWithWorktree("feature/done");

        var run = Prune();

        Assert.Equal(0, run.Exit);
        Assert.DoesNotContain("feature/done", Branches());
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public void A_merged_branch_with_no_worktree_goes_too()
    {
        MergedBranch("feature/done");

        Prune();

        Assert.DoesNotContain("feature/done", Branches());
    }

    [Fact]
    public void What_went_is_named_with_its_short_sha()
    {
        var head = MergedBranch("feature/done");

        var run = Prune();

        Assert.Contains("feature/done", run.Error);
        Assert.Contains(head[..7], run.Error);
    }

    // -- what stays -----------------------------------------------------------

    [Fact]
    public void A_branch_carrying_work_of_its_own_stays()
    {
        OpenBranch("feature/open");

        Prune();

        Assert.Contains("feature/open", Branches());
    }

    /// <summary>
    /// A branch created a moment ago sits exactly on the mainline, so "merging it would change
    /// nothing" is true of it as well. Without a second condition every such branch would be
    /// deleted by the next pull that touched the mainline.
    /// </summary>
    [Fact]
    public void A_branch_with_no_commit_of_its_own_stays()
    {
        Git("branch feature/fresh main");

        var run = Prune();

        Assert.Contains("feature/fresh", Branches());
        Assert.Contains("feature/fresh", run.Error);
    }

    [Fact]
    public void The_mainline_itself_stays()
    {
        MergedBranch("feature/done");

        Prune();

        Assert.Contains("main", Branches());
    }

    [Fact]
    public void The_branch_the_command_is_running_on_stays()
    {
        MergedBranch("feature/done");
        Git("checkout --quiet feature/done");

        Prune();

        Assert.Contains("feature/done", Branches());
    }

    [Fact]
    public void A_worktree_that_still_holds_work_keeps_its_branch()
    {
        var worktree = MergedBranchWithWorktree("feature/done");
        File.WriteAllText(Path.Combine(worktree, "unsaved.txt"), "work nobody committed\n");

        var run = Prune();

        Assert.Contains("feature/done", Branches());
        Assert.True(Directory.Exists(worktree));
        Assert.Contains("feature/done", run.Error);
    }

    // -- rehearsal ------------------------------------------------------------

    [Fact]
    public void A_dry_run_removes_nothing()
    {
        var worktree = MergedBranchWithWorktree("feature/done");

        var run = Prune("--dry-run");

        Assert.Equal(0, run.Exit);
        Assert.Contains("feature/done", Branches());
        Assert.True(Directory.Exists(worktree));
        Assert.Contains("feature/done", run.Error);
    }

    [Fact]
    public void With_nothing_to_remove_it_says_so_and_changes_nothing()
    {
        OpenBranch("feature/open");

        var run = Prune();

        Assert.Equal(0, run.Exit);
        Assert.Contains("feature/open", Branches());
    }

    // -- refusals -------------------------------------------------------------

    [Fact]
    public void An_argument_it_does_not_understand_removes_nothing()
    {
        MergedBranch("feature/done");

        var run = Prune("--sweep");

        Assert.Equal(2, run.Exit);
        Assert.Contains("feature/done", Branches());
    }

    [Fact]
    public void Outside_a_repository_it_refuses()
    {
        var elsewhere = Path.Combine(_root, "not-a-repository");
        Directory.CreateDirectory(elsewhere);

        var run = Run(["--root", elsewhere]);

        Assert.Equal(2, run.Exit);
    }

    // -- after a pull ---------------------------------------------------------

    /// <summary>
    /// Off until the repository asks for it. An installed hook was invited to keep an index in
    /// sync; deleting branches is not something it may start doing because the tool updated.
    /// </summary>
    [Fact]
    public void A_pull_removes_nothing_until_the_repository_asks_for_it()
    {
        MergedBranch("feature/done");

        var error = new StringWriter();
        RepoPruneCommand.OnPostMerge(_repository, _runtime, error);

        Assert.Contains("feature/done", Branches());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Once_asked_for_a_pull_removes_the_merged_branch()
    {
        var worktree = MergedBranchWithWorktree("feature/done");
        Git("config localai.pruneMerged true");

        var error = new StringWriter();
        RepoPruneCommand.OnPostMerge(_repository, _runtime, error);

        Assert.DoesNotContain("feature/done", Branches());
        Assert.False(Directory.Exists(worktree));
        Assert.Contains("feature/done", error.ToString());
    }

    /// <summary>
    /// And a pull that cleans nothing says nothing at all. A line on every pull is a hook people
    /// uninstall, and the reasons a branch was kept are for somebody who asked, not for somebody
    /// who typed `git pull`.
    /// </summary>
    [Fact]
    public void A_pull_with_nothing_to_clean_is_silent()
    {
        OpenBranch("feature/open");
        Git("config localai.pruneMerged true");

        var error = new StringWriter();
        RepoPruneCommand.OnPostMerge(_repository, _runtime, error);

        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("feature/open", Branches());
    }

    /// <summary>
    /// `git config --type=bool` is what reads the setting, so the spellings git itself accepts
    /// work. Compared as a raw string, `on` would be silently off.
    /// </summary>
    [Fact]
    public void The_setting_is_read_the_way_git_reads_a_boolean()
    {
        MergedBranch("feature/done");
        Git("config localai.pruneMerged on");

        var error = new StringWriter();
        RepoPruneCommand.OnPostMerge(_repository, _runtime, error);

        Assert.DoesNotContain("feature/done", Branches());
    }

    // -- helpers --------------------------------------------------------------

    private sealed record Outcome(int Exit, string Out, string Error);

    private Outcome Prune(params string[] options) =>
        Run([.. options, "--root", _repository]);

    private Outcome Run(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = RepoPruneCommand.Execute(args, new CommandStreams(output, error), _runtime);
        return new Outcome(exit, output.ToString(), error.ToString());
    }

    /// <summary>A branch of two commits, squash-merged into main and left behind.</summary>
    private string MergedBranch(string branch)
    {
        Git($"checkout --quiet -b {branch} main");
        Write("first.txt", "first\n");
        Git("add first.txt");
        Git("commit --quiet -m first");
        Write("second.txt", "second\n");
        Git("add second.txt");
        Git("commit --quiet -m second");
        var head = Git("rev-parse HEAD").Trim();
        Git("checkout --quiet main");
        Git($"merge --squash {branch}");
        Git($"commit --quiet -m \"Add the feature (#1)\"");
        return head;
    }

    private string MergedBranchWithWorktree(string branch)
    {
        MergedBranch(branch);
        var worktree = Path.Combine(_root, "worktrees", branch.Replace('/', '-'));
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        Git($"worktree add --quiet \"{worktree}\" {branch}");
        return worktree;
    }

    private void OpenBranch(string branch)
    {
        Git($"checkout --quiet -b {branch} main");
        Write("open.txt", "unmerged\n");
        Git("add open.txt");
        Git("commit --quiet -m open");
        Git("checkout --quiet main");
    }

    private IEnumerable<string> Branches() =>
        Git("branch --format=%(refname:short)")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    private void Write(string relative, string text) =>
        File.WriteAllText(Path.Combine(_repository, relative), text);

    private string Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
