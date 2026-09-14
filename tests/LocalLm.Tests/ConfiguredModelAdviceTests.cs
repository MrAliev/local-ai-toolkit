using System.Text.Json;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class ConfiguredModelAdviceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localai-advice-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Installation_advice_uses_the_custom_model_and_effective_catalog_version()
    {
        var store = Configure();

        var advice = MissingModelAdvice.ForProfile(LocalTaskProfile.VisualAnalysis, _root);

        Assert.Contains("fixture/vision:latest", advice, StringComparison.Ordinal);
        Assert.Contains(store.Read().CatalogVersion, advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Ineligible_advice_names_the_configured_model()
    {
        Configure();

        var advice = MissingModelAdvice.ForIneligibleRequest(LocalTaskProfile.VisualAnalysis, _root);

        Assert.Contains("fixture/vision:latest", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("model pull", advice, StringComparison.Ordinal);
    }

    private ModelRoutingCatalogStore Configure()
    {
        var store = new ModelRoutingCatalogStore(_root);
        var document = ModelRoutingCatalogResource.LoadDocument();
        var model = document.Models.First(entry => entry.SupportsImages) with
        {
            Tag = "fixture/vision:latest", Source = "fixture/vision:latest",
        };
        document = document with
        {
            Models = [.. document.Models, model],
            Routes = document.Routes.Select(route => route.Profile == LocalTaskProfile.VisualAnalysis
                ? route with { Candidates = [model.Tag], Fallbacks = [model.Tag] }
                : route).ToArray(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(document, LocalAiJson.Strict));
        return store;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
