using System.Xml.Linq;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The prerequisites page printed each row on one line and cut it at the window edge, mid-word:
/// «…нужен только для закрытого форка или когда». What was lost is the half that says what
/// skipping the row costs — on the page where the reader decides exactly that.
///
/// Two things went wrong and both are checked here. The row said too much for one line, so it
/// becomes two; and the line was left to an implicit style that does not reach into a
/// <c>DataTemplate</c>, which is why it did not wrap at all.
/// </summary>
public sealed class ARowFitsItsTextTests
{
    /// <summary>
    /// The scannable half: the name, and whether the machine already has it. Nothing else — a
    /// reader going down this list is answering "which of these am I missing".
    /// </summary>
    [Fact]
    public void The_head_line_is_the_name_and_the_state()
    {
        var git = new DependencySelection("git", "Git", IsRequired: true) { IsInstalled = true };

        Assert.Equal("Git — Already installed", git.HeadLine);
    }

    /// <summary>
    /// What skipping costs runs to three lines in Russian. Joined onto the head line it is what
    /// pushed the row off the page.
    /// </summary>
    [Fact]
    public void What_skipping_costs_is_a_line_of_its_own()
    {
        var cli = new DependencySelection("gh", "GitHub CLI", IsRequired: false)
        {
            IsInstalled = true,
            Consequence = "needed only for a private fork",
        };

        Assert.Equal("GitHub CLI — Already installed", cli.HeadLine);
        Assert.Equal("optional: needed only for a private fork", cli.RequirementText);
    }

    /// <summary>
    /// Every <c>TextBlock</c> realised from a template says how it wraps.
    ///
    /// The window declares an implicit <c>Style TargetType="TextBlock"</c> that sets
    /// <c>TextWrapping="Wrap"</c>, and the sentence it did not reach is the one that shipped
    /// clipped. The rest of the file works around that by hand — some rows set the attribute,
    /// others borrow <c>Muted</c>, which is <c>BasedOn</c> the implicit style — so which rows
    /// wrap is decided by whether somebody remembered. Saying it on the element is the only form
    /// of that decision a reader of the markup can see, and <c>NoWrap</c> is as good an answer as
    /// <c>Wrap</c>: a one-word column that must not grow says so.
    /// </summary>
    [Fact]
    public void No_text_in_a_template_is_left_to_an_implicit_style()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var unstated = new List<string>();

        foreach (var window in InstallerMarkup.Windows)
        {
            var blocks = XDocument.Parse(InstallerMarkup.Read(window))
                .Descendants(presentation + "DataTemplate")
                .SelectMany(template => template.Descendants(presentation + "TextBlock"));

            foreach (var block in blocks)
            {
                if (block.Attribute("TextWrapping") is not null ||
                    block.Descendants(presentation + "Setter").Any(setter =>
                        setter.Attribute("Property")?.Value == "TextWrapping"))
                {
                    continue;
                }

                unstated.Add($"{window}: {block.Attribute("Text")?.Value ?? block.ToString()}");
            }
        }

        Assert.Empty(unstated);
    }
}
