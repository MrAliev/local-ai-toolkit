using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;

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
/// same arguments; the caller then exits and releases the installed copy.
///
/// The copy cannot delete itself either, so a hidden Windows PowerShell waits for it to end,
/// however it ends, and removes its directory. Should that never run, the directory carries the
/// installer's marker and the next start's sweep takes it.
/// </summary>
public sealed class UninstallerRelaunch(
    InstallationLayout layout,
    string temporaryRoot,
    IProcessFactory processes)
{
    public const string DirectoryPrefix = "LocalAi-uninstall-";

    private const string DirectoryVariable = "LOCALAI_UNINSTALL_COPY";

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

        InstallerScratchDirectory? directory = null;
        try
        {
            directory = InstallerScratchDirectory.Claim(temporaryRoot, DirectoryPrefix);
            var copy = Path.Combine(directory.Path, Path.GetFileName(executablePath));
            File.Copy(executablePath, copy);
            // Not its own directory: whatever the removal starts inherits it, and a process
            // outliving the copy would keep it.
            var start = new ProcessStartInfo(copy)
            {
                UseShellExecute = false,
                WorkingDirectory = temporaryRoot,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = processes.Start(start);
            RemoveAfterExit(process.Id, directory.Path);
            directory.Release();
            return UninstallerRelaunchOutcome.Relaunched;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                Win32Exception or InvalidOperationException)
        {
            return UninstallerRelaunchOutcome.Failed;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>What copies whose own cleanup never ran left under <paramref name="temporaryRoot"/>.</summary>
    public static void RemoveLeftovers(string temporaryRoot) =>
        InstallerScratchDirectory.RemoveLeftovers(temporaryRoot, DirectoryPrefix);

    /// <summary>
    /// Waits on the process rather than polling for it, so a removal left open for an hour costs
    /// nothing. The retries are for a scanner still holding the file just after the exit.
    /// </summary>
    private void RemoveAfterExit(int processId, string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = temporaryRoot,
        };
        // Through the environment, so no path needs quoting for PowerShell.
        start.Environment[DirectoryVariable] = directory;
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(
            "$ErrorActionPreference = 'SilentlyContinue'; " +
            $"Wait-Process -Id {processId.ToString(CultureInfo.InvariantCulture)}; " +
            "foreach ($attempt in 1..60) { " +
            $"Remove-Item -LiteralPath $env:{DirectoryVariable} -Recurse -Force; " +
            $"if (-not (Test-Path -LiteralPath $env:{DirectoryVariable})) {{ break }}; " +
            "Start-Sleep -Milliseconds 500 }");
        try
        {
            using var helper = processes.Start(start);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or
                IOException or UnauthorizedAccessException)
        {
            // The copy is already running, so the handover stands; its directory waits for the
            // next start's sweep.
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
