using System.Diagnostics;
using System.Text;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The overlay a working root keeps for itself lives under <c>.localai</c>, and git is told not
/// to look at it.
///
/// It used to live under <c>.claude</c>, which is one client's directory: Codex opening the same
/// checkout would neither write nor find it, and the product's own name says whose file it is.
/// The docstring that path carried admitted the rest of the problem -- it "requires
/// <c>.claude/</c> to be git-ignored, or a multi-megabyte binary lands in someone's commit",
/// which is a requirement on somebody else's repository rather than anything this code did.
///
/// So the rule is written into <c>.git/info/exclude</c> rather than into <c>.gitignore</c>: that
/// file is per-clone and is never committed, so the index cannot reach a remote by way of it, and
/// no file belonging to the repository is edited at all.
/// </summary>
public sealed class AnOverlayStaysOutOfTheRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-overlay-" + Guid.NewGuid().ToString("N"));

    public AnOverlayStaysOutOfTheRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--quiet", ".");
        Git("config", "user.email", "tests@localai");
        Git("config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(_root, "README.md"), "sample\n");
        Git("add", "README.md");
        Git("commit", "--quiet", "-m", "initial");
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

    [Fact]
    public void The_overlay_is_kept_under_localai()
    {
        var path = RepoLocator.OverlayPathFor(FsPath.From(_root));

        Assert.Equal(
            Path.Combine(_root, ".localai", "codesearch", "overlay.cidx"),
            path);
    }

    [Fact]
    public void The_client_specific_folder_is_not_used_any_more()
    {
        var path = RepoLocator.OverlayPathFor(FsPath.From(_root));

        Assert.DoesNotContain(".claude", path);
    }

    [Fact]
    public void Git_does_not_see_the_overlay_folder()
    {
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));
        WriteOverlay();

        Assert.DoesNotContain(".localai", Status());
    }

    /// <summary>
    /// `.gitignore` is committed and would carry the rule to every clone of somebody's
    /// repository; `.git/info/exclude` is this clone's own and goes nowhere.
    /// </summary>
    [Fact]
    public void No_file_belonging_to_the_repository_is_edited()
    {
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));
        WriteOverlay();

        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.Equal(string.Empty, Status().Trim());
    }

    [Fact]
    public void What_the_exclude_file_already_held_is_kept()
    {
        var exclude = Path.Combine(_root, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        File.WriteAllText(exclude, "# mine\r\nscratch/\r\n");

        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));

        var written = File.ReadAllText(exclude);
        Assert.Contains("# mine", written);
        Assert.Contains("scratch/", written);
        Assert.DoesNotContain("\n\n", written.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_rule_is_written_once_however_often_it_is_asked_for()
    {
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));

        var written = File.ReadAllLines(Path.Combine(_root, ".git", "info", "exclude"));
        Assert.Equal(1, written.Count(line => line.Trim() == "/.localai/"));
    }

    /// <summary>
    /// `info/exclude` lives in the common directory, so one rule covers every worktree of the
    /// repository -- which is what makes this work for the second checkout a branch gets.
    /// </summary>
    [Fact]
    public void A_worktree_of_the_same_repository_is_covered_too()
    {
        RepoLocator.EnsureOverlayIgnored(FsPath.From(_root));
        var worktree = Path.Combine(Path.GetTempPath(), "localai-overlay-wt-" + Guid.NewGuid().ToString("N"));
        Git("worktree", "add", "--quiet", "-b", "probe", worktree, "HEAD");

        try
        {
            Directory.CreateDirectory(Path.Combine(worktree, ".localai", "codesearch"));
            File.WriteAllText(
                Path.Combine(worktree, ".localai", "codesearch", "overlay.cidx"), "binary");

            Assert.DoesNotContain(".localai", GitIn(worktree, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            Git("worktree", "remove", "--force", worktree);
        }
    }

    [Fact]
    public void Worktree_paths_are_passed_as_literal_git_arguments()
    {
        var characters = OperatingSystem.IsWindows() ? "space & caret^ (group)" : "space \"quote\" & caret^ (group)";
        var parent = Directory.CreateTempSubdirectory("localai-overlay-argv-").FullName;
        var worktree = Path.Combine(parent, "localai-overlay-wt-" + characters + "-" + Guid.NewGuid().ToString("N"));
        var expected = Path.GetFullPath(worktree);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] ListedWorktrees() => Git("worktree", "list", "--porcelain", "-z").Split('\0')
            .Where(value => value.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(value => Path.GetFullPath(value[9..])).ToArray();
        try
        {
            Git("worktree", "add", "--quiet", "-b", "literal-argument-probe", worktree, "HEAD");
            Assert.True(File.Exists(Path.Combine(worktree, ".git")));
            Assert.True(string.Equals(expected, Path.GetFullPath(GitIn(worktree, "rev-parse", "--show-toplevel").Trim()), comparison));
            Assert.Contains(ListedWorktrees(), path => string.Equals(expected, path, comparison));
            Git("worktree", "remove", "--force", worktree);
            Assert.False(Directory.Exists(worktree));
            Assert.DoesNotContain(ListedWorktrees(), path => string.Equals(expected, path, comparison));
        }
        finally
        {
            var ownedPrefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            foreach (var path in ListedWorktrees().Where(path => path.StartsWith(ownedPrefix, comparison)))
                Git("worktree", "remove", "--force", path);
            Directory.Delete(parent, recursive: true);
        }
    }

    // -- helpers ---------------------------------------------------------------

    private void WriteOverlay()
    {
        var path = RepoLocator.OverlayPathFor(FsPath.From(_root));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "binary");
    }

    private string Status() => Git("status", "--porcelain", "--untracked-files=all");

    private string Git(params string[] arguments) => GitIn(_root, arguments);

    private static string GitIn(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output;
    }
}
