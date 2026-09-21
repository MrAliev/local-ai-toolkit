using System.Security.Cryptography;
using System.Text;

namespace LocalAi.Documentation;

public enum DocumentationHtmlMode { Shared, Standalone }

internal static class DocumentationHtmlResources
{
    // A linked stylesheet can be read from file://; the font URLs themselves stay data URLs
    // so they do not depend on a browser permitting cross-file font requests.
    internal static string Css { get; } =
        "@font-face{font-family:Doc;src:url(data:font/ttf;base64," + Convert.ToBase64String(DocumentationFonts.Read("NotoSans-Regular.ttf")) + ")}" +
        "@font-face{font-family:DocMono;src:url(data:font/ttf;base64," + Convert.ToBase64String(DocumentationFonts.Read("NotoSansMono-Regular.ttf")) + ")}";
    private static readonly byte[] StyleSheet = Encoding.UTF8.GetBytes(Css);
    internal static string Name { get; } = Convert.ToHexStringLower(SHA256.HashData(StyleSheet)) + ".css";
    internal static IReadOnlyDictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [Name] = StyleSheet,
    };
    internal static bool ValidName(string name, string hash) => hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && name == hash + ".css";
}
