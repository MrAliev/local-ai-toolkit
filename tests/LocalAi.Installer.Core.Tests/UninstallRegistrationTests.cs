using LocalAi.Installer.Core.Removal;
using LocalAi.TestFixtures;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The entry in Apps &amp; features, and the copy of the installer it points at. Removal is
/// only a first-class way out if it can be found where people look for it.
///
/// These write to a key of their own under HKCU rather than the real Uninstall key: a test
/// suite that registers a product on the machine running it, or removes one that is genuinely
/// installed there, would be doing something nobody asked for.
/// </summary>
public sealed class UninstallRegistrationTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string subKey =
        @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N") + @"\Uninstall\LocalAi";

    private readonly string installerSource = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RegistrationTests",
        Guid.NewGuid().ToString("N"),
        "LocalAi.Installer.exe");

    public UninstallRegistrationTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installerSource)!);
        File.WriteAllText(installerSource, "installer");
    }

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\LocalAi.Tests\" + subKey.Split('\\')[2],
                throwOnMissingSubKey: false);
            Directory.Delete(Path.GetDirectoryName(installerSource)!, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [Fact]
    public void Installing_writes_the_entry_and_the_copy_it_points_at()
    {
        var registration = Registration();

        var entry = registration.Register("0.1.50", installerSource);

        Assert.True(File.Exists(registration.UninstallerPath));
        // What the product calls itself, which is the title of both READMEs. The entry used to
        // read "LocalAi Developer Toolkit", carrying a word the name never had — it survived the
        // repository rename because nobody looks at this list on a machine that already has the
        // thing installed.
        Assert.Equal("Local AI Toolkit", entry.DisplayName);
        Assert.Equal("0.1.50", entry.DisplayVersion);
        Assert.Equal(machine.Runtime, entry.InstallLocation);
        Assert.Equal(
            "\"" + registration.UninstallerPath + "\" --uninstall",
            entry.UninstallString);
        // Estimated from what is actually installed rather than guessed: this installation is
        // mostly its indexes, which differ by an order of magnitude between machines.
        Assert.True(entry.EstimatedSizeKilobytes >= 0);
        Assert.Equal(entry, registration.Read());
    }

    [Fact]
    public void The_entry_offers_no_repair_or_modify_it_cannot_perform()
    {
        Registration().Register("0.1.50", installerSource);

        using var key = Registry.CurrentUser.OpenSubKey(subKey);

        Assert.Equal(1, key!.GetValue("NoModify"));
        Assert.Equal(1, key.GetValue("NoRepair"));
    }

    /// <summary>
    /// Apps and features draws the row's icon from `DisplayIcon`, and without it falls back to
    /// whatever the shell decides - which is not the same thing on every machine, and was a
    /// generic box on this one. The value is the copy the entry already points at, so the icon
    /// and the uninstall command can never name two different files.
    /// </summary>
    [Fact]
    public void The_entry_names_the_icon_it_shows_rather_than_leaving_it_to_the_shell()
    {
        var registration = Registration();

        registration.Register("0.1.50", installerSource);

        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        Assert.Equal(registration.UninstallerPath, key!.GetValue("DisplayIcon"));
    }

    /// <summary>
    /// The key's name is the product, not the version, so an upgrade updates the one entry
    /// instead of leaving a list of every release ever installed.
    /// </summary>
    [Fact]
    public void An_upgrade_rewrites_the_version_rather_than_adding_a_second_entry()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        registration.Register("0.1.51", installerSource);

        Assert.Equal("0.1.51", registration.Read()!.DisplayVersion);
        using var parent = Registry.CurrentUser.OpenSubKey(
            subKey[..subKey.LastIndexOf('\\')]);
        Assert.Equal(["LocalAi"], parent!.GetSubKeyNames());
    }

    [Fact]
    public void Uninstalling_takes_the_entry_out_and_says_so_only_once()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        Assert.True(registration.Unregister());

        Assert.Null(registration.Read());
        // An entry somebody already removed by hand is not a failure to report.
        Assert.False(registration.Unregister());
    }

    [Fact]
    public void The_uninstallers_copy_goes_when_nothing_is_holding_it()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        registration.RemoveUninstallerCopy();

        Assert.False(Directory.Exists(registration.UninstallerDirectory));
        // Removing it twice is what a second uninstall run would do.
        registration.RemoveUninstallerCopy();
    }

    /// <summary>
    /// A removal never runs from the copy — it hands over to a temporary one first — so a copy
    /// that cannot be deleted is held by something else. That is said at once, to the caller,
    /// rather than handed to anything that promises to finish later.
    /// </summary>
    [Fact]
    public void A_copy_something_is_holding_throws_rather_than_being_left_for_later()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);
        using var held = new FileStream(
            registration.UninstallerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var refusal = Record.Exception(registration.RemoveUninstallerCopy);

        Assert.True(refusal is IOException or UnauthorizedAccessException, refusal?.ToString());
        Assert.True(File.Exists(registration.UninstallerPath));
    }

    /// <summary>
    /// A removal takes bin first and the entry last, so the copy the entry runs cannot live in
    /// bin: from the first path on, the entry would offer a removal whose program is gone.
    /// </summary>
    [Fact]
    public void The_copy_is_parked_outside_bin()
    {
        var registration = Registration();

        registration.Register("0.1.50", installerSource);

        Assert.Equal(
            Path.Combine(machine.Layout.InstallerDirectory, "uninstall", "LocalAi.Installer.exe"),
            registration.UninstallerPath);
        Assert.True(File.Exists(registration.UninstallerPath));
        Assert.False(Directory.Exists(LegacyCopyDirectory));
    }

    /// <summary>
    /// Earlier releases parked the copy in bin\uninstall. Registering again points the entry at
    /// the new copy and takes the old one out, so nothing in bin is still what the entry runs.
    /// </summary>
    [Fact]
    public void Registering_again_takes_out_the_copy_an_earlier_release_left_in_bin()
    {
        var legacy = LegacyCopy();
        var registration = Registration();

        var entry = registration.Register("0.1.51", installerSource);

        Assert.Equal("\"" + registration.UninstallerPath + "\" --uninstall", entry.UninstallString);
        Assert.False(Directory.Exists(Path.GetDirectoryName(legacy)));
    }

    /// <summary>
    /// A repair started from the old copy is running it, and Windows will not delete it. The
    /// entry already points at the new copy, so the old one staying is untidy, not a failure.
    /// </summary>
    [Fact]
    public void An_earlier_copy_that_cannot_go_yet_does_not_fail_the_registration()
    {
        var legacy = LegacyCopy();
        using var held = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var registration = Registration();

        var entry = registration.Register("0.1.51", installerSource);

        Assert.Equal("\"" + registration.UninstallerPath + "\" --uninstall", entry.UninstallString);
        Assert.True(File.Exists(legacy));
    }

    private string LegacyCopyDirectory => Path.Combine(machine.Layout.BinRoot, "uninstall");

    private string LegacyCopy()
    {
        var legacy = Path.Combine(LegacyCopyDirectory, "LocalAi.Installer.exe");
        Directory.CreateDirectory(LegacyCopyDirectory);
        File.WriteAllText(legacy, "earlier installer");
        return legacy;
    }

    /// <summary>
    /// A repair started from the parked copy would otherwise try to copy the file over itself,
    /// which throws.
    /// </summary>
    [Fact]
    public void Registering_from_the_parked_copy_is_not_a_copy_onto_itself()
    {
        var registration = Registration();
        registration.Register("0.1.50", installerSource);

        var entry = registration.Register("0.1.51", registration.UninstallerPath);

        Assert.Equal("0.1.51", entry.DisplayVersion);
        Assert.True(File.Exists(registration.UninstallerPath));
    }

    private UninstallRegistration Registration() => new(machine.Layout, subKey);
}
