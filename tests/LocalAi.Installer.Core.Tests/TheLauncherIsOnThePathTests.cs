using LocalAi.TestFixtures;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every command the installed instructions offer begins `localai-launcher.exe` — sixteen of
/// them across `SKILL.md`, `CLAUDE.md` and `AGENTS.md`, and not one of them ran, because the
/// launcher was never on the path. Codex found it the first time it was asked to check the
/// installation: "The term 'localai-launcher.exe' is not recognized".
///
/// That half of the toolkit is the documented way through a dead MCP server, so it was the
/// fallback that did not work.
///
/// The user's path, not the machine's: the whole installation lives under LOCALAPPDATA and
/// belongs to one account.
/// </summary>
public sealed class TheLauncherIsOnThePathTests
{
    private const string Directory = @"C:\Users\x\AppData\Local\LocalAi\bin\launcher";

    [Fact]
    public void The_directory_is_added_once_and_the_rest_is_left_alone()
    {
        var before = @"C:\Windows;C:\Windows\System32";

        var after = LauncherPathRegistration.Add(before, Directory);

        Assert.Equal(before + ";" + Directory, after);
    }

    /// <summary>
    /// An installation over an installation must not grow the path a copy at a time. Somebody
    /// who put it there by hand is already done, too.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows;C:\Users\x\AppData\Local\LocalAi\bin\launcher")]
    // Windows paths do not differ by case, and a trailing separator is the same directory.
    [InlineData(@"C:\Windows;c:\users\x\appdata\local\localai\bin\launcher")]
    [InlineData(@"C:\Windows;C:\Users\x\AppData\Local\LocalAi\bin\launcher\")]
    public void A_directory_already_there_is_not_added_again(string before)
    {
        Assert.Equal(before, LauncherPathRegistration.Add(before, Directory));
    }

    [Fact]
    public void An_empty_path_gets_the_directory_and_no_separator()
    {
        Assert.Equal(Directory, LauncherPathRegistration.Add(string.Empty, Directory));
        Assert.Equal(Directory, LauncherPathRegistration.Add(null, Directory));
    }

    /// <summary>
    /// Removal takes our entry and nothing else, however the reader spelled theirs, and leaves
    /// no empty slot behind — `A;;B` is a path with a nameless directory in it.
    /// </summary>
    [Fact]
    public void Removal_takes_only_our_entry()
    {
        var before = @"C:\Windows;c:\users\x\appdata\local\localai\bin\launcher\;C:\Tools";

        var after = LauncherPathRegistration.Remove(before, Directory);

        Assert.Equal(@"C:\Windows;C:\Tools", after);
    }

    [Fact]
    public void Removal_of_something_that_is_not_there_changes_nothing()
    {
        var before = @"C:\Windows;C:\Tools";

        Assert.Equal(before, LauncherPathRegistration.Remove(before, Directory));
    }

    /// <summary>
    /// Whether the reader's path already carries it, which is what the review page reports and
    /// what stops the uninstall offering to remove something nobody added.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows;C:\Users\x\AppData\Local\LocalAi\bin\launcher", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData("", false)]
    public void Whether_it_is_there_is_answered_the_same_way(string path, bool expected)
    {
        Assert.Equal(expected, LauncherPathRegistration.Contains(path, Directory));
    }

    /// <summary>
    /// What the account's PATH is asked, never the machine's own: a test that read the real one
    /// would answer differently on the developer's machine than on a clean one, and a test that
    /// wrote it would change theirs.
    /// </summary>
    [Fact]
    public async Task The_page_names_the_path_entry_before_anything_is_applied()
    {
        using var machine = new RemovalFixture();
        var launcher = Path.Combine(machine.Layout.BinRoot, "launcher");

        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            readUserPath: () => @"C:\Windows;" + launcher);

        Assert.True(plan.RemovesLauncherFromPath);
        Assert.Contains("PATH", plan.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>Nothing of ours on the path is nothing to offer to remove.</summary>
    [Fact]
    public async Task A_path_without_it_plans_nothing()
    {
        using var machine = new RemovalFixture();

        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            readUserPath: () => @"C:\Windows");

        Assert.False(plan.RemovesLauncherFromPath);
    }

    /// <summary>
    /// And a reinstall leaves it alone. Taking it off only to put it back would leave a window
    /// where a console started in between finds nothing — the same reasoning that keeps the
    /// Apps &amp; features entry through a reinstall.
    /// </summary>
    [Fact]
    public async Task A_reinstall_leaves_the_path_alone()
    {
        using var machine = new RemovalFixture();
        var launcher = Path.Combine(machine.Layout.BinRoot, "launcher");

        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            installationFollows: true,
            readUserPath: () => @"C:\Windows;" + launcher);

        Assert.False(plan.RemovesLauncherFromPath);
    }
}
