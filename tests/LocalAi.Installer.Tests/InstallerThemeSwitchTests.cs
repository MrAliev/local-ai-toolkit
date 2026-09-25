using System.Windows;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Swapping the palette is one assignment to merged dictionary zero. What these pin is that it
/// stays one assignment: appending instead would leave the old palette in the lookup, and the
/// control styles that come after dictionary zero would keep resolving against it.
///
/// The dictionaries are WPF objects, so each test builds them inside WpfTestThread.Run: made on a
/// pool thread they can leave that thread a Dispatcher nobody shuts down.
/// </summary>
public sealed class InstallerThemeSwitchTests
{
    [Fact]
    public Task High_contrast_temporarily_overrides_an_explicit_theme_and_restores_the_choice() => WpfTestThread.Run(() =>
    {
        var palettes = new Palettes();
        var resources = new ResourceDictionary();
        var contrast = Palette("high contrast");
        var enabled = true;
        using var themes = new InstallerThemeSwitch(resources, InstallerTheme.Dark, () => false,
            dark => dark ? palettes.Dark : palettes.Light, () => enabled, () => contrast);
        themes.Apply();
        Assert.Same(contrast, Assert.Single(resources.MergedDictionaries));
        themes.Choose(InstallerTheme.Light);
        Assert.Same(contrast, Assert.Single(resources.MergedDictionaries));
        enabled = false;
        themes.Apply();
        Assert.Same(palettes.Light, Assert.Single(resources.MergedDictionaries));
    });

    [Fact]
    public Task Following_a_dark_system_paints_the_dark_palette() => WpfTestThread.Run(() =>
    {
        var palettes = new Palettes();
        var resources = new ResourceDictionary();
        using var themes = palettes.Switch(resources, InstallerTheme.System, systemPrefersDark: true);

        themes.Apply();

        Assert.True(themes.IsDark);
        Assert.Same(palettes.Dark, Assert.Single(resources.MergedDictionaries));
    });

    [Fact]
    public Task An_explicit_light_choice_ignores_a_dark_system() => WpfTestThread.Run(() =>
    {
        var palettes = new Palettes();
        var resources = new ResourceDictionary();
        using var themes = palettes.Switch(resources, InstallerTheme.Light, systemPrefersDark: true);

        themes.Apply();

        Assert.False(themes.IsDark);
        Assert.Same(palettes.Light, Assert.Single(resources.MergedDictionaries));
    });

    /// <summary>
    /// The palette is dictionary zero and the control styles come after it. A second palette
    /// appended to the end would be looked up last, so the swap would appear to do nothing.
    /// </summary>
    [Fact]
    public Task Switching_replaces_the_palette_rather_than_adding_one() => WpfTestThread.Run(() =>
    {
        var palettes = new Palettes();
        var resources = new ResourceDictionary();
        var styles = new ResourceDictionary();
        using var themes = palettes.Switch(resources, InstallerTheme.Light, systemPrefersDark: false);
        themes.Apply();
        resources.MergedDictionaries.Add(styles);

        themes.Choose(InstallerTheme.Dark);

        Assert.Equal(2, resources.MergedDictionaries.Count);
        Assert.Same(palettes.Dark, resources.MergedDictionaries[0]);
        Assert.Same(styles, resources.MergedDictionaries[1]);
    });

    /// <summary>
    /// The window's caption is drawn by the desktop manager, not by these resources, so
    /// something outside has to be told. Without this the palette swaps and the title bar
    /// stays white.
    /// </summary>
    [Fact]
    public Task Applying_a_theme_announces_which_one_it_was() => WpfTestThread.Run(() =>
    {
        var palettes = new Palettes();
        var resources = new ResourceDictionary();
        using var themes = palettes.Switch(resources, InstallerTheme.System, systemPrefersDark: true);
        var announced = new List<bool>();
        themes.Applied += (_, dark) => announced.Add(dark);

        themes.Apply();
        themes.Choose(InstallerTheme.Light);

        Assert.Equal([true, false], announced);
    });

    private static ResourceDictionary Palette(string name) =>
        new() { { "Name", name } };

    private sealed class Palettes
    {
        public ResourceDictionary Light { get; } = Palette("light");

        public ResourceDictionary Dark { get; } = Palette("dark");

        public InstallerThemeSwitch Switch(
            ResourceDictionary resources,
            InstallerTheme chosen,
            bool systemPrefersDark) =>
            new(
                resources,
                chosen,
                () => systemPrefersDark,
                dark => dark ? Dark : Light,
                highContrast: () => false);
    }
}
