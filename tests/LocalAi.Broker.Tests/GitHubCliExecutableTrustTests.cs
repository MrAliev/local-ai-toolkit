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
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted-bin")).FullName;
        File.WriteAllText(Path.Combine(cwd, Executable), "CWD fixture: never execute this file.");
        var projectBin = Directory.CreateDirectory(Path.Combine(cwd, "bin")).FullName;
        File.WriteAllText(Path.Combine(projectBin, Executable), "Project fixture: never execute this file.");
        var expected = Path.Combine(trusted, Executable);
        File.WriteAllText(expected, "PATH fixture: inspect the selected path only.");
        var path = string.Join(Path.PathSeparator, "", ".", "relative-bin", cwd, projectBin, trusted);
        var start = GitHubCliUpdateSource.CreateStartInfo(["api"], path, cwd, TestContext.Current.CancellationToken);
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
            GitHubCliUpdateSource.CreateStartInfo(["api"], string.Join(Path.PathSeparator, "", ".", "relative-bin", cwd), cwd, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateCheckFailure.AuthenticationUnavailable, exception.Failure);
    }

    [Fact]
    public void Launching_from_a_filesystem_root_still_allows_an_explicit_installed_binary_path()
    {
        var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted-bin")).FullName;
        var expected = Path.Combine(trusted, Executable);
        File.WriteAllText(expected, "Inspect this PATH fixture only.");
        Assert.Equal(expected, GitHubCliUpdateSource.CreateStartInfo([], trusted, Path.GetPathRoot(root)!, TestContext.Current.CancellationToken).FileName);
    }

    [Theory]
    [InlineData("user-home", "AppData/Local/Microsoft/WinGet/Links")]
    [InlineData("user-home", "scoop/shims")]
    [InlineData("Program Files", "GitHub CLI")]
    public void Installed_paths_are_not_rejected_just_because_cwd_is_a_parent_folder(string cwdName, string installation)
    {
        var cwd = Directory.CreateDirectory(Path.Combine(root, cwdName)).FullName;
        var installed = Directory.CreateDirectory(Path.Combine(cwd, installation.Replace('/', Path.DirectorySeparatorChar))).FullName;
        var executable = Path.Combine(installed, Executable);
        File.WriteAllText(executable, "Inspect this installed PATH fixture only.");
        Assert.Equal(executable, GitHubCliUpdateSource.CreateStartInfo([], installed, cwd, TestContext.Current.CancellationToken).FileName);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Exercises Windows UNC and mapped-drive normalization.")]
    [InlineData("//localai-not-contacted/share/tools", false)]
    [InlineData("N:\\tools", true)]
    public void Normalized_unc_and_mapped_network_paths_are_rejected_before_file_probes(string path, bool mapped)
    {
        var cwd = Directory.CreateDirectory(Path.Combine(root, "local-cwd")).FullName;
        var probed = false;
        var selected = GitHubCliExecutableLocator.Find(path, cwd,
            cancellationToken: TestContext.Current.CancellationToken,
            fileExists: _ => { probed = true; return true; },
            driveType: _ => mapped ? DriveType.Network : DriveType.Fixed);
        Assert.Null(selected);
        Assert.False(probed);
    }

    public void Dispose() => OwnedProcessDirectoryCleanup.Delete(root);
}
