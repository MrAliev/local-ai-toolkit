using System.Security.Cryptography;
using System.Text.Json;

namespace LocalAi.Contracts;

/// <summary>The validated catalogue selected by one installation, shared by every reader.</summary>
public sealed class ModelRoutingCatalogStore
{
    public const string FileName = "model-routing.json";

    public ModelRoutingCatalogStore(string? runtimeRoot = null)
    {
        FilePath = RuntimeDirectories.SettingsFileForWriting(
            runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot, FileName);
    }

    public string FilePath { get; }

    public ModelRoutingCatalogDocument Read() => ReadWithSource().Document;

    public ModelRoutingCatalogSnapshot ReadWithSource()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var document = JsonSerializer.Deserialize<ModelRoutingCatalogDocument>(stream, LocalAiJson.Strict)
                ?? throw new InvalidDataException("Model routing catalog is empty.");
            Validate(document);
            // Formatting is not a change of catalogue. Hash its parsed, canonical form so a
            // stale client cannot reuse a preflight after a hand edit left catalogVersion alone.
            var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, LocalAiJson.Strict));
            return new ModelRoutingCatalogSnapshot(
                document with { CatalogVersion = "custom-" + Convert.ToHexString(hash).ToLowerInvariant() },
                FilePath);
        }
        catch (FileNotFoundException)
        {
            return new ModelRoutingCatalogSnapshot(LoadEmbedded(), null);
        }
        catch (DirectoryNotFoundException)
        {
            return new ModelRoutingCatalogSnapshot(LoadEmbedded(), null);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Model routing catalog '{FilePath}' could not be read: {failure.Message}", failure);
        }
    }

    public static ModelRoutingCatalogDocument LoadEmbedded()
    {
        var document = ModelRoutingCatalogResource.LoadDocument();
        Validate(document);
        return document;
    }

    private static void Validate(ModelRoutingCatalogDocument document)
    {
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported model routing schema '{document.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.CatalogVersion))
        {
            throw new InvalidDataException("Catalog version cannot be blank.");
        }

        var models = document.Models
            ?? throw new InvalidDataException("Models are required.");
        if (models.Count == 0)
        {
            throw new InvalidDataException("At least one model is required.");
        }

        var modelsByTag = new Dictionary<string, ModelCatalogEntry>(
            StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Tag) ||
                string.IsNullOrWhiteSpace(model.Source))
            {
                throw new InvalidDataException("Model tag and source are required.");
            }

            if (!modelsByTag.TryAdd(model.Tag, model))
            {
                throw new InvalidDataException($"Duplicate model tag '{model.Tag}'.");
            }

            if (!Enum.IsDefined(model.Lifecycle) ||
                !Enum.IsDefined(model.InstallPolicy) ||
                model.Capabilities is null ||
                model.Capabilities.Count == 0 ||
                model.Capabilities.Any(capability => !Enum.IsDefined(capability)))
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has invalid lifecycle or capabilities.");
            }

            if (model.ContextTokens is null ||
                model.ContextTokens.Count == 0 ||
                model.ContextTokens.Distinct().Count() != model.ContextTokens.Count ||
                model.ContextTokens.Any(context => !LocalContextTiers.IsSupported(context)))
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has unsupported context tiers.");
            }

            if (model.MaxImagePixels is < 1 || !model.SupportsImages && model.MaxImagePixels is not null)
            {
                throw new InvalidDataException(
                    $"Model '{model.Tag}' has invalid image constraints.");
            }
        }

        var routes = document.Routes
            ?? throw new InvalidDataException("Routes are required.");
        if (routes.Count != Enum.GetValues<LocalTaskProfile>().Length ||
            routes.Any(route => route is null) ||
            routes.Select(route => route.Profile).Distinct().Count() != routes.Count)
        {
            throw new InvalidDataException(
                "Every task profile must have exactly one route.");
        }

        foreach (var route in routes)
        {
            if (!Enum.IsDefined(route.Profile) ||
                !Enum.IsDefined(route.Mode) ||
                !Enum.IsDefined(route.Validator) ||
                !Enum.IsDefined(route.DefaultDuration) ||
                route.Candidates is null ||
                route.Fallbacks is null)
            {
                throw new InvalidDataException(
                    $"Route '{route.Profile}' is invalid.");
            }

            if (route.Mode == LocalRouteMode.Model &&
                (route.Candidates.Count == 0 || route.Fallbacks.Count == 0))
            {
                throw new InvalidDataException(
                    $"Model route '{route.Profile}' requires candidates and fallbacks.");
            }

            if (route.Mode == LocalRouteMode.Deterministic &&
                (route.Candidates.Count != 0 || route.Fallbacks.Count != 0))
            {
                throw new InvalidDataException(
                    $"Deterministic route '{route.Profile}' cannot name models.");
            }

            foreach (var tag in route.Candidates.Concat(route.Fallbacks))
            {
                if (string.IsNullOrWhiteSpace(tag) || !modelsByTag.ContainsKey(tag))
                {
                    throw new InvalidDataException(
                        $"Route '{route.Profile}' references unknown model '{tag}'.");
                }
            }
        }

        var allowlist = document.MaintenanceAllowlist
            ?? throw new InvalidDataException("Maintenance allowlist is required.");
        if (allowlist.Count != allowlist.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Maintenance allowlist contains duplicates.");
        }

        foreach (var tag in allowlist)
        {
            if (string.IsNullOrWhiteSpace(tag) || !modelsByTag.TryGetValue(tag, out var model) ||
                model.Lifecycle != LocalModelLifecycle.Experimental &&
                model.InstallPolicy != LocalModelInstallPolicy.Recommended)
            {
                throw new InvalidDataException(
                    $"Maintenance model '{tag}' is not recommended or experimental.");
            }
        }
    }
}

public sealed record ModelRoutingCatalogSnapshot(ModelRoutingCatalogDocument Document, string? SourcePath);
