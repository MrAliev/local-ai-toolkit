using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The page that decides whether this product works on this machine at all moved under the
/// pointer. Each rule's consequence was bound to that rule being chosen, so picking one collapsed
/// a paragraph, expanded another, revealed a warning, and shifted everything below — «поплыло
/// всё». The reader could not compare three rules without clicking all three, and the thing they
/// clicked had moved by the time they got there.
///
/// The consequence belongs to the rule, so it goes inside it: always visible, aligned by the
/// control's own template rather than by a margin somebody measured, and part of what the option
/// offers rather than a reward for choosing it.
/// </summary>
public sealed class ThePageHoldsStillTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// A horizontal <c>StackPanel</c> measures its children with infinite width, so anything in
    /// one is asked how wide it would like to be and never told how wide it may be. Both choice
    /// controls put their <c>ContentPresenter</c> in one, which meant no checkbox or radio in this
    /// installer could hold content that wraps — the precondition for putting a sentence in one.
    /// </summary>
    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    public void A_choice_control_can_hold_more_than_one_line(string control)
    {
        var template = Templates().Single(each => each.Attribute("TargetType")?.Value == control);
        var presenter = template.Descendants(Presentation + "ContentPresenter").Single();

        var stacked = presenter.Ancestors()
            .TakeWhile(ancestor => ancestor != template)
            .Where(ancestor => ancestor.Name == Presentation + "StackPanel" &&
                               ancestor.Attribute("Orientation")?.Value == "Horizontal")
            .ToList();

        Assert.Empty(stacked);
    }

    /// <summary>
    /// Three rules, three consequences, all readable at once. Nothing on this page appears or
    /// disappears because a rule was chosen — which is the whole of the reflow.
    /// </summary>
    [Fact]
    public void Choosing_a_rule_makes_nothing_appear_or_disappear()
    {
        var conditional = Window().Descendants()
            .Select(element => element.Attribute("Visibility")?.Value ?? string.Empty)
            .Where(visibility =>
                visibility.Contains("Residency.IsRequireFullVram", StringComparison.Ordinal) ||
                visibility.Contains("Residency.IsAllowPartialOffload", StringComparison.Ordinal) ||
                visibility.Contains("Residency.IsAllowCpu", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(conditional);
    }

    [Fact]
    public void Each_rule_carries_its_own_consequence()
    {
        var rules = Window().Descendants(Presentation + "RadioButton")
            .Where(radio => radio.Attribute("GroupName")?.Value == "Residency")
            .ToList();

        Assert.Equal(3, rules.Count);
        Assert.All(rules, rule =>
            Assert.Equal(2, rule.Descendants(Presentation + "TextBlock").Count()));
    }

    /// <summary>
    /// The one thing on this page that must be louder than the rest. It was a bold red sentence
    /// among ordinary ones; the removal window already had the strip, and this is the same idea.
    /// </summary>
    [Fact]
    public void The_warning_is_the_loudest_thing_on_the_page()
    {
        var warning = Window().Descendants()
            .Single(element =>
                element.Attribute("Visibility")?.Value
                    .Contains("Residency.HasWarning", StringComparison.Ordinal) == true);

        Assert.Equal(Presentation + "Border", warning.Name);
        Assert.Equal("{DynamicResource WarnFill}", warning.Attribute("Background")?.Value);
    }

    /// <summary>
    /// Text that belongs to an option but sits below it lines up with that option's label, not
    /// three pixels to its left. The number is the control template's own: the indicator's width
    /// and the gap after it, which is what the <c>ContentPresenter</c> starts at.
    /// </summary>
    [Fact]
    public void Content_under_an_option_lines_up_with_its_label()
    {
        var box = Templates()
            .Single(each => each.Attribute("TargetType")?.Value == "CheckBox")
            .Descendants(Presentation + "Border")
            .Single(border => border.Attribute("Name")?.Value == "Box" ||
                              border.Attributes().Any(a => a.Name.LocalName == "Name" &&
                                                           a.Value == "Box"));
        var indent =
            double.Parse(box.Attribute("Width")!.Value) +
            double.Parse(box.Attribute("Margin")!.Value.Split(',')[2]);

        var misaligned = Window().Descendants()
            .Where(element => element.Attribute("Visibility")?.Value
                .Contains("Models.Is", StringComparison.Ordinal) == true)
            .Select(element => element.Attribute("Margin")?.Value ?? "0,0,0,0")
            .Where(margin => double.Parse(margin.Split(',')[0]) != indent)
            .ToList();

        Assert.Empty(misaligned);
    }

    private static IEnumerable<XElement> Templates() =>
        XDocument.Parse(InstallerMarkup.Read(Path.Combine("Themes", "Controls.xaml")))
            .Descendants(Presentation + "ControlTemplate");

    private static XElement Window() =>
        XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml")).Root!;
}
