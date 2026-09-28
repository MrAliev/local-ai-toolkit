using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Every wait that lasts shows the gears, each shown by the state that says the wait is on:
/// the system check, the release lookup and its hold on the review, the prerequisites' second
/// check, the run, a rollback on either page it can start from; and in the removal wizard the
/// scan, the plan and the removal itself.
/// </summary>
public sealed class EveryLongWaitShowsTheGearsTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Theory]
    [InlineData("MainWindow.xaml", new[]
    {
        "Diagnose.IsChecking", "Diagnose.IsChecking", "IsRollingBack", "IsRollingBack", "IsRunning",
        "Package.IsResolving", "Review.IsStillChecking",
    })]
    [InlineData("UninstallWindow.xaml", new[] { "IsPlanning", "IsRunning", "IsScanning" })]
    public void Each_wait_has_gears_shown_by_its_own_state(string window, string[] expected)
    {
        var shown = Gears(window)
            .Select(gears => BindingPath(gears.Attribute("Visibility")?.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), shown);
    }

    /// <summary>The gears replace the pulsing bar the system check used; nothing else waits on one.</summary>
    [Fact]
    public void No_window_waits_on_an_indeterminate_bar_any_more()
    {
        foreach (var window in InstallerMarkup.Windows)
        {
            Assert.DoesNotContain(
                XDocument.Parse(InstallerMarkup.Read(window)).Descendants(Presentation + "ProgressBar"),
                bar => bar.Attribute("IsIndeterminate")?.Value == "True");
        }
    }

    private static IEnumerable<XElement> Gears(string window) =>
        XDocument.Parse(InstallerMarkup.Read(window))
            .Descendants(Presentation + "Control")
            .Where(control => control.Attribute("Style")?.Value == "{StaticResource BusyGears}");

    /// <summary>The path of a Visibility binding: {Binding Path, Converter=...} to Path.</summary>
    private static string BindingPath(string? binding)
    {
        Assert.NotNull(binding);
        var inside = binding.Trim('{', '}');
        Assert.StartsWith("Binding ", inside, StringComparison.Ordinal);
        return inside["Binding ".Length..].Split(',')[0].Trim();
    }
}
