using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ModelRoutingCatalog
{
    private readonly IReadOnlyDictionary<string, ModelCatalogEntry> _modelsByTag;
    private readonly IReadOnlyDictionary<LocalTaskProfile, TaskRouteEntry> _routesByProfile;
    private readonly HashSet<string> _maintenanceAllowlist;

    private ModelRoutingCatalog(ModelRoutingCatalogDocument document)
    {
        SchemaVersion = document.SchemaVersion;
        CatalogVersion = document.CatalogVersion;
        Models = Array.AsReadOnly(document.Models.ToArray());
        Routes = Array.AsReadOnly(document.Routes.ToArray());
        MaintenanceAllowlist = Array.AsReadOnly(
            document.MaintenanceAllowlist.ToArray());
        _modelsByTag = Models.ToDictionary(model => model.Tag, StringComparer.Ordinal);
        _routesByProfile = Routes.ToDictionary(route => route.Profile);
        _maintenanceAllowlist = new HashSet<string>(
            MaintenanceAllowlist,
            StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }

    public string CatalogVersion { get; }

    public IReadOnlyList<ModelCatalogEntry> Models { get; }

    public IReadOnlyList<TaskRouteEntry> Routes { get; }

    public IReadOnlyList<string> MaintenanceAllowlist { get; }

    public ModelCatalogEntry Model(string tag) =>
        _modelsByTag.TryGetValue(tag, out var model)
            ? model
            : throw new KeyNotFoundException($"Model '{tag}' is not in the routing catalog.");

    public TaskRouteEntry Route(LocalTaskProfile profile) =>
        _routesByProfile.TryGetValue(profile, out var route)
            ? route
            : throw new KeyNotFoundException(
                $"Task profile '{profile}' is not in the routing catalog.");

    public bool IsMaintenanceAllowed(string tag) =>
        _maintenanceAllowlist.Contains(tag);

    /// <summary>
    /// Whether routed chat must ask Ollama to switch the model's reasoning off. False for a
    /// model outside the catalog: the transport also carries native passthrough traffic, and
    /// an unknown tag there is the caller's own request to leave untouched.
    /// </summary>
    public bool DisablesThinking(string tag) =>
        _modelsByTag.TryGetValue(tag, out var model) && model.DisableThinking;

    public static ModelRoutingCatalog Load(string runtimeRoot) =>
        new(new ModelRoutingCatalogStore(runtimeRoot).Read());

    public static ModelRoutingCatalog LoadEmbedded() =>
        new(ModelRoutingCatalogStore.LoadEmbedded());
}
