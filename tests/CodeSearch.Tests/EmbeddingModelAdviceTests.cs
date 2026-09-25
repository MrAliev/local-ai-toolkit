using System.Text.Json;
using CodeSearch.Core.Embedding;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// What a search or an index build says when the embedding model is not installed. It says only
/// what was checked — Ollama answered and does not have the model — points at the model
/// directory, which is where the 2026-09-16 case on #633 was, and names an install route only when
/// that route will not refuse: the broker downloads only what the catalog's maintenance allowlist
/// names.
/// </summary>
public sealed class EmbeddingModelAdviceTests : IDisposable
{
    private const string Intro =
        "Search and indexing need the embedding model qwen3-embedding:8b-q8_0, and Ollama does not " +
        "have it. If it was installed before, check which model directory Ollama is using.";

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-embedding-advice-" + Guid.NewGuid().ToString("N"));

    public EmbeddingModelAdviceTests() => Directory.CreateDirectory(_runtimeRoot);

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }
    }

    /// <summary>The shipped catalog: the broker does not download the embedding model.</summary>
    [Fact]
    public void A_model_the_broker_does_not_download_is_installed_into_Ollama()
    {
        Assert.Equal(
            Intro + " Otherwise install it into Ollama yourself — the broker does not download " +
            "this model: ollama pull qwen3-embedding:8b-q8_0",
            EmbeddingModelAdvice.For("qwen3-embedding:8b-q8_0", _runtimeRoot));
    }

    [Fact]
    public void A_model_the_broker_downloads_is_installed_through_it()
    {
        var store = new ModelRoutingCatalogStore(_runtimeRoot);
        var embedded = ModelRoutingCatalogStore.LoadEmbedded();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllBytes(
            store.FilePath,
            JsonSerializer.SerializeToUtf8Bytes(
                embedded with
                {
                    Models =
                    [
                        .. embedded.Models.Select(model => model.Tag == "qwen3-embedding:8b-q8_0"
                            ? model with { InstallPolicy = LocalModelInstallPolicy.Recommended }
                            : model),
                    ],
                    MaintenanceAllowlist = [.. embedded.MaintenanceAllowlist, "qwen3-embedding:8b-q8_0"],
                },
                LocalAiJson.Strict));
        var version = store.Read().CatalogVersion;

        Assert.Equal(
            Intro + " Otherwise install it through the broker: localai-launcher.exe run localai " +
            $"model pull --model qwen3-embedding:8b-q8_0 --catalog-version {version}",
            EmbeddingModelAdvice.For("qwen3-embedding:8b-q8_0", _runtimeRoot));
    }

    /// <summary>Without a catalog nobody knows whether the broker would refuse, so it is not named.</summary>
    [Fact]
    public void An_unreadable_catalog_names_only_the_route_that_always_works()
    {
        var store = new ModelRoutingCatalogStore(_runtimeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Equal(
            Intro + " Otherwise install it into Ollama: ollama pull qwen3-embedding:8b-q8_0",
            EmbeddingModelAdvice.For("qwen3-embedding:8b-q8_0", _runtimeRoot));
    }

    [Fact]
    public void A_Russian_reader_is_told_in_Russian()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal(
            "Для поиска и индексации нужна модель эмбеддингов qwen3-embedding:8b-q8_0, а в Ollama её " +
            "нет. Если она уже устанавливалась, проверьте, какой каталог моделей использует Ollama. " +
            "Иначе установите её в Ollama сами — брокер эту модель не скачивает: ollama pull " +
            "qwen3-embedding:8b-q8_0",
            EmbeddingModelAdvice.For("qwen3-embedding:8b-q8_0", _runtimeRoot));
    }
}
