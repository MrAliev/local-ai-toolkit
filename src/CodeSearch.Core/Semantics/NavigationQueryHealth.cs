using CodeSearch.Core.Resources;
using CodeSearch.Core.Search;

namespace CodeSearch.Core.Semantics;

/// <summary>Evidence for one requested query, separate from cheap index-header evidence.</summary>
public sealed record NavigationQueryHealth(
    string HeaderEvidence,
    string Freshness,
    string Language,
    string Coverage,
    string Path,
    int Line,
    int Column,
    string Operation,
    string Precision,
    int? ResultCount,
    string? Reason)
{
    public static async Task<NavigationQueryHealth> ProbeAsync(
        IndexStatus status,
        SemanticNavigationGateway? gateway,
        string path,
        int line,
        int column,
        string operation,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SemanticDocumentPath.IsRepositoryRelative(path))
            throw new ArgumentException("path_not_relative", nameof(path));
        if (!SourcePosition.TryFromOneBased(line, column, out var position))
            throw new ArgumentOutOfRangeException(nameof(line), "invalid_position");
        if (operation is not ("definition" or "references"))
            throw new ArgumentException("invalid_navigation_operation", nameof(operation));

        var health = new NavigationQueryHealth(
            status.NavigationState,
            !status.Exists || status.IndexedCommit.Length == 0 || status.CurrentCommit.Length == 0
                ? "Unknown" : status.CommitDrifted ? "Stale" : "Current",
            System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".cs" => "CSharp",
                ".ts" or ".tsx" or ".js" or ".jsx" => "TypeScriptJavaScript",
                ".py" => "Python",
                _ => "Unknown"
            },
            // Neither a readable header nor a location list proves language/document coverage.
            "Unknown", path, line, column, operation, "Unknown", null, null);
        if (gateway is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return health with { Reason = IndexText.NavigationProbeUnavailable };
        }

        try
        {
            // Exactly one existing operation; no repository scan, index rebuild or health cache.
            var outcome = operation == "definition"
                ? await gateway.ResolveDefinitionAsync(path, position.Line, position.Utf16Column, root, cancellationToken)
                : await gateway.ResolveReferencesAsync(path, position.Line, position.Utf16Column, false, root, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var precision = outcome.Locations.Count == 0 ? "Unknown"
                : outcome.Locations.All(location => location.Precision == NavigationPrecision.Precise)
                    ? "Precise" : "Heuristic";
            return health with
            {
                Precision = precision,
                ResultCount = outcome.Locations.Count,
                Reason = outcome.Degradation?.Sentence ??
                    (outcome.Locations.Count == 0 ? IndexText.NavigationProbeNoLocations : null)
            };
        }
        catch (SemanticNavigationNotReadyException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return health with { Reason = exception.Degradation.Sentence };
        }
    }
}
