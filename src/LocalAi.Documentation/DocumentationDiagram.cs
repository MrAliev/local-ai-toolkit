using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace LocalAi.Documentation;

/// <summary>Lay out the validated topology with pinned glyph outlines, independent of system fonts.</summary>
internal static class DocumentationDiagram
{
    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    internal static (byte[] Svg, byte[] Png) Render(DocumentationInput input)
    {
        if (input.Nodes.Count is < 1 or > 24 || input.Edges.Count > 48 || input.Nodes.Any(n => n.Label.Length > 500))
            throw new DocumentationValidationException(["diagram_too_large"]);
        using var fontData = SKData.CreateCopy(DocumentationFonts.Read("NotoSans-Regular.ttf"));
        using var typeface = SKTypeface.FromData(fontData);
        using var font = new SKFont(typeface, 18);
        if (input.Nodes.Any(n => font.GetGlyphs(n.Label).Any(g => g == 0)))
            throw new DocumentationValidationException(["unsupported_label_glyph"]);
        var lines = input.Nodes.Select(n => Wrap(n.Label, font, 700)).ToArray();
        var top = new float[input.Nodes.Count];
        var heights = lines.Select(l => Math.Max(60, l.Count * 26 + 24)).ToArray();
        float y = 30;
        for (var i = 0; i < top.Length; i++) { top[i] = y; y += heights[i] + 36; }
        if (y > 4096) throw new DocumentationValidationException(["diagram_too_large"]);
        var height = (int)y;
        var root = new XElement(Ns + "svg", new XAttribute("width", 1200), new XAttribute("height", height),
            new XAttribute("viewBox", $"0 0 1200 {height}"), new XAttribute("role", "img"),
            new XElement(Ns + "title", input.Name),
            new XElement(Ns + "rect", new XAttribute("width", 1200), new XAttribute("height", height), new XAttribute("fill", "white")));
        var positions = input.Nodes.Select((n, i) => (n.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index, StringComparer.Ordinal);
        var edgeIndex = 0;
        foreach (var edge in input.Edges)
        {
            var from = positions[edge.From]; var to = positions[edge.To];
            var lane = 30 + edgeIndex++ * 7;
            var y1 = top[from] + heights[from] / 2f;
            var y2 = top[to] + heights[to] / 2f;
            // Separate ports retain a visible arrow even for a self-reference.
            if (from == to) { y1 -= 15; y2 += 15; }
            root.Add(new XElement(Ns + "path", new XAttribute("d", FormattableString.Invariant($"M 420 {y1} H {lane} V {y2} H 412")),
                new XAttribute("fill", "none"), new XAttribute("stroke", "#526580"), new XAttribute("stroke-width", 2),
                new XElement(Ns + "title", edge.From + " → " + edge.To + ": " + edge.Kind)));
            root.Add(new XElement(Ns + "polygon", new XAttribute("points", FormattableString.Invariant($"420,{y2} 409,{y2 - 5} 409,{y2 + 5}")), new XAttribute("fill", "#526580")));
        }
        for (var i = 0; i < input.Nodes.Count; i++)
        {
            var group = new XElement(Ns + "g", new XAttribute("data-symbol-id", input.Nodes[i].Id), new XElement(Ns + "title", input.Nodes[i].Label));
            group.Add(new XElement(Ns + "rect", new XAttribute("x", 420), new XAttribute("y", Invariant(top[i])),
                new XAttribute("width", 750), new XAttribute("height", heights[i]), new XAttribute("rx", 8),
                new XAttribute("fill", "#eef3fa"), new XAttribute("stroke", "#526580")));
            for (var line = 0; line < lines[i].Count; line++)
            {
                using var path = font.GetTextPath(lines[i][line], new SKPoint(440, top[i] + 31 + line * 26));
                group.Add(new XElement(Ns + "path", new XAttribute("d", path.ToSvgPathData()), new XAttribute("fill", "#13233a")));
            }
            root.Add(group);
        }
        var svg = root.ToString(SaveOptions.DisableFormatting);
        using var picture = new SKSvg();
        picture.FromSvg(svg);
        if (picture.Picture is null) throw new DocumentationValidationException(["svg_render_failed"]);
        using var surface = SKSurface.Create(new SKImageInfo(1200, height));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawPicture(picture.Picture);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return (Encoding.UTF8.GetBytes(svg), png.ToArray());
    }

    private static string Invariant(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<string> Wrap(string label, SKFont font, float width)
    {
        var lines = new List<string>(); var line = "";
        foreach (var rune in label.EnumerateRunes())
        {
            var next = line + rune;
            if (font.MeasureText(next) > width && line.Length > 0) { lines.Add(line); line = rune.ToString(); }
            else line = next;
        }
        lines.Add(line);
        return lines;
    }
}
