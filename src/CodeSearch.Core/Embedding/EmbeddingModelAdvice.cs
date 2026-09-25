using CodeSearch.Core.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Core.Embedding;

/// <summary>
/// What to tell someone whose search or index build has no embedding model to run on: what was
/// checked, where to look, and an install command only when the broker will take it — it
/// downloads nothing its catalog's maintenance allowlist does not name.
/// </summary>
public static class EmbeddingModelAdvice
{
    public static string For(string model, string? runtimeRoot = null)
    {
        var store = new ModelRoutingCatalogStore(runtimeRoot);
        string install;
        try
        {
            var catalog = store.Read();
            install = catalog.MaintenanceAllowlist.Contains(model, StringComparer.Ordinal)
                ? IndexText.EmbeddingModelInstallByBroker(model, catalog.CatalogVersion)
                : IndexText.EmbeddingModelInstallNotAllowed;
        }
        catch (InvalidDataException)
        {
            install = IndexText.EmbeddingModelInstallCatalogUnreadable(store.FilePath);
        }

        return IndexText.EmbeddingModelMissing(model) + " " + install;
    }
}
