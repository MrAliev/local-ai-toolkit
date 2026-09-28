using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The mark is an L and, in the corner it leaves, an octagonal frame around AI: level with the
/// L's top and its right edge, and as far from the stem as from the foot. The rail draws the
/// icon's own geometry, so the two cannot drift apart.
/// </summary>
public sealed class TheMarkFramesItsLettersTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void The_frame_is_an_octagon_level_with_the_L_and_as_far_from_its_stem_as_from_its_foot()
    {
        var shapes = Subpaths(MarkData());
        var l = shapes[0];
        var frame = shapes[1];
        var top = l.Min(point => point.Y);
        var right = l.Max(point => point.X);
        var stem = l.Where(point => point.Y == top).Max(point => point.X);
        var foot = l.Where(point => point.X == right).Min(point => point.Y);

        Assert.Equal(8, frame.Count);
        Assert.Equal(top, frame.Min(point => point.Y));
        Assert.Equal(right, frame.Max(point => point.X));
        Assert.True(frame.Min(point => point.X) - stem > 0);
        Assert.Equal(frame.Min(point => point.X) - stem, foot - frame.Max(point => point.Y));
    }

    [Fact]
    public void Every_corner_of_the_frame_is_cut_by_the_same_amount()
    {
        var frame = Subpaths(MarkData())[1];
        var cuts = frame
            .Select((point, index) => (From: point, To: frame[(index + 1) % frame.Count]))
            .Where(edge => edge.From.X != edge.To.X && edge.From.Y != edge.To.Y)
            .Select(edge => (Dx: Math.Abs(edge.To.X - edge.From.X), Dy: Math.Abs(edge.To.Y - edge.From.Y)))
            .ToArray();

        Assert.Equal(4, cuts.Length);
        Assert.All(cuts, cut => Assert.Equal(cut.Dx, cut.Dy));
        Assert.Single(cuts.Select(cut => cut.Dx).Distinct());
    }

    [Fact]
    public void The_letters_stand_inside_the_frame()
    {
        var shapes = Subpaths(MarkData());
        var inside = shapes[2];

        Assert.Equal(8, inside.Count);
        Assert.All(shapes.Skip(3).SelectMany(shape => shape), point =>
        {
            Assert.InRange(point.X, inside.Min(p => p.X), inside.Max(p => p.X));
            Assert.InRange(point.Y, inside.Min(p => p.Y), inside.Max(p => p.Y));
        });
    }

    [Fact]
    public void The_rail_draws_the_icons_own_geometry()
    {
        var rail = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(Presentation + "Path")
            .Single(path => path.Attribute("Fill")?.Value == "{StaticResource MarkInk}")
            .Attribute("Data")!.Value;
        var mark = Subpaths(MarkData());
        var left = mark[0].Min(point => point.X);
        var top = mark[0].Min(point => point.Y);

        Assert.Equal(
            mark.Select(shape => shape.Select(point => (point.X - left, point.Y - top)).ToArray()).ToArray(),
            Subpaths(rail.Replace("F0", "", StringComparison.Ordinal)).Select(shape => shape.ToArray()).ToArray());
    }

    private static string MarkData() =>
        XDocument.Load(Path.Combine(InstallerMarkup.Directory, "..", "..", "assets", "localai-mark.svg"))
            .Descendants(XNamespace.Get("http://www.w3.org/2000/svg") + "path")
            .Single()
            .Attribute("d")!.Value;

    /// <summary>The corners of each closed figure, for the absolute M, L, H, V and Z the mark is written in.</summary>
    private static List<List<(double X, double Y)>> Subpaths(string data)
    {
        var tokens = Regex.Matches(data, @"[MLHVZ]|-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToList();
        var shapes = new List<List<(double X, double Y)>>();
        var at = (X: 0.0, Y: 0.0);
        double Number(ref int index) => double.Parse(tokens[++index], CultureInfo.InvariantCulture);
        for (var index = 0; index < tokens.Count; index++)
        {
            switch (tokens[index])
            {
                case "M":
                    at = (Number(ref index), Number(ref index));
                    shapes.Add([at]);
                    break;
                case "L":
                    at = (Number(ref index), Number(ref index));
                    shapes[^1].Add(at);
                    break;
                case "H":
                    at = (Number(ref index), at.Y);
                    shapes[^1].Add(at);
                    break;
                case "V":
                    at = (at.X, Number(ref index));
                    shapes[^1].Add(at);
                    break;
                case "Z":
                    break;
                default:
                    throw new InvalidDataException($"Unexpected path token '{tokens[index]}'.");
            }
        }

        return shapes;
    }
}
