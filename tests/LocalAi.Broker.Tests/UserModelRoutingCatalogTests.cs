using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class UserModelRoutingCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localai-user-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void No_file_keeps_the_shipped_catalog_and_does_not_create_settings()
    {
        var catalog = ModelRoutingCatalog.Load(_root);

        Assert.Equal(ModelRoutingCatalog.LoadEmbedded().CatalogVersion, catalog.CatalogVersion);
        Assert.Equal(ModelRoutingCatalog.LoadEmbedded().Route(LocalTaskProfile.LogTriage).Candidates,
            catalog.Route(LocalTaskProfile.LogTriage).Candidates);
        Assert.Equal(Enum.GetValues<LocalTaskProfile>().Order(), catalog.Routes.Select(route => route.Profile).Order());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void User_routes_replace_the_defaults_without_changing_the_embedded_catalog()
    {
        Write(ChangedRoute());

        var catalog = ModelRoutingCatalog.Load(_root);

        Assert.Equal(["qwen3.5:9b"], catalog.Route(LocalTaskProfile.LogTriage).Candidates);
        Assert.Equal(["qwen2.5-coder:14b"], catalog.Route(LocalTaskProfile.LogTriage).Fallbacks);
        Assert.NotEqual(catalog.CatalogVersion, ModelRoutingCatalog.LoadEmbedded().CatalogVersion);
        Assert.StartsWith("custom-", catalog.CatalogVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_declared_by_the_user_is_available_to_routing()
    {
        var document = ModelRoutingCatalogResource.LoadDocument();
        var model = document.Models.First() with { Tag = "fixture/custom:latest", Source = "fixture/custom:latest" };
        Write(document with { Models = [.. document.Models, model] });

        var configured = ModelRoutingCatalog.Load(_root).Model(model.Tag);
        Assert.Equal(model.Source, configured.Source);
        Assert.Equal(model.ContextTokens, configured.ContextTokens);
        Assert.Equal(model.Capabilities, configured.Capabilities);
    }

    [Fact]
    public void Content_changes_invalidate_the_effective_version_without_a_manual_version_bump()
    {
        var first = ChangedRoute();
        Write(first);
        var before = ModelRoutingCatalog.Load(_root).CatalogVersion;
        Write(ModelRoutingCatalogResource.LoadDocument() with { CatalogVersion = first.CatalogVersion });

        Assert.NotEqual(before, ModelRoutingCatalog.Load(_root).CatalogVersion);
    }

    [Fact]
    public void The_model_picker_includes_models_from_the_user_catalog()
    {
        var document = ModelRoutingCatalogResource.LoadDocument();
        var model = document.Models.First() with { Tag = "fixture/custom:latest", Source = "fixture/custom:latest" };
        Write(document with { Models = [.. document.Models, model] });

        Assert.Contains(ModelRoutingCatalogResource.SelectableModels(_root), entry => entry.Tag == model.Tag);
    }

    [Fact]
    public void Formatting_does_not_change_the_effective_version_or_rewrite_the_file()
    {
        var document = ChangedRoute();
        Write(document);
        var before = ModelRoutingCatalog.Load(_root).CatalogVersion;
        var formatted = JsonSerializer.Serialize(document, new JsonSerializerOptions(LocalAiJson.Strict) { WriteIndented = true });
        File.WriteAllText(CatalogPath, formatted);

        Assert.Equal(before, ModelRoutingCatalog.Load(_root).CatalogVersion);
        Assert.Equal(formatted, File.ReadAllText(CatalogPath));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("version")]
    [InlineData("duplicate-model")]
    [InlineData("context")]
    [InlineData("missing-profile")]
    [InlineData("unknown-model")]
    [InlineData("null-model")]
    [InlineData("null-route")]
    [InlineData("null-candidate")]
    [InlineData("null-maintenance")]
    public void Invalid_catalogs_are_rejected_with_the_settings_path(string defect)
    {
        var document = ModelRoutingCatalogResource.LoadDocument();
        document = defect switch
        {
            "null-candidate" => document with
            {
                Routes = document.Routes.Select(route => route.Profile == LocalTaskProfile.LogTriage
                    ? route with { Candidates = [null!] } : route).ToArray(),
            },
            "null-maintenance" => document with { MaintenanceAllowlist = [null!] },
            "schema" => document with { SchemaVersion = 999 },
            "version" => document with { CatalogVersion = "" },
            "duplicate-model" => document with { Models = [.. document.Models, document.Models[0]] },
            "context" => document with { Models = [document.Models[0] with { ContextTokens = [17] }, .. document.Models.Skip(1)] },
            "missing-profile" => document with { Routes = document.Routes.Skip(1).ToArray() },
            "unknown-model" => document with { Routes = [document.Routes[0] with { Candidates = ["not-declared"] }, .. document.Routes.Skip(1)] },
            "null-model" => document with { Models = [null!] },
            "null-route" => document with { Routes = [null!, .. document.Routes.Skip(1)] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        Write(document);

        var failure = Assert.Throws<InvalidDataException>(() => ModelRoutingCatalog.Load(_root));
        Assert.Contains(CatalogPath, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    public void Malformed_json_is_not_silently_replaced_by_the_shipped_catalog(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        File.WriteAllText(CatalogPath, json);

        var failure = Assert.Throws<InvalidDataException>(() => ModelRoutingCatalog.Load(_root));
        Assert.Contains(CatalogPath, failure.Message, StringComparison.Ordinal);
    }

    private static ModelRoutingCatalogDocument ChangedRoute()
    {
        var document = ModelRoutingCatalogResource.LoadDocument();
        return document with
        {
            Routes = document.Routes.Select(route => route.Profile == LocalTaskProfile.LogTriage
                ? route with { Candidates = ["qwen3.5:9b"], Fallbacks = ["qwen2.5-coder:14b"] }
                : route).ToArray()
        };
    }

    private string CatalogPath => RuntimeDirectories.SettingsFileForWriting(_root, "model-routing.json");

    private void Write(ModelRoutingCatalogDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        File.WriteAllText(CatalogPath, JsonSerializer.Serialize(document, LocalAiJson.Strict));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
