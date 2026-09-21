using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Documentation;

/// <summary>Text-affecting versions are separate from the presentation version.</summary>
public sealed record DocumentationCacheVersions(
    string Prompt = DocumentationGenerator.PromptVersion,
    string Validator = DocumentationValidator.CacheVersion,
    string Route = "documentation-route-1",
    string Renderer = DocumentationRenderer.CacheVersion)
{
    public static DocumentationCacheVersions ForRuntime(string runtimeRoot)
    {
        var catalog = new ModelRoutingCatalogStore(runtimeRoot).Read();
        var route = JsonSerializer.SerializeToUtf8Bytes(new { DocumentationGenerator.OverviewModels, Catalog = catalog });
        return new(Route: "documentation-route-1:" + Convert.ToHexStringLower(SHA256.HashData(route)));
    }
}
