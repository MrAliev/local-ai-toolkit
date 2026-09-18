using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Language and theme were reachable from the start screen alone (#602). Removal started from
/// Apps &amp; features never shows that screen, and nobody could change either once a wizard had
/// opened. One panel now sits in every window, above the page and outside its scrolling area, so
/// it is on screen during a check, a run, an error and the finish page alike.
/// </summary>
public sealed class PanelOnEveryScreenTests
{
    [Theory]
    [InlineData("StartWindow.xaml")]
    [InlineData("MainWindow.xaml")]
    [InlineData("UninstallWindow.xaml")]
    public void Every_window_carries_the_shared_panel_outside_its_scrolling_area(string window)
    {
        var panels = XDocument.Parse(InstallerMarkup.Read(window))
            .Descendants()
            .Where(element => element.Name.LocalName == "PreferencesBar")
            .ToList();

        var panel = Assert.Single(panels);
        Assert.DoesNotContain(
            panel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        Assert.Equal("{Binding Presentation}", (string?)panel.Attribute("DataContext"));
    }

    /// <summary>
    /// A broken binding is silent, so the panel's paths are resolved against what it is bound to.
    /// It has no item templates: every path starts at the presentation.
    /// </summary>
    [Fact]
    public void Every_binding_on_the_panel_names_a_member_of_the_presentation()
    {
        var missing = Regex
            .Matches(InstallerMarkup.Read("PreferencesBar.xaml"), @"\{Binding\s+(?<path>[A-Za-z0-9_]+)")
            .Select(match => match.Groups["path"].Value)
            .Where(path => typeof(InstallerPresentation).GetProperty(
                path,
                BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>The code behind parses each control's Tag; one that no longer parses does nothing when clicked.</summary>
    [Fact]
    public void Every_choice_on_the_panel_names_a_real_language_or_theme()
    {
        var elements = XDocument.Parse(InstallerMarkup.Read("PreferencesBar.xaml")).Descendants().ToList();
        var languages = elements
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => element.Attribute("Tag")?.Value ?? string.Empty)
            .ToList();
        var themes = elements
            .Where(element => element.Name.LocalName == "RadioButton")
            .Select(element => element.Attribute("Tag")?.Value ?? string.Empty)
            .ToList();

        Assert.Equal(Enum.GetNames<InstallerLanguage>().Order(), languages.Order());
        Assert.Equal(Enum.GetNames<InstallerTheme>().Order(), themes.Order());
    }
}
