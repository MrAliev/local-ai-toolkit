using CodeSearch.Core.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Core.Embedding;

/// <summary>
/// What to tell someone whose search or index build has no embedding model to run on: what was
/// checked, where to look, and an install route only when it will not refuse — the broker
/// downloads nothing its catalog's maintenance allowlist does not name.
/// </summary>
public static class EmbeddingModelAdvice
{
    public static string For(string model, string? runtimeRoot = null)
    {
        string install;
        try
        {
            var catalog = new ModelRoutingCatalogStore(runtimeRoot).Read();
            install = catalog.MaintenanceAllowlist.Contains(model, StringComparer.Ordinal)
                ? IndexText.EmbeddingModelInstallByBroker(model, catalog.CatalogVersion)
                : IndexText.EmbeddingModelInstallInOllama(model);
        }
        catch (InvalidDataException)
        {
            install = IndexText.EmbeddingModelInstallInOllamaUnchecked(model);
        }

        return IndexText.EmbeddingModelMissing(model) + " " + install;
    }
}
