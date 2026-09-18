using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The theme is chosen where the language is chosen, for the same reason: the start screen is
/// the first thing anybody sees, and both wizards behind it read the answer at construction.
///
/// "System" is the default and is not a third colour scheme — it means the installer keeps
/// following Windows, including a change made while it is open. Somebody who has already told
/// Windows which they prefer has answered this question.
/// </summary>
public sealed class StartScreenOffersAThemeTests : IDisposable
{
    private readonly RemovalFixture machine = new();
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose()
    {
        InstallerCulture.Current = original;
        machine.Dispose();
    }

    [Fact]
    public void With_nothing_chosen_the_screen_follows_the_system()
    {
        Assert.Equal(InstallerTheme.System, Start().Presentation.Theme);
        Assert.True(Start().Presentation.IsSystemTheme);
        Assert.False(Start().Presentation.IsLightTheme);
        Assert.False(Start().Presentation.IsDarkTheme);
    }

    [Fact]
    public void Choosing_a_theme_moves_the_selection()
    {
        var start = Start();

        start.Presentation.ChooseTheme(InstallerTheme.Dark);

        Assert.Equal(InstallerTheme.Dark, start.Presentation.Theme);
        Assert.True(start.Presentation.IsDarkTheme);
        Assert.False(start.Presentation.IsSystemTheme);
    }

    /// <summary>
    /// A choice made here outlives the run, exactly as the language does: removing LocalAi
    /// months later should not ask again.
    /// </summary>
    [Fact]
    public void A_theme_chosen_here_is_remembered()
    {
        Start().Presentation.ChooseTheme(InstallerTheme.Light);

        Assert.Equal(
            InstallerTheme.Light,
            new InstallerPreferencesStore(
                Path.Combine(machine.LocalAppData, "LocalAi-installer-logs")).ReadTheme());
    }

    /// <summary>The screen repaints itself, or the person watches their own choice do nothing.</summary>
    [Fact]
    public void Choosing_a_theme_announces_the_change()
    {
        var start = Start();
        var announced = new List<string?>();
        start.Presentation.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        start.Presentation.ChooseTheme(InstallerTheme.Dark);

        Assert.Contains(announced, name => InstallerPresentationTests.Announces(
            name, nameof(InstallerPresentation.IsDarkTheme)));
        Assert.Contains(announced, name => InstallerPresentationTests.Announces(
            name, nameof(InstallerPresentation.IsSystemTheme)));
    }

    [Fact]
    public void The_three_choices_name_themselves_in_both_languages()
    {
        InstallerCulture.Current = InstallerLanguage.English;
        Assert.Equal("System", PageLabels.ThemeSystem);
        Assert.Equal("Light", PageLabels.ThemeLight);
        Assert.Equal("Dark", PageLabels.ThemeDark);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal("Системная", PageLabels.ThemeSystem);
        Assert.Equal("Светлая", PageLabels.ThemeLight);
        Assert.Equal("Тёмная", PageLabels.ThemeDark);
    }

    private InstallerStartViewModel Start() =>
        new(
            machine.LocalAppData,
            readInstalledVersion: () => new InstalledVersion("467ed5f0f9bf", "0.1.51"));
}
