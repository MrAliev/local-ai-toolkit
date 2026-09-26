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
        var started = Assert.Single(processes.Started);
        Assert.Equal(["--uninstall", "/extra arg"], started.Arguments);
        Assert.False(Inside(machine.Layout.Root, started.FileName), started.FileName);
        Assert.True(Inside(TemporaryRoot, started.FileName), started.FileName);
        Assert.Equal("the installer itself", File.ReadAllText(started.FileName));
        // A process's current directory cannot be deleted while it runs, so the copy must not
        // inherit one inside the tree it is about to remove: it starts in its own.
        Assert.Equal(Path.GetDirectoryName(started.FileName), started.WorkingDirectory);
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

            return new ExitedProcess();
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
