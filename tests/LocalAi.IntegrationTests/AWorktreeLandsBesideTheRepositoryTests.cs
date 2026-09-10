using System.Diagnostics;
using System.Text;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A worktree belongs beside the repository, not inside it: `&lt;parent&gt;\worktrees\&lt;repository&gt;\
/// &lt;branch&gt;`. Inside the working tree it is an untracked second checkout that only an ignore
/// rule keeps out of the index, and JetBrains' IDEs document that nesting one there breaks their
/// worktree integration outright. Outside, nothing in the repository has to know about it at all.
///
/// The other half of what is pinned here is the branch. The operand is a branch name, the
/// directory is derived from it by flattening `/` to `-`, and a client's one-word slug is mapped
/// to a branch rather than taken as one -- so `feature-auth` from the client and
/// `feature/auth` from a person end up at the same worktree instead of two.
/// </summary>
public sealed class AWorktreeLandsBesideTheRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-worktree-" + Guid.NewGuid().ToString("N"));

    private readonly string _repository;

    public AWorktreeLandsBesideTheRepositoryTests()
    {
        // A parent of its own, because the worktrees root is a sibling of the repository and a
        // test must not write one into the machine's temp directory.
        _repository = Path.Combine(_root, "sample-repo");
        Directory.CreateDirectory(_repository);
        Git("init --quiet .");
        Git("config user.email tests@localai");
        Git("config user.name Tests");
        File.WriteAllText(Path.Combine(_repository, "README.md"), "sample\n");
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
            // A temporary directory left behind is not worth failing a run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -- where it lands --------------------------------------------------------

    [Fact]
    public void It_lands_beside_the_repository_under_its_name()
    {
        var run = Create("fix/version-line");

        Assert.Equal(0, run.Exit);
        Assert.Equal(Expected("fix-version-line"), run.Out.Trim());
        Assert.True(Directory.Exists(Expected("fix-version-line")));
    }

    [Fact]
    public void Nothing_is_created_inside_the_repository()
    {
        Create("fix/version-line");

        Assert.False(Directory.Exists(Path.Combine(_repository, ".worktrees")));
    }

    /// <summary>
    /// The whole point of moving out: the repository is not touched at all, so no ignore rule is
    /// load-bearing and no file of the reader's is edited.
    /// </summary>
    [Fact]
    public void The_repository_is_left_exactly_as_it_was()
    {
        Create("fix/version-line");

        Assert.Equal(string.Empty, Git("status --porcelain --untracked-files=all").Trim());
        Assert.False(File.Exists(Path.Combine(_repository, ".gitignore")));
    }

    [Fact]
    public void Git_lists_what_was_created_as_a_worktree()
    {
        Create("fix/version-line");

        Assert.Contains(
            Registered(),
            path => string.Equals(
                Path.GetFullPath(path),
                Expected("fix-version-line"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_path_and_nothing_else_goes_to_standard_output()
    {
        var run = Create("fix/version-line");

        Assert.Equal(Expected("fix-version-line"), run.Out.TrimEnd('\r', '\n'));
    }

    // -- the branch ------------------------------------------------------------

    [Fact]
    public void A_branch_with_a_type_prefix_is_created_under_that_name()
    {
        Create("fix/version-line");

        Assert.Contains("fix/version-line", Branches());
    }

    [Fact]
    public void The_branch_it_made_is_named_on_standard_error()
    {
        var run = Create("fix/version-line");

        Assert.Contains("fix/version-line", run.Error);
    }

    /// <summary>
    /// A client sends one slug and no slashes, so `feature-auth` has to become `feature/auth`:
    /// otherwise every worktree a client opens carries a branch that breaks the naming rule, and
    /// a person asking for `feature/auth` later gets a second worktree for the same work.
    /// </summary>
    [Fact]
    public void A_client_slug_with_a_known_type_becomes_a_prefixed_branch()
    {
        var run = FromPayload("{\"name\":\"feature-auth\",\"cwd\":" + Quoted(_repository) +
            ",\"hook_event_name\":\"WorktreeCreate\"}");

        Assert.Equal(0, run.Exit);
        Assert.Contains("feature/auth", Branches());
        Assert.Equal(Expected("feature-auth"), run.Out.Trim());
    }

    [Fact]
    public void A_generated_slug_is_taken_as_it_is()
    {
        var run = FromPayload("{\"name\":\"bold-oak-a3f2\",\"cwd\":" + Quoted(_repository) +
            ",\"hook_event_name\":\"WorktreeCreate\"}");

        Assert.Equal(0, run.Exit);
        Assert.Contains("bold-oak-a3f2", Branches());
    }

    [Fact]
    public void A_person_and_a_client_asking_for_one_branch_get_one_worktree()
    {
        var typed = Create("feature/auth");
        var fromClient = FromPayload("{\"name\":\"feature-auth\",\"cwd\":" + Quoted(_repository) +
            ",\"hook_event_name\":\"WorktreeCreate\"}");

        Assert.Equal(typed.Out.Trim(), fromClient.Out.Trim());
        Assert.Single(Registered(), path => path.Contains("feature-auth"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("feature/")]
    [InlineData("-dashed")]
    [InlineData("with space")]
    [InlineData("   ")]
    public void A_name_git_will_not_take_as_a_branch_creates_nothing(string branch)
    {
        var run = Create(branch);

        Assert.Equal(2, run.Exit);
        Assert.Equal(string.Empty, run.Out.Trim());
        Assert.False(Directory.Exists(Root()));
    }

    /// <summary>
    /// Windows compares paths case-insensitively, so `Fix/A` and `fix/a` want one directory.
    /// Handing back the other branch's worktree would put the work on the wrong branch, which is
    /// worse than a refusal.
    /// </summary>
    [Fact]
    public void A_directory_already_held_by_another_branch_is_refused()
    {
        Create("fix/a");

        var run = Create("Fix/A");

        Assert.Equal(2, run.Exit);
        Assert.Equal(string.Empty, run.Out.Trim());
    }

    [Fact]
    public void The_branch_of_the_main_checkout_is_refused()
    {
        var current = Git("rev-parse --abbrev-ref HEAD").Trim();

        var run = Create(current);

        Assert.Equal(2, run.Exit);
        Assert.Contains(current, run.Error);
    }

    // -- asking twice ----------------------------------------------------------

    [Fact]
    public void Asking_again_for_the_same_branch_answers_with_its_worktree()
    {
        var first = Create("fix/version-line");
        var second = Create("fix/version-line");

        Assert.Equal(0, second.Exit);
        Assert.Equal(first.Out.Trim(), second.Out.Trim());
    }

    [Fact]
    public void With_no_branch_and_nobody_piping_anything_in_it_refuses()
    {
        var run = Run(["create", "--root", _repository], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.Contains("localai agent worktree create", run.Error);
    }

    // -- removal ---------------------------------------------------------------

    /// <summary>
    /// The field is `worktree_path`. Observed from this machine's client on 2026-09-11, and the
    /// hooks reference says the same. The command read `path` until now, so every removal a
    /// client asked for refused with exit 2 -- and the test that was supposed to cover it built
    /// its payload from the same wrong assumption, so it passed and proved nothing.
    /// </summary>
    [Fact]
    public void The_path_arrives_from_the_client_in_worktree_path()
    {
        var path = Create("fix/version-line").Out.Trim();

        var run = Run(
            ["remove"],
            payload: "{\"worktree_path\":" + Quoted(path) +
                ",\"hook_event_name\":\"WorktreeRemove\"}",
            inputRedirected: true);

        Assert.Equal(0, run.Exit);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Remove_takes_the_worktree_away_and_says_nothing_on_standard_output()
    {
        var path = Create("fix/version-line").Out.Trim();

        var run = Run(["remove", path], payload: null, inputRedirected: false);

        Assert.Equal(0, run.Exit);
        Assert.Equal(string.Empty, run.Out);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void A_worktree_that_still_holds_work_is_kept()
    {
        var path = Create("fix/version-line").Out.Trim();
        File.WriteAllText(Path.Combine(path, "unsaved.txt"), "work nobody committed\n");

        var run = Run(["remove", path], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Removal_leaves_the_main_checkout_alone()
    {
        Create("fix/version-line");

        var run = Run(["remove", _repository], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.True(Directory.Exists(Path.Combine(_repository, ".git")));
    }

    [Fact]
    public void An_operation_it_does_not_know_is_refused()
    {
        var run = Run(["sprout", "fix/version-line"], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.Equal(string.Empty, run.Out.Trim());
    }

    // -- helpers ---------------------------------------------------------------

    private sealed record Outcome(int Exit, string Out, string Error);

    private Outcome Create(string branch) =>
        Run(["create", branch, "--root", _repository], payload: null, inputRedirected: false);

    private Outcome FromPayload(string payload) =>
        Run(["create"], payload, inputRedirected: true);

    private static Outcome Run(string[] args, string? payload, bool inputRedirected)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = AgentWorktreeCommand.Execute(
            args,
            new CommandStreams(output, error),
            payload is null ? TextReader.Null : new StringReader(payload),
            inputRedirected);
        return new Outcome(exit, output.ToString(), error.ToString());
    }

    /// <summary>`&lt;parent&gt;\worktrees\&lt;repository&gt;`, the root this layout puts them under.</summary>
    private string Root() => Path.Combine(_root, "worktrees", "sample-repo");

    private string Expected(string directory) =>
        Path.GetFullPath(Path.Combine(Root(), directory));

    private IEnumerable<string> Branches() =>
        Git("branch --format=%(refname:short)")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    private IEnumerable<string> Registered() =>
        Git("worktree list --porcelain")
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line[9..].Trim());

    private static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\") + "\"";

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
