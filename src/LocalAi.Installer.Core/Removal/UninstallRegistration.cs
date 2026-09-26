using System.Runtime.Versioning;
using LocalAi.Installer.Core.Activation;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Removal;

/// <summary>What Apps &amp; features shows for this installation.</summary>
public sealed record UninstallEntry(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string UninstallString,
    string InstallLocation,
    int EstimatedSizeKilobytes);

/// <summary>
/// The entry in Apps &amp; features, and the copy of the installer it points at.
///
/// Removal is only a first-class way out if it can be found where people look for it, which
/// on Windows is that list. The registration is per-user (HKCU) because the installation is:
/// everything lives under %LOCALAPPDATA% and no part of it needs administrator rights, and an
/// HKLM entry would offer every account on the machine an uninstall for something only one of
/// them has.
///
/// The uninstaller is a copy of this installer parked inside the runtime root. It has to be a
/// copy: the file somebody downloaded is routinely gone by the time they want to remove
/// anything, and an UninstallString pointing at a missing file is worse than no entry at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallRegistration(
    InstallationLayout layout,
    string? registrySubKey = null)
{
    /// <summary>
    /// Where Windows looks. The leaf name is the product key, and it is stable across
    /// versions: an upgrade updates this entry rather than adding a second one.
    /// </summary>
    public const string DefaultSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAi";

    /// <summary>
    /// What the product calls itself, which is the title of both READMEs.
    ///
    /// It read "LocalAi Developer Toolkit" until now, carrying a word the name never had. The
    /// repository rename that took the same word out of the URL did not reach here, because this
    /// string is only ever seen in a list nobody opens on a machine that already has the thing
    /// installed — and the install log prints it, which is where it was finally noticed.
    /// </summary>
    public const string DisplayName = "Local AI Toolkit";

    public const string Publisher = "MrAliev";

    /// <summary>The directory holding the uninstaller's copy, inside the runtime root.</summary>
    public const string UninstallerDirectoryName = "uninstall";

    public const string UninstallerFileName = "LocalAi.Installer.exe";

    private readonly string subKey = registrySubKey ?? DefaultSubKey;

    public string UninstallerDirectory =>
        Path.Combine(layout.BinRoot, UninstallerDirectoryName);

    public string UninstallerPath =>
        Path.Combine(UninstallerDirectory, UninstallerFileName);

    /// <summary>
    /// Copies this installer into the runtime root and writes the entry pointing at the copy.
    ///
    /// An upgrade runs this again: the same key is rewritten, so DisplayVersion follows the
    /// installation instead of naming whichever release first created the entry.
    /// </summary>
    public UninstallEntry Register(string version, string installerSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerSourcePath);
        CopyUninstaller(installerSourcePath);
        var entry = new UninstallEntry(
            DisplayName,
            version,
            Publisher,
            "\"" + UninstallerPath + "\" " + UninstallSwitch,
            layout.Root,
            EstimatedSizeKilobytes());
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue("DisplayName", entry.DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", entry.DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", entry.Publisher, RegistryValueKind.String);
        key.SetValue("UninstallString", entry.UninstallString, RegistryValueKind.String);
        key.SetValue("InstallLocation", entry.InstallLocation, RegistryValueKind.String);
        key.SetValue("EstimatedSize", entry.EstimatedSizeKilobytes, RegistryValueKind.DWord);
        // The same copy the uninstall command points at, so the row's icon and the program it
        // runs can never name two different files. Without this the shell picks the icon itself,
        // which is not the same choice on every machine.
        key.SetValue("DisplayIcon", UninstallerPath, RegistryValueKind.String);
        // There is no repair or modify path: the wizard's own start page is where those live,
        // and offering buttons here that lead nowhere is worse than not offering them.
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return entry;
    }

    public UninstallEntry? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key is null)
        {
            return null;
        }

        return new UninstallEntry(
            key.GetValue("DisplayName") as string ?? string.Empty,
            key.GetValue("DisplayVersion") as string ?? string.Empty,
            key.GetValue("Publisher") as string ?? string.Empty,
            key.GetValue("UninstallString") as string ?? string.Empty,
            key.GetValue("InstallLocation") as string ?? string.Empty,
            key.GetValue("EstimatedSize") as int? ?? 0);
    }

    /// <summary>
    /// The entry, when it describes the installation at this layout. The key is one per
    /// account, so what is found under it may belong to a LocalAi somewhere else.
    /// </summary>
    public UninstallEntry? ReadOwn() =>
        Read() is { } entry && SamePath(entry.InstallLocation, layout.Root) ? entry : null;

    private static bool SamePath(string recorded, string root)
    {
        try
        {
            return recorded.Length > 0 && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Takes the entry out of Apps &amp; features. Absent is success: an uninstall run on a
    /// machine whose entry was already removed by hand has nothing left to do here, and the
    /// result says which it was.
    ///
    /// A key that cannot be removed throws rather than answering "absent": the caller deletes
    /// the uninstaller copy next, and must not while an entry still runs it.
    /// </summary>
    public bool Unregister()
    {
        var parent = subKey[..subKey.LastIndexOf('\\')];
        var leaf = subKey[(subKey.LastIndexOf('\\') + 1)..];
        using var key = Registry.CurrentUser.OpenSubKey(parent, writable: true);
        using (var existing = key?.OpenSubKey(leaf))
        {
            if (existing is null)
            {
                return false;
            }
        }

        key!.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>
    /// Removes the uninstaller's copy. Only once the entry is gone: an entry whose copy is
    /// missing offers a removal that cannot start.
    ///
    /// A removal never runs from this copy — it hands over to a temporary one first — so a copy
    /// that cannot be deleted is held by something else. That throws, and the caller reports
    /// it; nothing is scheduled for later.
    /// </summary>
    public void RemoveUninstallerCopy()
    {
        if (Directory.Exists(UninstallerDirectory))
        {
            Directory.Delete(UninstallerDirectory, recursive: true);
        }
    }

    /// <summary>The argument the entry passes back to the copied installer.</summary>
    public const string UninstallSwitch = "--uninstall";

    private void CopyUninstaller(string installerSourcePath)
    {
        Directory.CreateDirectory(UninstallerDirectory);
        var source = Path.GetFullPath(installerSourcePath);
        if (string.Equals(
                source,
                Path.GetFullPath(UninstallerPath),
                StringComparison.OrdinalIgnoreCase))
        {
            // Already the copy — an uninstaller that was asked to install, or a repair run
            // started from the parked executable. Copying a file onto itself throws.
            return;
        }

        File.Copy(source, UninstallerPath, overwrite: true);
    }

    /// <summary>
    /// What Apps &amp; features prints beside the entry. Reported in kilobytes because that is
    /// the unit the registry value is defined in, and measured rather than guessed: this
    /// installation's size is mostly its indexes, which vary by an order of magnitude between
    /// machines.
    /// </summary>
    private int EstimatedSizeKilobytes()
    {
        if (!Directory.Exists(layout.Root))
        {
            return 0;
        }

        try
        {
            var bytes = Directory
                .EnumerateFiles(layout.Root, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        return 0L;
                    }
                });
            return (int)Math.Clamp(bytes / 1024, 0, int.MaxValue);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

}
