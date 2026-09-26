using System.Runtime.Versioning;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Transactions;

/// <summary>
/// What named the installed release before a package run, besides the pointer: the release
/// record beside it and the version Apps &amp; features shows. Rolling an update back returns
/// both with the pointer; left behind, the record describes a directory that is no longer
/// active and the list names a release that is not installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReleaseNamesBeforeRun
{
    private readonly string recordPath;
    private readonly bool recordExisted;
    private readonly byte[] record;
    private readonly string? entryVersion;

    private ReleaseNamesBeforeRun(
        string recordPath,
        bool recordExisted,
        byte[] record,
        string? entryVersion)
    {
        this.recordPath = recordPath;
        this.recordExisted = recordExisted;
        this.record = record;
        this.entryVersion = entryVersion;
    }

    public static ReleaseNamesBeforeRun Read(
        InstallationLayout layout,
        string? uninstallRegistrySubKey = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = new InstalledReleaseStore(layout.BinRoot).FilePath;
        var existed = File.Exists(path);
        return new(
            path,
            existed,
            existed ? File.ReadAllBytes(path) : [],
            new UninstallRegistration(layout, uninstallRegistrySubKey).ReadOwn()?.DisplayVersion);
    }

    /// <summary>
    /// Taken once the run has written its release record: the record's undo is guarded by what
    /// the run left in it. The list is rewritten later in the run, always to
    /// <paramref name="activatedRelease"/>, and its undo is guarded by that value.
    /// </summary>
    public InstallerRunUndoData UndoFor(
        string activatedVersion,
        string priorVersion,
        string activatedRelease)
    {
        IReadOnlyList<InstallerRunFileUndo>? files;
        try
        {
            var after = File.Exists(recordPath) ? File.ReadAllBytes(recordPath) : [];
            files = [InstallerRunFileUndo.Between(recordPath, recordExisted, record, after)];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            files = null;
        }

        return new(
            activatedVersion,
            priorVersion,
            files,
            entryVersion is { Length: > 0 } shown &&
                !string.Equals(shown, activatedRelease, StringComparison.Ordinal)
                ? new InstallerRunValueUndo(shown, activatedRelease)
                : null);
    }
}
