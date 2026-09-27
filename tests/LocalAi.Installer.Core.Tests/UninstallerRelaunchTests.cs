using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Removal;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Apps &amp; features starts the uninstaller's copy parked inside the installation, and Windows
/// will not delete an executable that is running, nor a directory that is some process's current
/// directory. So the copy starts a second copy of itself from outside the installation and
/// steps aside before anything is touched: the removal then runs from somewhere it is not
/// deleting, and the whole of <c>bin</c> can go.
/// </summary>
public sealed class UninstallerRelaunchTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private string TemporaryRoot => Path.Combine(machine.Root, "temp");

    private string InstalledCopy => Path.Combine(
        machine.Layout.BinRoot,
        UninstallRegistration.UninstallerDirectoryName,
        UninstallRegistration.UninstallerFileName);

    public UninstallerRelaunchTests()
    {
        Directory.CreateDirectory(TemporaryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledCopy)!);
        File.WriteAllText(InstalledCopy, "the installer itself");
    }

    public void Dispose() => machine.Dispose();

    [Fact]
    public void The_installed_copy_starts_a_copy_outside_the_installation_with_the_same_arguments()
    {
        var processes = new RecordingProcessFactory();

        var outcome = Relaunch(processes).Relaunch(InstalledCopy, ["--uninstall", "/extra arg"]);

        Assert.Equal(UninstallerRelaunchOutcome.Relaunched, outcome);
        var started = processes.Started[0];
        Assert.Equal(["--uninstall", "/extra arg"], started.Arguments);
        Assert.False(Inside(machine.Layout.Root, started.FileName), started.FileName);
        Assert.True(Inside(TemporaryRoot, started.FileName), started.FileName);
        Assert.Equal("the installer itself", File.ReadAllText(started.FileName));
        Assert.False(started.UseShellExecute);
        // The copy in the installation is left exactly as it was: it goes with the removal.
        Assert.Equal("the installer itself", File.ReadAllText(InstalledCopy));
    }

    [Fact]
    public void An_installer_already_outside_the_installation_runs_where_it_is()
    {
        var downloaded = Path.Combine(machine.Root, "Downloads", "LocalAi.Installer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        File.WriteAllText(downloaded, "the installer itself");
        var processes = new RecordingProcessFactory();

        var outcome = Relaunch(processes).Relaunch(downloaded, ["--uninstall"]);

        Assert.Equal(UninstallerRelaunchOutcome.NotNeeded, outcome);
        Assert.Empty(processes.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(TemporaryRoot));
    }

    [Fact]
    public void A_copy_that_cannot_be_started_is_reported_and_leaves_nothing_behind()
    {
        var processes = new RecordingProcessFactory
        {
            Failure = new Win32Exception(5, "Access is denied."),
        };

        var outcome = Relaunch(processes).Relaunch(InstalledCopy, ["--uninstall"]);

        Assert.Equal(UninstallerRelaunchOutcome.Failed, outcome);
        Assert.Single(processes.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(TemporaryRoot));
        Assert.True(File.Exists(InstalledCopy));
    }

    /// <summary>
    /// A temporary directory inside the installation would put the copy back inside the tree it
    /// removes — where it would relaunch again, forever.
    /// </summary>
    [Fact]
    public void A_temporary_directory_inside_the_installation_is_not_used()
    {
        var inside = Path.Combine(machine.Layout.Root, "temp");
        Directory.CreateDirectory(inside);
        var processes = new RecordingProcessFactory();

        var outcome = new UninstallerRelaunch(machine.Layout, inside, processes)
            .Relaunch(InstalledCopy, ["--uninstall"]);

        Assert.Equal(UninstallerRelaunchOutcome.Failed, outcome);
        Assert.Empty(processes.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public async Task A_copy_that_exits_takes_its_directory_with_it()
    {
        using var processes = new RealProcesses();

        var outcome = Relaunch(processes).Relaunch(RunnableInstalledCopy(), ["/d", "/c", "exit 0"]);

        Assert.Equal(UninstallerRelaunchOutcome.Relaunched, outcome);
        await processes.WaitForAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFileSystemEntries(TemporaryRoot));
    }

    /// <summary>Ended from Task Manager, or crashed: no code of its own runs at the end.</summary>
    [Fact]
    public async Task A_copy_that_is_ended_takes_its_directory_with_it()
    {
        using var processes = new RealProcesses();

        var outcome = Relaunch(processes).Relaunch(RunnableInstalledCopy(), RunsUntilEnded);
        Assert.Equal(UninstallerRelaunchOutcome.Relaunched, outcome);
        processes.Started[0].KillTree();

        await processes.WaitForAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFileSystemEntries(TemporaryRoot));
    }

    /// <summary>
    /// A directory that is some process's current directory cannot be removed, and whatever the
    /// removal starts inherits the copy's — a broker that outlives it would keep it.
    /// </summary>
    [Fact]
    public void Nothing_the_handover_starts_runs_inside_the_directory_that_has_to_go()
    {
        var processes = new RecordingProcessFactory();

        Relaunch(processes).Relaunch(InstalledCopy, ["--uninstall"]);

        var copyDirectory = Path.GetDirectoryName(processes.Started[0].FileName)!;
        Assert.All(processes.Started, started =>
        {
            Assert.False(string.IsNullOrEmpty(started.WorkingDirectory), started.FileName);
            Assert.False(Inside(copyDirectory, started.WorkingDirectory), started.WorkingDirectory);
            Assert.False(Inside(machine.Layout.Root, started.WorkingDirectory), started.WorkingDirectory);
        });
    }

    /// <summary>
    /// Once the copy runs, the handover has happened: reporting a failure would open a second
    /// removal wizard inside the installation beside it.
    /// </summary>
    [Fact]
    public void A_cleanup_that_cannot_start_leaves_the_handover_in_place()
    {
        var processes = new RecordingProcessFactory
        {
            FailWhen = start => !Inside(TemporaryRoot, start.FileName),
        };

        var outcome = Relaunch(processes).Relaunch(InstalledCopy, ["--uninstall"]);

        Assert.Equal(UninstallerRelaunchOutcome.Relaunched, outcome);
        Assert.Equal("the installer itself", File.ReadAllText(processes.Started[0].FileName));
    }

    [Fact]
    public void What_an_ended_copy_left_is_removed_at_the_next_start()
    {
        // Nothing runs here, so the copy's directory stays exactly as a copy whose cleanup never
        // ran would leave it.
        Relaunch(new RecordingProcessFactory()).Relaunch(InstalledCopy, ["--uninstall"]);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(TemporaryRoot));

        UninstallerRelaunch.RemoveLeftovers(TemporaryRoot);

        Assert.Empty(Directory.EnumerateFileSystemEntries(TemporaryRoot));
    }

    [Fact]
    public void A_directory_that_only_looks_like_a_copys_is_left_alone()
    {
        var unmarked = Path.Combine(
            TemporaryRoot,
            UninstallerRelaunch.DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unmarked);
        File.WriteAllText(Path.Combine(unmarked, UninstallRegistration.UninstallerFileName), "someone's");

        UninstallerRelaunch.RemoveLeftovers(TemporaryRoot);

        Assert.True(File.Exists(Path.Combine(unmarked, UninstallRegistration.UninstallerFileName)));
    }

    /// <summary>
    /// The copy does not hold its directory's marker; what keeps the next start's sweep off a
    /// removal still running is that Windows refuses to delete a running executable.
    /// </summary>
    [Fact]
    public async Task A_copy_still_running_keeps_its_directory()
    {
        using var processes = new RealProcesses();
        Relaunch(processes).Relaunch(RunnableInstalledCopy(), RunsUntilEnded);
        var copy = processes.Started[0];

        UninstallerRelaunch.RemoveLeftovers(TemporaryRoot);

        Assert.False(copy.HasExited);
        Assert.Single(Directory.EnumerateFiles(
            TemporaryRoot,
            UninstallRegistration.UninstallerFileName,
            SearchOption.AllDirectories));
        copy.KillTree();
        await processes.WaitForAllAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Busy rather than waiting on anything, so nothing but a kill ends it.</summary>
    private static readonly string[] RunsUntilEnded = ["/d", "/q", "/c", "for /l %i in (0,0,1) do rem"];

    /// <summary>
    /// A real executable in the installed copy's place, so the copy runs and holds its image the
    /// way the installer does. The command processor runs from any directory under any name.
    /// </summary>
    private string RunnableInstalledCopy()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), InstalledCopy, overwrite: true);
        return InstalledCopy;
    }

    private UninstallerRelaunch Relaunch(IProcessFactory processes) =>
        new(machine.Layout, TemporaryRoot, processes);

    private static bool Inside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StartedProcess(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        bool UseShellExecute);

    private sealed class RecordingProcessFactory : IProcessFactory
    {
        public List<StartedProcess> Started { get; } = [];

        public Exception? Failure { get; init; }

        public Func<ProcessStartInfo, bool>? FailWhen { get; init; }

        public IRunningProcess Start(ProcessStartInfo startInfo)
        {
            Started.Add(new(
                startInfo.FileName,
                [.. startInfo.ArgumentList],
                startInfo.WorkingDirectory,
                startInfo.UseShellExecute));
            if (Failure is not null)
            {
                throw Failure;
            }

            if (FailWhen?.Invoke(startInfo) == true)
            {
                throw new Win32Exception(2, "The system cannot find the file specified.");
            }

            return new ExitedProcess();
        }
    }

    /// <summary>
    /// Starts the processes for real and keeps them for the test to wait on: the relaunch lets
    /// go of its handles at once, and only their ends say that everything it set going is done.
    /// </summary>
    private sealed class RealProcesses : IProcessFactory, IDisposable
    {
        private readonly SystemProcessFactory system = new();

        public List<IRunningProcess> Started { get; } = [];

        public IRunningProcess Start(ProcessStartInfo startInfo)
        {
            var process = system.Start(startInfo);
            Started.Add(process);
            return new Borrowed(process);
        }

        public async Task WaitForAllAsync(CancellationToken cancellationToken)
        {
            foreach (var process in Started)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            foreach (var process in Started)
            {
                if (!process.HasExited)
                {
                    process.KillTree();
                }

                process.Dispose();
            }
        }

        private sealed class Borrowed(IRunningProcess process) : IRunningProcess
        {
            public int Id => process.Id;
            public bool HasExited => process.HasExited;
            public int ExitCode => process.ExitCode;
            public TextReader StandardOutput => process.StandardOutput;
            public Stream StandardOutputStream => process.StandardOutputStream;
            public TextReader StandardError => process.StandardError;

            public Task WaitForExitAsync(CancellationToken cancellationToken) =>
                process.WaitForExitAsync(cancellationToken);

            public void KillTree() => process.KillTree();
            public void Dispose() { }
        }
    }

    private sealed class ExitedProcess : IRunningProcess
    {
        public int Id => 0;
        public bool HasExited => true;
        public int ExitCode => 0;
        public TextReader StandardOutput => TextReader.Null;
        public Stream StandardOutputStream => Stream.Null;
        public TextReader StandardError => TextReader.Null;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void KillTree() { }
        public void Dispose() { }
    }
}
