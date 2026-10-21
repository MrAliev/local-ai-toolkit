using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Repository;
using Xunit;

namespace CodeSearch.Tests;

[CollectionDefinition("Git executable isolation", DisableParallelization = true)]
public sealed class GitExecutableIsolationCollection { }

[Collection("Git executable isolation")]
public sealed class GitExecutableLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LocalAi-git-executable-" + Guid.NewGuid().ToString("N"));
    private readonly string host;
    private readonly string repository;
    private static string Executable => OperatingSystem.IsWindows() ? "git.exe" : "git";

    public GitExecutableLocatorTests()
    {
        host = Directory.CreateDirectory(Path.Combine(root, "host")).FullName;
        repository = Directory.CreateDirectory(Path.Combine(root, "repository")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
    }

    private string Installation(string relative)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, relative)).FullName;
        File.WriteAllText(Path.Combine(directory, Executable), "installation fixture");
        return directory;
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    public void Relative_or_empty_path_entries_are_not_installations(string entry)
    {
        File.WriteAllText(Path.Combine(host, Executable), "cwd fixture");
        Directory.CreateDirectory(Path.Combine(host, "relative"));
        File.WriteAllText(Path.Combine(host, "relative", Executable), "relative fixture");
        Assert.Null(GitExecutableLocator.Find(entry, host, repository));
    }

    [Fact]
    public void Quoted_portable_installation_with_spaces_is_supported()
    {
        var installation = Installation("portable git with spaces");
        Assert.Equal(Path.Combine(installation, Executable),
            GitExecutableLocator.Find('"' + installation + '"', host, repository));
    }

    [Fact]
    public void Explicit_absolute_installation_can_also_be_the_non_project_cwd()
    {
        var installation = Installation("portable");
        Assert.Equal(Path.Combine(installation, Executable),
            GitExecutableLocator.Find(installation, installation, repository));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Project_path_entries_are_skipped_including_linked_worktrees(bool gitFile)
    {
        if (gitFile)
        {
            Directory.Delete(Path.Combine(repository, ".git"));
            File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: fixture");
        }
        var unsafeDirectory = Installation("repository/tools");
        var installation = Installation("portable");
        var path = unsafeDirectory + Path.PathSeparator + installation;
        Assert.Equal(Path.Combine(installation, Executable),
            GitExecutableLocator.Find(path, host, repository));
    }

    [Fact]
    public void Sibling_with_shared_name_prefix_is_not_part_of_the_project()
    {
        var installation = Installation("repository-tools");
        Assert.Equal(Path.Combine(installation, Executable),
            GitExecutableLocator.Find(installation, host, repository));
    }

    [Fact]
    public void Nested_git_marker_does_not_hide_the_outer_project()
    {
        var nested = Directory.CreateDirectory(Path.Combine(repository, "nested")).FullName;
        Directory.CreateDirectory(Path.Combine(nested, ".git"));
        var projectTools = Installation("repository/tools");
        Assert.Null(GitExecutableLocator.Find(projectTools, host, nested));
    }

    [Fact]
    public void Directory_alias_into_a_project_is_not_an_installation()
    {
        var projectTools = Installation("repository/tools");
        var alias = Path.Combine(root, "outside-alias");
        DirectoryLink(alias, projectTools);
        Assert.Null(GitExecutableLocator.Find(alias, host, repository));
    }

    [Fact]
    public void Executable_alias_into_a_project_is_not_an_installation()
    {
        var projectTools = Installation("repository/tools");
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        FileLink(Path.Combine(outside, Executable), Path.Combine(projectTools, Executable));
        Assert.Null(GitExecutableLocator.Find(outside, host, repository));
    }

    [Fact]
    public void Installation_alias_outside_the_project_resolves_to_the_physical_executable()
    {
        var installation = Installation("portable");
        var alias = Path.Combine(root, "portable-alias");
        DirectoryLink(alias, installation);
        Assert.Equal(Path.Combine(installation, Executable),
            GitExecutableLocator.Find(alias, host, repository));
    }

    [Fact]
    public void Working_directory_alias_does_not_hide_its_project_root()
    {
        var child = Directory.CreateDirectory(Path.Combine(repository, "child")).FullName;
        var alias = Path.Combine(root, "working-alias");
        DirectoryLink(alias, child);
        var projectTools = Installation("repository/tools");
        Assert.Null(GitExecutableLocator.Find(projectTools, host, alias));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepoLocator_does_not_execute_project_git_from_cwd_or_path(bool binaryOutput)
    {
        var realGit = GitExecutableLocator.Resolve(host);
        var oldCwd = Environment.CurrentDirectory;
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        const string marker = "LOCALAI_UNTRUSTED_GIT_EXECUTED";
        var fake = Path.Combine(repository, Executable);
        if (OperatingSystem.IsWindows())
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake);
        else
        {
            File.WriteAllText(fake, "#!/bin/sh\necho " + marker + "\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            Environment.CurrentDirectory = repository;
            Environment.SetEnvironmentVariable("PATH", repository + Path.PathSeparator + Path.GetDirectoryName(realGit));
            if (binaryOutput)
                Assert.Throws<InvalidOperationException>(() =>
                    RepoLocator.GitOutputBytesOrThrow(host, new[] { "/d", "/c", "echo", marker }));
            else
                Assert.Null(RepoLocator.GitOutput(host, "/d /c echo " + marker));

            var version = binaryOutput
                ? System.Text.Encoding.UTF8.GetString(RepoLocator.GitOutputBytesOrThrow(host, new[] { "--version" }))
                : RepoLocator.GitOutput(host, "--version");
            Assert.StartsWith("git version ", version);

            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Assert.Null(RepoLocator.GitOutput(host, "/d /c echo " + marker));
            Assert.Throws<InvalidOperationException>(() =>
                RepoLocator.GitOutputBytesOrThrow(host, new[] { "/d", "/c", "echo", marker }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.CurrentDirectory = oldCwd;
        }
    }

    [Fact]
    public async Task GitClient_ignores_project_executable_and_uses_installed_git()
    {
        var realGit = GitExecutableLocator.Resolve(host);
        var start = new ProcessStartInfo(realGit)
        {
            WorkingDirectory = host,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--quiet");
        using (var process = Process.Start(start)!)
        {
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken),
                process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        var fake = Path.Combine(repository, Executable);
        if (OperatingSystem.IsWindows())
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), fake);
        else
        {
            File.WriteAllText(fake, "#!/bin/sh\necho LOCALAI_UNTRUSTED_GIT_EXECUTED\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var oldCwd = Environment.CurrentDirectory;
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = repository;
            Environment.SetEnvironmentVariable("PATH", repository + Path.PathSeparator + Path.GetDirectoryName(realGit));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var common = await new GitClient().GetCommonDirectoryAsync(host, deadline.Token);
            Assert.Equal(Path.Combine(host, ".git"), common);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.CurrentDirectory = oldCwd;
        }
    }

    [Fact]
    public async Task GitClient_reports_missing_installation_and_preserves_missing_directory_error()
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var client = new GitClient();
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                client.GetCommonDirectoryAsync(host, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                client.GetCommonDirectoryAsync(Path.Combine(host, "absent"), TestContext.Current.CancellationToken));
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    [Fact]
    public void Windows_directory_junction_cannot_supply_project_git()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Directory junctions are a Windows filesystem feature.");
        var tools = Path.Combine(repository, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, Executable), string.Empty);
        var junction = Path.Combine(host, "linked-tools");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{junction}\" \"{tools}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000), "Creating a directory junction timed out.");
        Assert.Equal(0, process.ExitCode);
        try { Assert.Null(GitExecutableLocator.Find(junction, host, repository)); }
        finally { Directory.Delete(junction, recursive: false); }
    }

    private static void DirectoryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        { Assert.Skip("Creating a directory symbolic link requires a Windows privilege."); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Assert.Skip("Creating a directory symbolic link is not permitted."); }
    }

    private static void FileLink(string path, string target)
    {
        try { File.CreateSymbolicLink(path, target); }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        { Assert.Skip("Creating a file symbolic link requires a Windows privilege."); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Assert.Skip("Creating a file symbolic link is not permitted."); }
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Unexpected test cleanup root.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
}
