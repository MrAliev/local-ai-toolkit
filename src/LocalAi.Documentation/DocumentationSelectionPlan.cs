using System.Text.Json.Serialization;

namespace LocalAi.Documentation;

public sealed record DocumentationPlannedUnit(
    [property: JsonPropertyName("symbolId")] string SymbolId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sourcePaths")] IReadOnlyList<string> SourcePaths,
    [property: JsonPropertyName("dependencyPaths")] IReadOnlyList<string> DependencyPaths,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("referencesHash")] string ReferencesHash,
    [property: JsonPropertyName("historyHash")] string HistoryHash);

public sealed record DocumentationSelectionPlan(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("snapshot")] string Snapshot,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("limit")] int? Limit,
    [property: JsonPropertyName("units")] IReadOnlyList<DocumentationPlannedUnit> Units)
{
    public static DocumentationSelectionPlan Create(DocumentationBuildInput input, string? path, int limit) =>
        new(input.WorkingRoot, input.Snapshot, DocumentationPathScope.Normalize(path), limit == int.MaxValue ? null : limit,
            input.Units.Select(unit => new DocumentationPlannedUnit(unit.Content.SymbolId, unit.Content.Name,
                unit.SourcePaths, unit.DependencyPaths, unit.SourceHash, unit.ReferencesHash, unit.HistoryHash)).ToArray());
}
