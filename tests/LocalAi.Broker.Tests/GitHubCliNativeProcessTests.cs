using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.Broker.Tests;

// The fixture changes only this test process's environment/CWD. Serialize it with all other
// collections; the controlled child is our own compiled test apphost, never a found binary.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitHubCliProcessCollection
{
    public const string Name = "GitHub CLI native boundary";
}

[Collection(GitHubCliProcessCollection.Name)]
public sealed class GitHubCliNativeProcessTests(GitHubCliNativeFixture fixture)
    : IClassFixture<GitHubCliNativeFixture>
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private static readonly string[] Arguments = ["api", "--hostname", "github.com", "--method", "GET", "repos/test/project"];

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Uses the Windows native apphost boundary.")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task Native_stream_cutoff_stops_the_owned_process_and_returns_a_bounded_reason(string mode)
    {
        using var environment = fixture.Enter(mode);
        var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() =>
            GitHubCliUpdateSource.ExecuteAsync(Arguments, 65536, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateCheckFailure.InvalidResponse, exception.Failure);
        await environment.AssertStoppedAsync();
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Uses the Windows native apphost boundary.")]
    public async Task Native_caller_cancellation_stops_the_owned_process_without_becoming_no_release()
    {
        using var environment = fixture.Enter("wait");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2)); // deadlock watchdog, not a timing assertion
        var running = GitHubCliUpdateSource.ExecuteAsync(Arguments, 65536, cancellation.Token);
        while (!environment.Started)
        {
            if (running.IsCompleted) await running;
            await Task.Delay(10, cancellation.Token);
        }
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await environment.AssertStoppedAsync();
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Uses the Windows native apphost boundary.")]
    public async Task Native_request_deadline_stops_the_owned_process_and_reports_timeout()
    {
        using var environment = fixture.Enter("wait");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2)); // protects the suite if the production deadline breaks
        var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() =>
            GitHubCliUpdateSource.ExecuteAsync(Arguments, 65536, cancellation.Token));
        Assert.Equal(UpdateCheckFailure.Timeout, exception.Failure);
        await environment.AssertStoppedAsync();
    }
}

public sealed class GitHubCliNativeFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-gh-native-" + Guid.NewGuid().ToString("N"));
    private string Trusted => Path.Combine(root, "trusted-bin");
    private string Project => Path.Combine(root, "project");

    public GitHubCliNativeFixture()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Trusted);
        Directory.CreateDirectory(Project);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(Trusted, Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(AppContext.BaseDirectory, "LocalAi.Broker.Tests.exe"), Path.Combine(Trusted, "gh.exe"));
        File.WriteAllText(Path.Combine(Project, "gh.exe"), "Untrusted CWD fixture: never execute.");
    }

    public EnvironmentScope Enter(string mode) => new(Trusted, Project, Path.Combine(root, Guid.NewGuid().ToString("N") + ".pid"), mode);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) OwnedProcessDirectoryCleanup.Delete(root);
    }

    public sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
        private readonly string previousDirectory = Environment.CurrentDirectory;
        private readonly string executable;
        private readonly string marker;

        public EnvironmentScope(string trusted, string project, string marker, string mode)
        {
            executable = Path.Combine(trusted, "gh.exe");
            this.marker = marker;
            Set("PATH", trusted);
            Set("LOCALAI_GH_NATIVE_MODE", mode);
            Set("LOCALAI_GH_NATIVE_MARKER", marker);
            Environment.CurrentDirectory = project;
        }

        public bool Started => ReadPid() is not null;

        public async Task AssertStoppedAsync()
        {
            Assert.True(Started, "The controlled native helper must actually have started.");
            using var process = OpenOwnedProcess();
            if (process is null) return;
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            watchdog.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(watchdog.Token);
            Assert.True(process.HasExited);
        }

        private void Set(string name, string value)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        private int? ReadPid()
        {
            try { return int.TryParse(File.ReadAllText(marker), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null; }
            catch (IOException) { return null; }
        }

        private Process? OpenOwnedProcess()
        {
            if (ReadPid() is not { } pid) return null;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            return null;
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = previousDirectory;
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            using var owned = OpenOwnedProcess();
            if (owned is not null && !owned.HasExited) owned.Kill(entireProcessTree: true);
        }
    }
}

internal static class ControlledGitHubCliNativeHelper
{
    [ModuleInitializer]
    internal static void RunOnlyForTheExplicitNativeFixture()
    {
        var arguments = Environment.GetCommandLineArgs();
        var mode = Environment.GetEnvironmentVariable("LOCALAI_GH_NATIVE_MODE");
        var marker = Environment.GetEnvironmentVariable("LOCALAI_GH_NATIVE_MARKER");
        if (arguments.Length < 4 || arguments[1] != "api" || arguments[2] != "--hostname" || arguments[3] != "github.com"
            || mode is not ("wait" or "stdout" or "stderr" or "exit") || string.IsNullOrWhiteSpace(marker))
            return;
        File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (mode == "exit") Environment.Exit(23);
        if (mode is "stdout" or "stderr")
        {
            using var stream = mode == "stdout" ? Console.OpenStandardOutput() : Console.OpenStandardError();
            stream.Write(new byte[mode == "stdout" ? 65537 : 8193]);
            stream.Flush();
        }
        Thread.Sleep(Timeout.Infinite);
    }
}
