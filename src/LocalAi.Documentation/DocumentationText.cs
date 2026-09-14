using System.Globalization;
using LocalAi.Contracts.Localization;

namespace LocalAi.Documentation;

internal static class DocumentationText
{
    private static readonly TextCatalogue Catalogue = new("LocalAi.Documentation.Resources.DocumentationText", typeof(DocumentationText).Assembly);
    internal static string Get(string key, string language) => Catalogue.Get(key, CultureInfo.GetCultureInfo(language));
}
