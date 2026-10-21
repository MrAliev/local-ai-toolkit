using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.Broker.Tests;

public sealed class GitHubCliExecutableTrustTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-gh-trust-" + Guid.NewGuid().ToString("N"));
    private string Executable => OperatingSystem.IsWindows() ? "gh.exe" : "gh";

    [Fact]
    public void Cwd_hijack_is_ignored_and_the_native_process_gets_an_absolute_path_and_safe_directory()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(root, "untrusted-project")).FullName;
        var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted-bin")).FullName;
        File.WriteAllText(Path.Combine(cwd, Executable), "CWD fixture: never execute this file.");
        var projectBin = Directory.CreateDirectory(Path.Combine(cwd, "bin")).FullName;
        File.WriteAllText(Path.Combine(projectBin, Executable), "Project fixture: never execute this file.");
        var expected = Path.Combine(trusted, Executable);
        File.WriteAllText(expected, "PATH fixture: inspect the selected path only.");
        var path = string.Join(Path.PathSeparator, "", ".", "relative-bin", cwd, projectBin, trusted);
        var start = GitHubCliUpdateSource.CreateStartInfo(["api"], path, cwd);
        Assert.Equal(expected, start.FileName);
        Assert.True(Path.IsPathFullyQualified(start.FileName));
        Assert.Equal(trusted, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void No_absolute_path_candidate_means_no_process_instead_of_operating_system_search()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(root, "untrusted-project")).FullName;
        File.WriteAllText(Path.Combine(cwd, Executable), "Never execute a CWD fixture.");
        var exception = Assert.Throws<UpdateCheckSourceException>(() =>
            GitHubCliUpdateSource.CreateStartInfo(["api"], string.Join(Path.PathSeparator, "", ".", "relative-bin", cwd), cwd));
        Assert.Equal(UpdateCheckFailure.AuthenticationUnavailable, exception.Failure);
    }

    [Fact]
    public void Launching_from_a_filesystem_root_still_allows_an_explicit_installed_binary_path()
    {
        var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted-bin")).FullName;
        var expected = Path.Combine(trusted, Executable);
        File.WriteAllText(expected, "Inspect this PATH fixture only.");
        Assert.Equal(expected, GitHubCliUpdateSource.CreateStartInfo([], trusted, Path.GetPathRoot(root)!).FileName);
    }

    public void Dispose() => OwnedProcessDirectoryCleanup.Delete(root);
}
