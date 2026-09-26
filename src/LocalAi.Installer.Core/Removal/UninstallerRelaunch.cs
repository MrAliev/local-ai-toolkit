using System.ComponentModel;
using System.Diagnostics;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Removal;

public enum UninstallerRelaunchOutcome
{
    /// <summary>The executable is already outside the installation and runs where it is.</summary>
    NotNeeded,

    /// <summary>A copy outside the installation was started; this process should exit.</summary>
    Relaunched,

    /// <summary>No copy was started; the one made for it is removed again.</summary>
    Failed,
}

/// <summary>
/// Moves a removal out of the tree it deletes before it touches anything.
///
/// Apps &amp; features starts the copy parked in <c>bin\uninstall</c>, and Windows will delete
/// neither a running executable nor a directory that is a process's current directory — so a
/// removal run from there can never take <c>bin</c> away. This copies the executable into a
/// directory of its own under <paramref name="temporaryRoot"/> and starts that copy with the
/// same arguments, from that directory; the caller then exits and releases the installed copy.
/// </summary>
public sealed class UninstallerRelaunch(
    InstallationLayout layout,
    string temporaryRoot,
    IProcessFactory processes)
{
    public const string DirectoryPrefix = "LocalAi-uninstall-";

    public UninstallerRelaunchOutcome Relaunch(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Inside(layout.Root, executablePath))
        {
            return UninstallerRelaunchOutcome.NotNeeded;
        }

        // A copy placed inside the installation would relaunch again, and be deleted by the
        // removal it runs.
        if (Inside(layout.Root, temporaryRoot))
        {
            return UninstallerRelaunchOutcome.Failed;
        }

        var directory = Path.Combine(temporaryRoot, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, Path.GetFileName(executablePath));
            File.Copy(executablePath, copy);
            var start = new ProcessStartInfo(copy)
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = processes.Start(start);
            return UninstallerRelaunchOutcome.Relaunched;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                Win32Exception or InvalidOperationException)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanup) when (
                cleanup is IOException or UnauthorizedAccessException)
            {
                // Only a temporary directory, and the caller is told the handover failed.
            }

            return UninstallerRelaunchOutcome.Failed;
        }
    }

    private static bool Inside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
