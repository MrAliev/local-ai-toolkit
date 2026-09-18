using System.Xml.Linq;

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
}
