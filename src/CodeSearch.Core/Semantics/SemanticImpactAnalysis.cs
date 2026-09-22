using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeSearch.Core.Semantics;

/// <summary>A source coordinate and the indexed SHA-256 of that entire document.</summary>
public sealed record SemanticImpactSource(SemanticLocation Location, string DocumentHash);

/// <summary>A direct indexed use or an incoming relationship, not a prediction of a runtime failure.</summary>
public sealed record SemanticImpactEvidence(
    string Id,
    string Kind,
    string TargetSymbolId,
    string? RelatedSymbolId,
    SemanticImpactSource Source);

public sealed record SemanticImpactReport(
    SemanticSnapshotIdentity Snapshot,
    string SymbolId,
    IReadOnlyList<SemanticImpactSource> Definitions,
    IReadOnlyList<SemanticImpactEvidence> Evidence,
    int TotalEvidence)
{
    public string Scope => "direct-indexed-csharp";
    public bool Truncated => Evidence.Count < TotalEvidence;
}

/// <summary>Builds evidence for the first C# impact pilot without heuristic or live-buffer fallback.</summary>
public static class SemanticImpactAnalysis
{
    public static SemanticImpactReport Analyze(SemanticIndex index, string documentPath, int line, int utf16Column,
        SemanticSnapshotIdentity snapshot, int limit = 200, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(index);
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        var symbol = new SemanticNavigationService(index).ResolveOccurrence(documentPath, line, utf16Column, snapshot)
            ?? throw new InvalidOperationException("impact_symbol_not_found");
        if (!IsCSharp(symbol.DocumentPath) || symbol.Precision != NavigationPrecision.Precise)
            throw new InvalidOperationException("impact_precise_csharp_required");

        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in index.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCSharp(document.RelPath) && document.Hash.Length == 32)
                documents.Add(document.RelPath.Replace('\\', '/'), Convert.ToHexStringLower(document.Hash));
        }
        if (!documents.ContainsKey(symbol.DocumentPath))
            throw new InvalidOperationException("impact_source_hash_required");
        SemanticImpactSource? Source(SemanticOccurrence occurrence)
        {
            var path = occurrence.DocumentPath.Replace('\\', '/');
            return occurrence.Precision == NavigationPrecision.Precise && documents.TryGetValue(path, out var hash)
                ? new(new(path, occurrence.Range, occurrence.SymbolId, occurrence.Roles, occurrence.Precision), hash)
                : null;
        }
        var definitions = new Dictionary<string, List<SemanticImpactSource>>(StringComparer.Ordinal);
        var evidence = new List<SemanticImpactEvidence>();
        void Add(string kind, string? related, SemanticImpactSource source)
        {
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { kind, target = symbol.SymbolId, related, source }))));
            evidence.Add(new(id, kind, symbol.SymbolId, related, source));
        }
        foreach (var occurrence in index.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) && occurrence.SymbolId != symbol.SymbolId) continue;
            if (Source(occurrence) is not { } source) continue;
            if (occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition))
            {
                if (!definitions.TryGetValue(occurrence.SymbolId, out var list))
                    definitions.Add(occurrence.SymbolId, list = []);
                list.Add(source);
            }
            if (occurrence.SymbolId == symbol.SymbolId && occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Reference))
                Add("reference", null, source);
        }
        if (!definitions.TryGetValue(symbol.SymbolId, out var selectedDefinitions))
            throw new InvalidOperationException("impact_indexed_definition_required");
        foreach (var relationship in index.Relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relationship.TargetSymbolId != symbol.SymbolId ||
                !definitions.TryGetValue(relationship.SourceSymbolId, out var sources)) continue;
            var kind = relationship.Kind switch
            {
                SemanticRelationshipKind.Implementation => "implementation",
                SemanticRelationshipKind.Override => "override",
                SemanticRelationshipKind.TypeDefinition => "type-definition",
                _ => null,
            };
            if (kind is null) continue;
            foreach (var source in sources) Add(kind, relationship.SourceSymbolId, source);
        }
        var ordered = evidence.Distinct().OrderBy(edge => edge.Source.Location.DocumentPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.Location.Range.StartLine).ThenBy(edge => edge.Source.Location.Range.StartCharacter)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        var selected = selectedDefinitions.Distinct().OrderBy(source => source.Location.DocumentPath, StringComparer.Ordinal)
            .ThenBy(source => source.Location.Range.StartLine).ThenBy(source => source.Location.Range.StartCharacter).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(snapshot, symbol.SymbolId, selected, ordered.Take(limit).ToArray(), ordered.Length);
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
