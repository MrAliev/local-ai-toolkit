using PdfSharp.Fonts;

namespace LocalAi.Documentation;

internal sealed class DocumentationFonts : IFontResolver
{
    internal static byte[] Read(string name)
    {
        using var stream = typeof(DocumentationFonts).Assembly.GetManifestResourceStream("LocalAi.Documentation.Fonts." + name)
            ?? throw new InvalidOperationException("missing_documentation_font");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(familyName == "Noto Sans Mono" ? "NotoSansMono-Regular.ttf" : "NotoSans-Regular.ttf", false, italic);

    public byte[] GetFont(string faceName) => Read(faceName);
}
