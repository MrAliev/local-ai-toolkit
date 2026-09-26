using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Models;

public sealed record CatalogModelFit(
    string Tag,
    int ContextTokens,
    bool FitsInVideoMemory,
    long DownloadSizeBytes,
    string Explanation)
{
    public bool HasKnownSize => DownloadSizeBytes > 0;
}

public sealed record CatalogRecommendation(
    IReadOnlyList<CatalogModelFit> Fits,
    string AdapterExplanation,
    bool SizesKnown)
{
    public static CatalogRecommendation Empty { get; } =
        new([], "No graphics adapter information was available.", false);

    public IReadOnlyList<CatalogModelFit> Fitting =>
        [.. Fits.Where(fit => fit.FitsInVideoMemory)];
}

/// <summary>
/// Turns the routing catalogue into a hardware-aware recommendation.
///
/// The catalogue supplies what a model is for and which context sizes it permits; the
/// registry supplies how big it is; the diagnosis supplies what the machine has. Only all
/// three together answer "will this run here", which is why none of them is hardcoded.
/// </summary>
public sealed class CatalogModelRecommender(
    IModelSizeSource sizeSource,
    ModelMemoryReservePolicy? reservePolicy = null)
{
    private readonly IModelSizeSource sizeSource =
        sizeSource ?? throw new ArgumentNullException(nameof(sizeSource));

    private readonly ModelRecommendationEngine engine = new(reservePolicy);

    public async Task<CatalogRecommendation> RecommendAsync(
        GpuSnapshot gpu,
        IReadOnlyList<ModelCatalogEntry> catalogModels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(catalogModels);

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var model in catalogModels)
        {
            var size = await sizeSource
                .GetDownloadSizeBytesAsync(model.Tag, cancellationToken)
                .ConfigureAwait(false);
            if (size is > 0)
            {
                sizes[model.Tag] = size.Value;
            }
        }

        if (sizes.Count == 0)
        {
            // Without sizes there is nothing to weigh against the adapter. Say so plainly
            // instead of presenting a guess as a recommendation.
            return Unsized;
        }

        var manifestModels = catalogModels
            .Where(model => sizes.ContainsKey(model.Tag))
            .SelectMany(model => model.ContextTokens
                .Distinct()
                .Select(context => new ManifestModel(
                    model.Tag,
                    context,
                    sizes[model.Tag],
                    // The engine treats this as the base weight estimate and adds the
                    // runtime and per-token reserves itself.
                    sizes[model.Tag])))
            .ToArray();

        return Weighed(engine.Recommend(gpu, manifestModels));
    }

    /// <summary>
    /// The catalogue's models weighed by a verified release's own sizes, for when the registry
    /// sized nothing. Its contexts rather than the catalogue's: they are what setup plans from.
    /// </summary>
    public static CatalogRecommendation FromRelease(
        GpuSnapshot gpu,
        IReadOnlyList<ModelCatalogEntry> catalogModels,
        IReadOnlyList<ManifestModel> signedModels,
        ModelMemoryReservePolicy? reservePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(catalogModels);
        ArgumentNullException.ThrowIfNull(signedModels);

        var tags = catalogModels.Select(model => model.Tag).ToHashSet(StringComparer.Ordinal);
        var listed = signedModels.Where(model => tags.Contains(model.Name)).ToArray();
        return listed.Length == 0
            ? Unsized
            : Weighed(new ModelRecommendationEngine(reservePolicy).Recommend(gpu, listed));
    }

    private static CatalogRecommendation Unsized { get; } = CatalogRecommendation.Empty with
    {
        AdapterExplanation = "Model sizes could not be retrieved, so no recommendation was computed.",
    };

    private static CatalogRecommendation Weighed(ModelRecommendation recommendation) =>
        new(
            [.. recommendation.Choices.Select(choice => new CatalogModelFit(
                choice.Name,
                choice.ContextTokens,
                choice.IsEnabled,
                (long)choice.SignedDownloadSizeBytes,
                choice.Explanation))],
            recommendation.AdapterSelectionExplanation,
            SizesKnown: true);
}
