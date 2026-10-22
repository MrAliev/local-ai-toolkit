using System.Diagnostics;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.Broker.Tests;

public sealed class GitHubCliPhysicalPathTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("localai-gh-physical-").FullName;
    private readonly List<string> directoryLinks = [];
    private readonly List<string> fileLinks = [];
    private string Project => Path.Combine(root, "project");
    private string Tools => Path.Combine(Project, "tools");
    private string Cwd => Path.Combine(Project, "workspace");
    private string Trusted => Path.Combine(root, "trusted");
    private static string Executable => OperatingSystem.IsWindows() ? "gh.exe" : "gh";
    public static bool IsPosix => !OperatingSystem.IsWindows();

    public GitHubCliPhysicalPathTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        Directory.CreateDirectory(Cwd);
        CopyNativeFixture(Tools);
        CopyNativeFixture(Trusted);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("intermediate-directory")]
    [InlineData("cwd-alias")]
    [InlineData("cwd-link-out-path-back")]
    [InlineData("project-link-out")]
    public async Task Project_aliases_are_refused_before_starting_a_process(string route)
    {
        var alias = Path.Combine(root, "alias");
        var cwd = Cwd;
        string path;
        switch (route)
        {
            case "directory":
                await LinkDirectory(alias, Tools);
                path = alias;
                break;
            case "intermediate-directory":
                await LinkDirectory(alias, Project);
                path = Path.Combine(alias, "tools");
                break;
            case "cwd-alias":
                await LinkDirectory(alias, Cwd);
                cwd = alias;
                path = Tools;
                break;
            case "cwd-link-out-path-back":
                var outside = Directory.CreateDirectory(Path.Combine(root, "external-cwd")).FullName;
                cwd = Path.Combine(Project, "linked-cwd");
                await LinkDirectory(cwd, outside);
                await LinkDirectory(alias, Tools);
                path = alias;
                break;
            default:
                path = Path.Combine(Project, "external-tools");
                await LinkDirectory(path, Trusted);
                break;
        }
        var marker = Path.Combine(root, "refused.pid");
        var exception = Assert.Throws<UpdateCheckSourceException>(() => StartInfo(path, cwd, marker));
        Assert.Equal(UpdateCheckFailure.AuthenticationUnavailable, exception.Failure);
        Assert.False(File.Exists(marker));
        await AssertNativeRun(Trusted, Cwd, Path.Combine(Trusted, Executable));
    }

    [Fact]
    public async Task An_alias_to_an_independent_installation_runs_the_resolved_native_path()
    {
        var alias = Path.Combine(root, "installed-alias");
        await LinkDirectory(alias, Trusted);
        await AssertNativeRun(alias, Cwd, Path.Combine(Trusted, Executable));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broken_or_cyclic_links_are_refused_and_a_later_trusted_entry_still_works(bool cycle)
    {
        var alias = Path.Combine(root, "invalid-alias");
        await LinkDirectory(alias, cycle ? alias : Path.Combine(root, "missing"));
        Assert.Throws<UpdateCheckSourceException>(() => StartInfo(alias, Cwd, Path.Combine(root, "invalid.pid")));
        await AssertNativeRun(string.Join(Path.PathSeparator, alias, Trusted), Cwd, Path.Combine(Trusted, Executable));
    }

    [Theory(SkipUnless = nameof(IsPosix), Skip = "Creating Windows file symlinks requires a host privilege; directory junctions are covered on Windows.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_links_apply_the_same_project_boundary_and_resolve_independent_targets(bool projectTarget)
    {
        var alias = Directory.CreateDirectory(Path.Combine(root, "file-alias")).FullName;
        var link = Path.Combine(alias, Executable);
        File.CreateSymbolicLink(link, Path.Combine(projectTarget ? Tools : Trusted, Executable));
        fileLinks.Add(link);
        if (projectTarget)
        {
            Assert.Throws<UpdateCheckSourceException>(() => StartInfo(alias, Cwd, Path.Combine(root, "file-refused.pid")));
            await AssertNativeRun(Trusted, Cwd, Path.Combine(Trusted, Executable));
        }
        else
        {
            await AssertNativeRun(alias, Cwd, Path.Combine(Trusted, Executable));
        }
    }

    private static void CopyNativeFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var extension = Path.GetExtension(source);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        }
        var apphost = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "LocalAi.Broker.Tests.exe" : "LocalAi.Broker.Tests");
        var destination = Path.Combine(directory, Executable);
        File.Copy(apphost, destination);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(apphost));
    }

    private static ProcessStartInfo StartInfo(string path, string cwd, string marker)
    {
        var start = GitHubCliUpdateSource.CreateStartInfo(["api", "--hostname", "github.com"], path, cwd,
            TestContext.Current.CancellationToken);
        start.Environment["LOCALAI_GH_NATIVE_MODE"] = "exit";
        start.Environment["LOCALAI_GH_NATIVE_MARKER"] = marker;
        return start;
    }

    private async Task AssertNativeRun(string path, string cwd, string expectedExecutable)
    {
        var marker = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pid");
        var start = StartInfo(path, cwd, marker);
        Assert.Equal(expectedExecutable, start.FileName);
        Assert.Equal(Path.GetDirectoryName(expectedExecutable), start.WorkingDirectory);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(watchdog.Token);
            var error = process.StandardError.ReadToEndAsync(watchdog.Token);
            await process.WaitForExitAsync(watchdog.Token);
            await Task.WhenAll(output, error);
            Assert.Equal(23, process.ExitCode);
            Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(marker));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private async Task LinkDirectory(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
        }
        else
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // mklink and /J are fixed builtin syntax: keep the renderer's validation
                // and quoting of paths, removing quotes only around those fixed tokens.
                Arguments = WindowsCommandScript.Arguments("mklink", ["/J", path, target], "Unsafe fixture path.")
                    .Replace("\"mklink\"", "mklink", StringComparison.Ordinal)
                    .Replace("\"/J\"", "/J", StringComparison.Ordinal)
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, (await output) + (await error));
        }
        directoryLinks.Add(path);
    }

    public void Dispose()
    {
        foreach (var link in fileLinks) File.Delete(link);
        foreach (var link in directoryLinks) Directory.Delete(link);
        OwnedProcessDirectoryCleanup.Delete(root);
    }
}
