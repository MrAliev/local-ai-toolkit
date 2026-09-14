using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LocalAi.Documentation;

/// <summary>Checks evidence identifiers and syntax. Prose still needs the pack's human review.</summary>
public static class DocumentationValidator
{
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = ["svg", "g", "rect", "line", "path", "polygon", "circle", "text"];
    private static readonly HashSet<string> Attributes =
    [
        "width", "height", "viewBox", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "points", "fill", "stroke", "stroke-width", "font-size", "font-family", "font-weight",
        "text-anchor", "dominant-baseline", "transform", "data-symbol-id", "data-from", "data-to"
    ];

    public static IReadOnlyList<string> Validate(DocumentationInput input, DocumentationDraft draft)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var facts = input.Facts.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (draft.Claims is not { Length: > 0 and <= 64 }) errors.Add("missing_claims");
        foreach (var claim in draft.Claims ?? [])
        {
            if (claim is null) { errors.Add("incomplete_pair"); continue; }
            if (claim.FactIds is not { Length: > 0 }) errors.Add("missing_evidence");
            if ((claim.FactIds ?? []).Any(id => !facts.Contains(id))) errors.Add("unknown_fact");
            if (string.IsNullOrWhiteSpace(claim.English) || string.IsNullOrWhiteSpace(claim.Russian)) errors.Add("incomplete_pair");
            if (claim.English?.Length > 4000 || claim.Russian?.Length > 8000) errors.Add("claim_too_large");
        }
        if (draft.Examples is null || draft.Examples.Length > 8) errors.Add("invalid_csharp");
        foreach (var example in draft.Examples ?? [])
        {
            if (string.IsNullOrWhiteSpace(example) || example.Length > 32000 ||
                CSharpSyntaxTree.ParseText(example).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                errors.Add("invalid_csharp");
        }
        foreach (var error in ValidateDiagram(input, draft.DiagramSvg)) errors.Add(error);
        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ValidateDiagram(DocumentationInput input, string svg)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        XDocument xml;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg ?? ""), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100_000,
            });
            xml = XDocument.Load(reader);
        }
        catch (XmlException) { return ["invalid_svg"]; }
        var root = xml.Root;
        if (root?.Name != SvgNamespace + "svg") return ["invalid_svg"];
        if (!Dimension(root, "width", out var width) || !Dimension(root, "height", out var height) || width * height > 8_000_000)
            errors.Add("diagram_bounds");
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.Namespace != SvgNamespace || !Elements.Contains(element.Name.LocalName)) errors.Add("unsafe_svg");
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                if (attribute.Name.Namespace != XNamespace.None || !Attributes.Contains(attribute.Name.LocalName) ||
                    attribute.Value.Contains("url", StringComparison.OrdinalIgnoreCase) || attribute.Value.Length > 16000)
                    errors.Add("unsafe_svg");
        }
        var nodes = root.Descendants().Where(e => e.Attribute("data-symbol-id") is not null).ToArray();
        if (nodes.Length != input.Nodes.Count) errors.Add("diagram_labels");
        foreach (var node in input.Nodes)
        {
            var found = nodes.Where(n => (string?)n.Attribute("data-symbol-id") == node.Id).ToArray();
            if (found.Length != 1 || found[0].Name.LocalName != "g") { errors.Add("diagram_labels"); continue; }
            var labels = found[0].Descendants(SvgNamespace + "text").ToArray();
            if (labels.Length != 1 || labels[0].Value != node.Label) errors.Add("diagram_labels");
        }
        if (root.Descendants(SvgNamespace + "text").Count() != input.Nodes.Count) errors.Add("diagram_labels");
        var expected = input.Edges.Select(e => e.From + "\0" + e.To).Order(StringComparer.Ordinal).ToArray();
        var actual = root.Descendants().Where(e => e.Name.LocalName is "line" or "path")
            .Where(e => e.Attribute("data-from") is not null || e.Attribute("data-to") is not null)
            .Select(e => (string?)e.Attribute("data-from") + "\0" + (string?)e.Attribute("data-to"))
            .Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual)) errors.Add("diagram_edges");
        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Dimension(XElement root, string name, out double value) =>
        double.TryParse((string?)root.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value) && value > 0 && value <= 4096;
}
