using System.Diagnostics;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// What differs between the tree the published index was built from and the tree in front of us.
///
/// Both halves in one question: the commits made on this branch since, and the edits not committed
/// at all. `git diff` against a tree compares it with the working copy directly, so one call covers
/// both; untracked files need the second.
/// </summary>
public sealed class WhatChangedSinceTheIndexWasBuiltTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-changed-since-" + Guid.NewGuid().ToString("N"));

    private string _baseTree = string.Empty;

    public WhatChangedSinceTheIndexWasBuiltTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "tests@example.invalid");
        Git("config", "user.name", "Tests");
        Write("Kept.cs", "class Kept { }");
        Write("Edited.cs", "class Edited { }");
        Git("add", ".");
        Git("commit", "-m", "first");
        _baseTree = Git("rev-parse", "HEAD^{tree}").Trim();
    }

    [Fact]
    public void A_committed_change_since_the_base_counts()
    {
        Write("Edited.cs", "class Edited { public int Added() => 1; }");
        Git("add", ".");
        Git("commit", "-m", "second");

        Assert.Equal(["Edited.cs"], RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree));
    }

    [Fact]
    public void An_uncommitted_change_counts()
    {
        Write("Edited.cs", "class Edited { public int Added() => 1; }");

        Assert.Equal(["Edited.cs"], RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree));
    }

    [Fact]
    public void An_untracked_file_counts()
    {
        Write("Fresh.cs", "class Fresh { }");

        Assert.Equal(["Fresh.cs"], RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree));
    }

    [Fact]
    public void A_tree_that_matches_reports_nothing()
    {
        Assert.Empty(RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree)!);
    }

    /// <summary>
    /// A tree hash git does not know is not "nothing changed": answering that would scope a
    /// rebuild to nothing at all. Null is the caller's signal to rebuild everything.
    /// </summary>
    [Fact]
    public void A_tree_git_does_not_know_is_not_an_answer()
    {
        Assert.Null(RuntimeIndexLayout.GetPathsChangedSince(
            _root,
            "0000000000000000000000000000000000000000"));
    }

    [Fact]
    public void The_earlier_text_of_a_file_comes_back()
    {
        Write("Edited.cs", "class Edited { public int Added() => 1; }");

        var before = RuntimeIndexLayout.GetTextAt(_root, _baseTree, "Edited.cs");

        Assert.Equal("class Edited { }", before?.TrimEnd());
    }

    [Fact]
    public void A_file_that_did_not_exist_then_has_no_earlier_text()
    {
        Assert.Null(RuntimeIndexLayout.GetTextAt(_root, _baseTree, "Fresh.cs"));
    }

    [Theory]
    [InlineData("TokenService.cs", "edit")]
    [InlineData("TokenService.cs", "add")]
    [InlineData("TokenService.cs", "delete")]
    [InlineData("SecretStore.cs", "edit")]
    [InlineData("Credentials.cs", "edit")]
    public void Tracked_source_changes_invalidate_both_indexes_despite_sensitive_names(
        string name,
        string change)
    {
        if (change != "add")
        {
            Write(name, "public class Service { public int Value => 1; }");
            Git("add", name);
            Git("commit", "-m", "tracked source");
            _baseTree = Git("rev-parse", "HEAD^{tree}").Trim();
            Assert.Contains(name, FileScanner.Enumerate(_root));
        }

        if (change == "delete")
        {
            File.Delete(Path.Combine(_root, name));
        }
        else
        {
            Write(name, "public class Service { public int Value => 2; }");
            if (change == "add")
            {
                Git("add", name);
            }
            Assert.Contains(name, FileScanner.Enumerate(_root));
        }

        Assert.Equal([name], RuntimeIndexLayout.GetDirtyPaths(_root));
        Assert.Equal([name], RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree));
        var identity = RuntimeIndexLayout.Inspect(_root, Path.Combine(_root, "runtime"));
        Assert.NotNull(identity.DirtyHash);
        Assert.NotNull(identity.SemanticDirtyHash);

        Git("add", "-A");
        Git("commit", "-m", "changed source");
        Assert.Empty(RuntimeIndexLayout.GetDirtyPaths(_root));
        Assert.Equal([name], RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree));
    }

    [Fact]
    public void Untracked_sensitive_files_remain_outside_the_dirty_corpus()
    {
        Write("credentials.json", "{\"fixture\":true}");
        Write("TokenService.cs", "public class TokenService { }");
        Write(".gitignore", "Ignored.cs");
        Write("Ignored.cs", "public class Ignored { }");

        Assert.Empty(RuntimeIndexLayout.GetDirtyPaths(_root));
        Assert.Empty(RuntimeIndexLayout.GetPathsChangedSince(_root, _baseTree)!);
    }

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_root, relative), content + Environment.NewLine);

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
