using System.Security.Cryptography;
using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher.Tests;

public sealed class UpdateHandoffTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task Update_runs_outside_active_version_without_holding_its_lease(int exitCode)
    {
        using var install = TestInstall.CreateComplete("v1", "v2");
        install.ReplaceTool("v1", "localai.exe", Environment.GetEnvironmentVariable("ComSpec")!);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var child = new WaitingChild(install.Root);
        var application = new LauncherApplication(
            install.BinRoot,
            Path.Combine(install.BinRoot, "launcher", "localai-launcher.exe"));
        var running = application.RunAsync(
            "localai",
            child.Arguments(exitCode),
            TestContext.Current.CancellationToken);
        try
        {
            await child.WaitUntilStartedAsync();
            using (VersionLease.AcquireExclusive(
                       Path.Combine(install.BinRoot, "current.lock"), TimeSpan.Zero))
            {
                Assert.False(running.IsCompleted);
            }

            var childDetails = File.ReadAllLines(child.Signal);
            var stagedExecutable = childDetails[0];
            Assert.Equal(
                Path.Combine(install.BinRoot, "launcher", "localai-launcher.exe"),
                childDetails[1]);
            Assert.Equal("v1", childDetails[2]);
            Assert.DoesNotContain(install.VersionsRoot, stagedExecutable, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(stagedExecutable));
            var activator = new VersionActivator(
                install.BinRoot,
                new LocalAiProcessController(),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
            activator.Activate(
                "v2",
                stopRunning: true,
                CurrentPointerExpectation.ExactSha256(SHA256.HashData(before)));
            Assert.False(running.IsCompleted);
            Assert.Equal("v2", new VersionResolver(install.BinRoot).ReadCurrent().Version);

            child.Release();
            Assert.Equal(exitCode, await running);
            Assert.False(Directory.Exists(Path.GetDirectoryName(stagedExecutable)));
        }
        finally
        {
            child.Release();
            await running;
        }
    }

    [Fact]
    public async Task Cancelling_update_stops_its_child_and_removes_only_the_temporary_handoff()
    {
        using var install = TestInstall.CreateComplete("v1");
        install.ReplaceTool("v1", "localai.exe", Environment.GetEnvironmentVariable("ComSpec")!);
        install.WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var before = File.ReadAllBytes(install.CurrentPath);
        using var child = new WaitingChild(install.Root);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var application = new LauncherApplication(
            install.BinRoot,
            Path.Combine(install.BinRoot, "launcher", "localai-launcher.exe"));
        var running = application.RunAsync("localai", child.Arguments(0), cancellation.Token);
        try
        {
            await child.WaitUntilStartedAsync();
            var stagedExecutable = File.ReadAllLines(child.Signal)[0];
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

            Assert.False(Directory.Exists(Path.GetDirectoryName(stagedExecutable)));
            Assert.Equal(before, File.ReadAllBytes(install.CurrentPath));
            Assert.True(File.Exists(Path.Combine(install.VersionDirectory("v1"), "localai.exe")));
            using var exclusive = VersionLease.AcquireExclusive(
                Path.Combine(install.BinRoot, "current.lock"), TimeSpan.Zero);
        }
        finally
        {
            cancellation.Cancel();
            child.Release();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class WaitingChild : IDisposable
    {
        private readonly string _release;

        public WaitingChild(string root)
        {
            Signal = Path.Combine(root, "started.txt");
            _release = Path.Combine(root, "release.txt");
        }

        public string Signal { get; }

        // cmd.exe ignores the leading command name before /c, letting the same real child
        // exercise the launcher's public "localai update" dispatch without installing anything.
        public string[] Arguments(int exitCode)
        {
            // Read only this helper's parent; no installed LocalAI process is queried or stopped.
            var script = $$"""
                $ProgressPreference = 'SilentlyContinue'
                $parentId = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
                $details = @((Get-Process -Id $parentId).Path, $env:LOCALAI_LAUNCHER_PATH, $env:LOCALAI_ACTIVE_VERSION)
                [IO.File]::WriteAllLines('{{Signal.Replace("'", "''")}}', [string[]] $details)
                while (-not (Test-Path -LiteralPath '{{_release.Replace("'", "''")}}')) {
                    Start-Sleep -Milliseconds 25
                }
                exit {{exitCode}}
                """;
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            return
            [
                "update", "/d", "/c",
                $"powershell.exe -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}",
            ];
        }

        public async Task WaitUntilStartedAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(Signal) || new FileInfo(Signal).Length == 0)
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        public void Release() => File.WriteAllText(_release, "release");

        public void Dispose() => Release();
    }
}
