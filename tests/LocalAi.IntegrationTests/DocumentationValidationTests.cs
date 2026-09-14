using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationValidationTests
{
    internal static DocumentationInput Input => new(
        "type:A", "Example.A", "src/A.cs", 4, "tree",
        [new("f0", "type:A", "public class A : B"), new("f1", "type:B", "public class B")],
        [new("n0", "Example.A"), new("n1", "Example.B")],
        [new("n0", "n1", "Implementation")], []);

    internal const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1200" height="700" viewBox="0 0 1200 700">
          <g data-symbol-id="n0"><rect x="20" y="20" width="400" height="60"/><text x="30" y="60" font-size="20">Example.A</text></g>
          <g data-symbol-id="n1"><rect x="20" y="180" width="400" height="60"/><text x="30" y="220" font-size="20">Example.B</text></g>
          <line data-from="n0" data-to="n1" x1="200" y1="80" x2="200" y2="180"/>
          <polygon data-from="n0" data-to="n1" points="200,180 190,160 210,160"/>
        </svg>
        """;

    internal static DocumentationDraft Draft => new(
        [new(["f0", "f1"], "A derives from B.", "A наследует B.")],
        ["public class A : B {} public class B {}"], Svg);

    [Fact]
    public void Valid_pair_and_arrowhead_are_accepted() => Assert.Empty(DocumentationValidator.Validate(Input, Draft));

    [Fact]
    public void Invented_fact_is_rejected() => Assert.Contains("unknown_fact", DocumentationValidator.Validate(Input,
        Draft with { Claims = [new(["invented"], "A persists data.", "A сохраняет данные.")] }));

    [Theory]
    [InlineData("", "Текст")]
    [InlineData("Text", "")]
    [InlineData(" ", " ")]
    public void Each_claim_has_both_languages(string en, string ru) => Assert.Contains("incomplete_pair",
        DocumentationValidator.Validate(Input, Draft with { Claims = [new(["f0"], en, ru)] }));

    [Fact]
    public void Every_claim_needs_evidence() => Assert.Contains("missing_evidence",
        DocumentationValidator.Validate(Input, Draft with { Claims = [new([], "Text", "Текст")] }));

    [Fact]
    public void Invalid_code_is_rejected_by_Roslyn() => Assert.Contains("invalid_csharp",
        DocumentationValidator.Validate(Input, Draft with { Examples = ["public class { broken"] }));

    [Theory]
    [InlineData("Example.B", "Invented.Name", "diagram_labels")]
    [InlineData("data-to=\"n1\"", "data-to=\"n0\"", "diagram_edges")]
    [InlineData("</svg>", "<script>alert(1)</script></svg>", "unsafe_svg")]
    [InlineData("</svg>", "<image href=\"file:///secret\"/></svg>", "unsafe_svg")]
    [InlineData("<line ", "<line onclick=\"alert(1)\" ", "unsafe_svg")]
    public void Diagram_cannot_invent_or_execute_content(string before, string after, string error) =>
        Assert.Contains(error, DocumentationValidator.Validate(Input, Draft with { DiagramSvg = Svg.Replace(before, after, StringComparison.Ordinal) }));

    [Fact]
    public void Dtd_is_refused_before_any_external_resource_is_read() => Assert.Contains("invalid_svg",
        DocumentationValidator.Validate(Input, Draft with { DiagramSvg = "<!DOCTYPE svg SYSTEM 'file:///secret'>" + Svg }));

    [Fact]
    public void Oversized_canvas_is_rejected() => Assert.Contains("diagram_bounds",
        DocumentationValidator.Validate(Input, Draft with { DiagramSvg = Svg.Replace("1200", "9999999", StringComparison.Ordinal) }));

    [Fact]
    public void Empty_output_is_never_publishable() => Assert.Contains("missing_claims",
        DocumentationValidator.Validate(Input, Draft with { Claims = [] }));
}
