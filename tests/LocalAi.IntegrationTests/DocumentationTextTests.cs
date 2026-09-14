using System.Globalization;
using LocalAi.Contracts.Localization;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationTextTests
{
    private static TextCatalogue Catalogue => new("LocalAi.Documentation.Resources.DocumentationText", typeof(DocumentationRenderer).Assembly);
    [Fact]
    public void Explicit_languages_work_without_mutating_the_callers_culture()
    {
        Assert.Equal("Source", Catalogue.Get("Source", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Исходный код", Catalogue.Get("Source", CultureInfo.GetCultureInfo("ru")));
    }
    [Fact]
    public void Report_catalogue_has_complete_language_pairs()
    {
        Assert.Empty(Catalogue.Gaps());
        Assert.Contains("ReportTitle", Catalogue.Keys(CultureInfo.InvariantCulture));
    }
}
