using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The system check says «это займёт несколько секунд» over a bar that does not move.
///
/// WPF puts a progress bar's indeterminate animation in the template, not in the control: the
/// stock template animates a glow rectangle it declares itself. This installer replaces that
/// template — the stock one is an Aero green gradient belonging to neither palette — and the
/// replacement has no glow and no <c>IsIndeterminate</c> trigger, so
/// <c>IsIndeterminate="True"</c> sets a property nothing reads while winget, git and ollama are
/// probed.
/// </summary>
public sealed class TheWaitingBarMovesTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void A_bar_with_nothing_to_measure_still_moves()
    {
        var waiting = Template()
            .Descendants(Presentation + "Trigger")
            .SingleOrDefault(trigger =>
                trigger.Attribute("Property")?.Value == "IsIndeterminate" &&
                trigger.Attribute("Value")?.Value == "True");

        Assert.NotNull(waiting);
        var animation = waiting!.Descendants()
            .Single(element => element.Name.LocalName.EndsWith("Animation", StringComparison.Ordinal));
        Assert.Equal("Forever", animation.Attribute("RepeatBehavior")?.Value);
    }

    /// <summary>
    /// Started on entering the state and stopped on leaving it. A storyboard left running holds
    /// the render thread awake on a page that has finished.
    /// </summary>
    [Fact]
    public void The_motion_stops_when_the_wait_does()
    {
        var waiting = Template()
            .Descendants(Presentation + "Trigger")
            .Single(trigger => trigger.Attribute("Property")?.Value == "IsIndeterminate");

        var begun = waiting.Descendants(Presentation + "BeginStoryboard").Single();
        var stopped = waiting.Descendants(Presentation + "StopStoryboard").Single();

        Assert.Equal(
            begun.Attributes().Single(a => a.Name.LocalName == "Name").Value,
            stopped.Attribute("BeginStoryboardName")?.Value);
    }

    private static XElement Template() =>
        XDocument.Parse(InstallerMarkup.Read(Path.Combine("Themes", "Controls.xaml")))
            .Descendants(Presentation + "ControlTemplate")
            .Single(template => template.Attribute("TargetType")?.Value == "ProgressBar");
}
