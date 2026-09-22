namespace CodeSearch.Core.Semantics;

/// <summary>The CLI/MCP wire view uses one-based, end-exclusive UTF-16 coordinates.</summary>
public static class SemanticImpactOutput
{
    public static object Create(SemanticImpactReport report) => new
    {
        schemaVersion = 1,
        scope = report.Scope,
        coordinateBase = 1,
        endExclusive = true,
        columnUnit = "utf16",
        snapshot = new { repositoryId = report.Snapshot.RepositoryId, generationId = report.Snapshot.GenerationId,
            gitTree = report.Snapshot.GitTree, dirtyHash = report.Snapshot.DirtyHash },
        symbolId = report.SymbolId,
        definitions = report.Definitions.Select(Source).ToArray(),
        evidence = report.Evidence.Select(edge => new { id = edge.Id, kind = edge.Kind, targetSymbolId = edge.TargetSymbolId,
            relatedSymbolId = edge.RelatedSymbolId, source = Source(edge.Source) }).ToArray(),
        totalEvidence = report.TotalEvidence,
        truncated = report.Truncated,
        limitations = new[] { "indexed-csharp-only", "no-transitive-closure", "no-runtime-breakage-prediction",
            "unsaved-buffers-excluded", "unverified-locations-excluded" },
    };

    private static object Source(SemanticImpactSource source) => new
    {
        path = source.Location.DocumentPath,
        startLine = SourcePosition.ToOneBased(source.Location.Range.StartLine),
        startColumn = SourcePosition.ToOneBased(source.Location.Range.StartCharacter),
        endLine = SourcePosition.ToOneBased(source.Location.Range.EndLine),
        endColumn = SourcePosition.ToOneBased(source.Location.Range.EndCharacter),
        symbolId = source.Location.SymbolId,
        roles = source.Location.Roles.ToString(),
        precision = source.Location.Precision.ToString(),
        documentSha256 = source.DocumentHash,
    };
}
