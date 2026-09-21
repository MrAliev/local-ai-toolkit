using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Two palettes, one key set. Swapping themes is a single dictionary assignment, so a key
/// defined in one file and not the other is a control that keeps its old colour — or throws —
/// at the moment somebody switches, which is the one moment nobody is watching the other
/// windows.
///
/// Read from the files rather than through WPF: loading a dictionary needs a resource assembly
/// and an STA thread, and what is being checked here is the text.
/// </summary>
public sealed class ThemeDictionariesTests
{
    private static readonly Regex Key = new(
        "x:Key=\"(?<key>[^\"]+)\"",
        RegexOptions.Compiled);

    private static readonly Regex Colour = new(
        "Color=\"(?<colour>#[0-9A-Fa-f]{6})\"",
        RegexOptions.Compiled);

    [Fact]
    public void Both_palettes_define_the_same_names()
    {
        var light = Keys("Light.xaml");
        var dark = Keys("Dark.xaml");

        Assert.Equal(light.OrderBy(key => key, StringComparer.Ordinal), dark.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Neither_palette_is_empty()
    {
        Assert.NotEmpty(Keys("Light.xaml"));
    }

    /// <summary>
    /// Every brush is a literal colour. A palette that referenced another resource would make
    /// the swap order matter, and the swap is one assignment with no order to it.
    /// </summary>
    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Every_brush_carries_its_own_colour(string palette)
    {
        var text = Read(palette);

        Assert.Equal(Key.Matches(text).Count, Colour.Matches(text).Count);
    }

    /// <summary>
    /// The two are different palettes, not one file copied. If they ever agree on everything,
    /// somebody has pasted over one of them.
    /// </summary>
    [Fact]
    public void The_dark_palette_is_not_the_light_one()
    {
        Assert.NotEqual(Colours("Light.xaml"), Colours("Dark.xaml"));
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Active_control_boundaries_and_future_steps_meet_their_contrast_targets(string palette)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colours = XDocument.Parse(Read(palette)).Root!.Elements().ToDictionary(
            element => (string)element.Attribute(xaml + "Key")!,
            element => (string)element.Attribute("Color")!);

        // Controls.xaml uses these borders inside fields and against the page/card/warning
        // surfaces. Both wizard rails use TextMuted for future steps. Disabled controls and
        // decorative Divider strokes deliberately have no active-control contrast target.
        (string Foreground, string Background, double Minimum)[] pairs =
        [
            ("ControlBorder", "ControlFill", 3),
            ("ControlBorder", "WindowFill", 3),
            ("ControlBorder", "Surface", 3),
            ("ControlBorder", "WarnFill", 3),
            ("TextMuted", "RailFill", 4.5),
            ("FocusRing", "ControlFill", 3),
            ("FocusRing", "WindowFill", 3),
            ("AccentFill", "ControlFill", 3),
            ("WindowFill", "AccentFill", 3),
        ];
        foreach (var (foreground, background, minimum) in pairs)
        {
            var a = Luminance(colours[foreground]);
            var b = Luminance(colours[background]);
            var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            Assert.True(ratio >= minimum,
                $"{palette}: {foreground}/{background} is {ratio:F3}:1; requires {minimum}:1.");
        }
    }

    private static double Luminance(string colour)
    {
        double Linear(int offset)
        {
            var channel = Convert.ToByte(colour.Substring(offset, 2), 16) / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(1) + 0.7152 * Linear(3) + 0.0722 * Linear(5);
    }

    private static IReadOnlyList<string> Keys(string palette) =>
        [.. Key.Matches(Read(palette)).Select(match => match.Groups["key"].Value)];

    private static IReadOnlyList<string> Colours(string palette) =>
        [.. Colour.Matches(Read(palette)).Select(match => match.Groups["colour"].Value)];

    private static string Read(string palette) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "LocalAi.Installer",
            "Themes",
            palette));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
