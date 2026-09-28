using System.Runtime.Versioning;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// An update rolled back puts the earlier version's pointer back, and with it the two other
/// places that name the installed release: the release record beside the pointer and the
/// version Apps &amp; features shows. Left behind, the record describes a directory that is no
/// longer active — so the installation stops knowing its release — and the list names a
/// release that is not installed.
///
/// The registry key is one of these tests' own under HKCU, never the real Uninstall key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RollingBackAnUpdateNamesThePriorReleaseTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.RollbackNames.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string registryRoot =
        @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N");

    private readonly InstallationLayout layout;
    private readonly string subKey;
    private readonly string installerSource;

    public RollingBackAnUpdateNamesThePriorReleaseTests()
    {
        layout = InstallationLayout.FromLocalAppData(Path.Combine(root, "appdata"));
        subKey = registryRoot + @"\Uninstall\LocalAi";
        installerSource = Path.Combine(root, "source", "LocalAi.Installer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installerSource)!);
        File.WriteAllText(installerSource, "installer");
        Directory.CreateDirectory(layout.LauncherDirectory);
        File.WriteAllBytes(layout.LauncherPath, [0x4D, 0x5A]);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_release_record_names_the_earlier_release_again()
    {
        WritePointer("v1");
        Record().Write("v1", "0.1.0");
        var recorded = File.ReadAllBytes(Record().FilePath);
        var before = ReleaseNamesBeforeRun.Read(layout, subKey);
        WritePointer("v2");
        Record().Write("v2", "0.2.0");
        using var journal = JournalWith(before.UndoFor("v2", "v1", "0.2.0"));

        var result = await Rollback().RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.True(result.AllReversibleUndone);
        Assert.Equal(recorded, File.ReadAllBytes(Record().FilePath));
        Assert.Equal("0.1.0", InstalledVersionReader.Read(layout.Root).ReleaseVersion);
    }

    [Fact]
    public async Task Apps_and_features_shows_the_earlier_release_again()
    {
        WritePointer("v1");
        Registration().Register("0.1.0", installerSource);
        var before = ReleaseNamesBeforeRun.Read(layout, subKey);
        WritePointer("v2");
        Registration().Register("0.2.0", installerSource);
        using var journal = JournalWith(before.UndoFor("v2", "v1", "0.2.0"));

        var result = await Rollback().RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.True(result.AllReversibleUndone);
        Assert.Equal("0.1.0", Registration().Read()!.DisplayVersion);
    }

    /// <summary>
    /// The record and the list follow the pointer, never lead it. A version somebody activated
    /// after the update keeps the names that describe it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_renamed_when_the_pointer_is_not_moved_back()
    {
        WritePointer("v1");
        Record().Write("v1", "0.1.0");
        Registration().Register("0.1.0", installerSource);
        var before = ReleaseNamesBeforeRun.Read(layout, subKey);
        Record().Write("v2", "0.2.0");
        Registration().Register("0.2.0", installerSource);
        var undo = before.UndoFor("v2", "v1", "0.2.0");
        WritePointer("v3");
        Record().Write("v3", "0.3.0");
        using var journal = JournalWith(undo);

        await Rollback().RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal("0.3.0", Record().ReadFor("v3"));
        Assert.Equal("0.2.0", Registration().Read()!.DisplayVersion);
    }

    /// <summary>A version shown in the list that this run did not write belongs to someone else.</summary>
    [Fact]
    public async Task A_version_shown_that_the_run_did_not_write_is_left_alone()
    {
        WritePointer("v1");
        Registration().Register("0.1.0", installerSource);
        var before = ReleaseNamesBeforeRun.Read(layout, subKey);
        WritePointer("v2");
        Registration().Register("0.2.0", installerSource);
        var undo = before.UndoFor("v2", "v1", "0.2.0");
        Registration().Register("0.2.1", installerSource);
        using var journal = JournalWith(undo);

        await Rollback().RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal("0.2.1", Registration().Read()!.DisplayVersion);
    }

    private InstalledReleaseStore Record() => new(layout.BinRoot);

    private UninstallRegistration Registration() => new(layout, subKey);

    private InstallerRunRollback Rollback() =>
        new(new PointerMovingRunner(this), layout, TimeSpan.FromSeconds(30), subKey);

    /// <summary>Read back from disk, the way a later start finds an interrupted run.</summary>
    private InstallerRunJournal JournalWith(InstallerRunUndoData undo)
    {
        using var journal = InstallerRunJournal.Start(Path.Combine(root, "logs"));
        var step = journal.BeginStep(
            InstallerRunEffectKind.PackageActivation,
            "Local AI package 0.2.0");
        journal.CompleteStep(step, "Activated v2; v1 was active before.", isReversible: true, undo);
        journal.Finish(InstallerRunOutcome.Failed);
        return InstallerRunJournal.Load(journal.JournalPath);
    }

    private void WritePointer(string version)
    {
        Directory.CreateDirectory(layout.BinRoot);
        File.WriteAllBytes(
            layout.CurrentPointerPath,
            CurrentPointerSnapshot.CreateCanonicalBytes(version));
    }

    /// <summary>What `localai-launcher activate &lt;version&gt;` does to the pointer.</summary>
    private sealed class PointerMovingRunner(RollingBackAnUpdateNamesThePriorReleaseTests test)
        : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            test.WritePointer(arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        }
    }
}
