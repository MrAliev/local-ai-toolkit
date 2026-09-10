using System.Diagnostics;
using System.Text;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A worktree belongs at <c>&lt;repository&gt;\.worktrees\&lt;name&gt;</c>, in the repository's own
/// root, because two clients have to arrive at the same one. Claude Code's default puts them under
/// <c>.claude</c>, which is one client's directory and invisible to the other; a home directory
/// puts them somewhere neither the repository nor a second clone knows about.
///
/// What is under test is the behaviour both callers depend on: the path, the two rules written
/// into the repository's own <c>.gitignore</c> so that a second checkout inside the working tree
/// is not an untracked directory, and the refusals. The hook face reads a payload on standard
/// input and prints the path and nothing else, because that is the client's contract; the face a
/// person types takes an operand. Both print the same bytes on standard output, so the contract
/// holds by construction rather than by remembering it when the next message is added.
/// </summary>
public sealed class AWorktreeLandsInTheRepositoryRootTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-worktree-" + Guid.NewGuid().ToString("N"));

    public AWorktreeLandsInTheRepositoryRootTests()
    {
        Directory.CreateDirectory(_root);
        Git("init --quiet .");
        Git("config user.email tests@localai");
        Git("config user.name Tests");
        File.WriteAllText(Path.Combine(_root, "README.md"), "sample\n");
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

    [Fact]
    public void Create_puts_the_worktree_in_the_repository_root()
    {
        var run = Create("issue-523");

        Assert.Equal(0, run.Exit);
        Assert.Equal(Expected("issue-523"), run.Out.Trim());
        Assert.True(Directory.Exists(Expected("issue-523")));
    }

    [Fact]
    public void Create_prints_the_path_and_nothing_else_on_standard_output()
    {
        var run = Create("issue-523");

        Assert.Equal(Expected("issue-523"), run.Out.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Git_lists_what_was_created_as_a_worktree()
    {
        Create("issue-523");

        Assert.Contains(
            Registered(),
            path => string.Equals(
                Path.GetFullPath(path),
                Expected("issue-523"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Both_folders_kept_in_the_root_are_written_into_gitignore()
    {
        Create("issue-523");

        var written = Ignored();
        Assert.Contains(".worktrees/", written);
        Assert.Contains(".localai/", written);
    }

    /// <summary>
    /// The product's name is not left in somebody's repository for ever. <c>.gitignore</c> is
    /// committed, and by decision an uninstall does not take these rules back out, so a header
    /// nothing ever reads again would outlive the installation that wrote it.
    /// </summary>
    [Fact]
    public void No_header_of_ours_is_left_in_the_repositorys_gitignore()
    {
        Create("issue-523");

        Assert.DoesNotContain("LocalAi", File.ReadAllText(Path.Combine(_root, ".gitignore")));
    }

    /// <summary>
    /// The file belongs to the repository. Rewriting it whole would decide its line endings on
    /// the reader's behalf and put every line of it in their next diff -- the mistake
    /// <c>HookInstaller.Exclude</c> records having made with <c>.git/info/exclude</c>.
    /// </summary>
    [Fact]
    public void An_existing_gitignore_keeps_its_lines_and_its_endings()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\r\nobj/\r\n");

        Create("issue-523");

        var text = File.ReadAllText(Path.Combine(_root, ".gitignore"));
        Assert.Contains("bin/", Ignored());
        Assert.Contains("obj/", Ignored());
        Assert.DoesNotContain('\n', text.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void Each_rule_is_written_once_however_often_the_command_runs()
    {
        Create("one");
        Create("two");

        Assert.Equal(1, Ignored().Count(line => line == ".worktrees/"));
        Assert.Equal(1, Ignored().Count(line => line == ".localai/"));
    }

    /// <summary>
    /// The second client asking for the worktree the first one made is this command's purpose,
    /// not a collision: it answers with the path it already has and exits 0.
    /// </summary>
    [Fact]
    public void Asking_again_for_the_same_worktree_answers_with_it()
    {
        var first = Create("issue-523");
        var second = Create("issue-523");

        Assert.Equal(0, second.Exit);
        Assert.Equal(first.Out.Trim(), second.Out.Trim());
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("   ")]
    public void A_name_that_is_not_one_directory_creates_nothing(string name)
    {
        var run = Create(name);

        Assert.Equal(2, run.Exit);
        Assert.Equal(string.Empty, run.Out.Trim());
        Assert.False(Directory.Exists(Path.Combine(_root, ".worktrees")));
    }

    /// <summary>
    /// Waiting on input that will never arrive is the mistake <c>AgentGateNeedsPayload</c> was
    /// written for. With no operand and nobody piping anything in, this refuses instead.
    /// </summary>
    [Fact]
    public void With_no_name_and_nobody_piping_anything_in_it_refuses()
    {
        var run = Run(["create", "--root", _root], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.Contains("localai agent worktree create", run.Error);
    }

    [Fact]
    public void The_name_can_arrive_in_a_hook_payload_instead()
    {
        var run = Run(
            ["create"],
            payload: "{\"name\":\"issue-523\",\"cwd\":" + Quoted(_root) +
                ",\"hook_event_name\":\"WorktreeCreate\"}",
            inputRedirected: true);

        Assert.Equal(0, run.Exit);
        Assert.Equal(Expected("issue-523"), run.Out.Trim());
    }

    [Fact]
    public void Remove_takes_the_worktree_away_and_says_nothing_on_standard_output()
    {
        var path = Create("issue-523").Out.Trim();

        var run = Run(["remove", path], payload: null, inputRedirected: false);

        Assert.Equal(0, run.Exit);
        Assert.Equal(string.Empty, run.Out);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void The_path_can_arrive_in_a_hook_payload_instead()
    {
        var path = Create("issue-523").Out.Trim();

        var run = Run(
            ["remove"],
            payload: "{\"path\":" + Quoted(path) + ",\"hook_event_name\":\"WorktreeRemove\"}",
            inputRedirected: true);

        Assert.Equal(0, run.Exit);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// Losing work at session exit would cost far more than a directory left on disk, so removal
    /// refuses rather than forcing. There is no <c>--force</c> on this command.
    /// </summary>
    [Fact]
    public void A_worktree_that_still_holds_work_is_kept()
    {
        var path = Create("issue-523").Out.Trim();
        File.WriteAllText(Path.Combine(path, "unsaved.txt"), "work nobody committed\n");

        var run = Run(["remove", path], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Removal_leaves_the_main_checkout_alone()
    {
        Create("issue-523");

        var run = Run(["remove", _root], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.True(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public void An_operation_it_does_not_know_is_refused()
    {
        var run = Run(["sprout", "issue-523"], payload: null, inputRedirected: false);

        Assert.Equal(2, run.Exit);
        Assert.Equal(string.Empty, run.Out.Trim());
    }

    // -- helpers ---------------------------------------------------------------

    private sealed record Outcome(int Exit, string Out, string Error);

    private Outcome Create(string name) =>
        Run(["create", name, "--root", _root], payload: null, inputRedirected: false);

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

    private string Expected(string name) =>
        Path.GetFullPath(Path.Combine(_root, ".worktrees", name));

    private IReadOnlyList<string> Ignored()
    {
        var path = Path.Combine(_root, ".gitignore");
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(line => line.Trim()).ToArray()
            : [];
    }

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
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
