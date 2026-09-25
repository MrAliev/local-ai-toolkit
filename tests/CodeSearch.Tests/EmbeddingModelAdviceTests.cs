using System.Text.Json;
using CodeSearch.Core.Embedding;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// What a search or an index build says when the embedding model is not installed. It says only
/// what was checked — Ollama answered and does not have the model — points at the model
/// directory, which is where the 2026-09-16 case on #633 was, and names an install command only
/// when the broker will take it: the broker downloads only what the catalog's maintenance
/// allowlist names, and nothing sends anyone around it with an ollama command.
/// </summary>
public sealed class EmbeddingModelAdviceTests : IDisposable
{
    private const string Model = "qwen3-embedding:8b-q8_0";

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

    [Fact]
    public void The_shipped_catalog_installs_it_through_the_broker()
    {
        Assert.Equal(
            Intro + " Otherwise install it through the broker: localai-launcher.exe run localai " +
            "model pull --model qwen3-embedding:8b-q8_0 --catalog-version 1",
            EmbeddingModelAdvice.For(Model, _runtimeRoot));
    }

    [Fact]
    public void A_catalog_the_broker_would_refuse_it_under_names_no_command()
    {
        WriteCatalogWithout(Model);

        Assert.Equal(
            Intro + " Otherwise the broker will not download it: the model catalog in use does not " +
            "list it in MaintenanceAllowlist, and the model-catalog line of localai doctor shows which " +
            "catalog that is.",
            EmbeddingModelAdvice.For(Model, _runtimeRoot));
    }

    /// <summary>Without a catalog nobody knows whether the broker would refuse, so no command is named.</summary>
    [Fact]
    public void An_unreadable_catalog_is_named_instead_of_a_command()
    {
        var path = WriteUnreadableCatalog();

        Assert.Equal(
            Intro + $" Otherwise first fix '{path}': the install command depends on that catalog, and " +
            "the model-catalog line of localai doctor shows why it cannot be read.",
            EmbeddingModelAdvice.For(Model, _runtimeRoot));
    }

    [Fact]
    public void A_Russian_reader_is_told_in_Russian()
    {
        using var reading = TestCulture.Reading("ru");

        Assert.Equal(
            "Для поиска и индексации нужна модель эмбеддингов qwen3-embedding:8b-q8_0, а в Ollama её " +
            "нет. Если она уже устанавливалась, проверьте, какой каталог моделей использует Ollama. " +
            "Иначе установите её через брокер: localai-launcher.exe run localai model pull --model " +
            "qwen3-embedding:8b-q8_0 --catalog-version 1",
            EmbeddingModelAdvice.For(Model, _runtimeRoot));
    }

    [Fact]
    public void A_Russian_reader_is_told_in_Russian_when_the_broker_would_refuse()
    {
        using var reading = TestCulture.Reading("ru");
        WriteCatalogWithout(Model);

        Assert.EndsWith(
            " Иначе брокер её не скачает: в используемом каталоге моделей её нет в MaintenanceAllowlist, " +
            "а какой это каталог, показывает строка model-catalog в localai doctor.",
            EmbeddingModelAdvice.For(Model, _runtimeRoot),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_Russian_reader_is_told_in_Russian_when_the_catalog_cannot_be_read()
    {
        using var reading = TestCulture.Reading("ru");
        var path = WriteUnreadableCatalog();

        Assert.EndsWith(
            $" Иначе сначала исправьте '{path}': команда установки зависит от этого каталога, а почему " +
            "он не читается, показывает строка model-catalog в localai doctor.",
            EmbeddingModelAdvice.For(Model, _runtimeRoot),
            StringComparison.Ordinal);
    }

    private void WriteCatalogWithout(string tag)
    {
        var store = new ModelRoutingCatalogStore(_runtimeRoot);
        var embedded = ModelRoutingCatalogStore.LoadEmbedded();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllBytes(
            store.FilePath,
            JsonSerializer.SerializeToUtf8Bytes(
                embedded with
                {
                    MaintenanceAllowlist = [.. embedded.MaintenanceAllowlist.Where(entry => entry != tag)],
                },
                LocalAiJson.Strict));
    }

    private string WriteUnreadableCatalog()
    {
        var store = new ModelRoutingCatalogStore(_runtimeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ not json");
        return store.FilePath;
    }
}
