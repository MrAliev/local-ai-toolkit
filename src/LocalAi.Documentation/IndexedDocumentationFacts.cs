using CodeSearch.Core.Semantics;

namespace LocalAi.Documentation;

public static class IndexedDocumentationFacts
{
    public static DocumentationInput Create(SemanticIndex index, string symbolId, IReadOnlyList<DocumentationHistory> history)
    {
        var symbols = index.Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (!symbols.TryGetValue(symbolId, out var symbol)) throw new DocumentationValidationException(["unknown_symbol"]);
        var definitions = index.Occurrences.Where(o => o.SymbolId == symbolId && IsDefinition(o))
            .OrderBy(o => o.DocumentPath, StringComparer.Ordinal).ThenBy(o => o.Range.StartLine).ToArray();
        if (definitions.Length == 0) throw new DocumentationValidationException(["missing_precise_definition"]);
        var members = index.Occurrences.Where(o => IsDefinition(o) && definitions.Any(d =>
            o.DocumentPath == d.DocumentPath && d.EnclosingRange is { } range && Contains(range, o.Range)))
            .Select(o => o.SymbolId).Append(symbolId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var facts = new List<DocumentationFact>();
        void Add(string id, string text) => facts.Add(new("f" + facts.Count, id, text));
        Add(symbolId, symbol.Kind + ": " + symbol.Signature);
        foreach (var member in members.Where(id => id != symbolId))
        {
            if (!symbols.TryGetValue(member, out var value)) throw new DocumentationValidationException(["unknown_symbol"]);
            if (value.Kind is SemanticSymbolKind.Parameter or SemanticSymbolKind.Local) continue;
            Add(member, value.Kind + ": " + value.Signature);
        }
        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        foreach (var reference in index.Occurrences.Where(o => memberSet.Contains(o.SymbolId) &&
                     o.Precision == NavigationPrecision.Precise && o.Roles.HasFlag(SemanticOccurrenceRoles.Reference))
                     .OrderBy(o => o.DocumentPath, StringComparer.Ordinal).ThenBy(o => o.Range.StartLine).ThenBy(o => o.Range.StartCharacter))
            Add(reference.SymbolId, $"Reference: {reference.DocumentPath}:{reference.Range.StartLine + 1}");
        var relationships = index.Relationships.Where(r => r.SourceSymbolId == symbolId || r.TargetSymbolId == symbolId)
            .OrderBy(r => r.SourceSymbolId, StringComparer.Ordinal).ThenBy(r => r.TargetSymbolId, StringComparer.Ordinal).ThenBy(r => r.Kind).ToArray();
        var nodeIds = new[] { symbolId }.Concat(relationships.SelectMany(r => new[] { r.SourceSymbolId, r.TargetSymbolId }))
            .Distinct(StringComparer.Ordinal).ToArray();
        var nodes = nodeIds.Select((id, i) => new DiagramNode("n" + i,
            symbols.TryGetValue(id, out var value) ? value.DisplayName : throw new DocumentationValidationException(["unknown_symbol"]))).ToArray();
        var nodeMap = nodeIds.Select((id, i) => (id, Node: "n" + i)).ToDictionary(p => p.id, p => p.Node, StringComparer.Ordinal);
        var edges = relationships.Select(r => new DiagramEdge(nodeMap[r.SourceSymbolId], nodeMap[r.TargetSymbolId], r.Kind.ToString())).ToArray();
        foreach (var relationship in relationships)
        {
            Add(relationship.SourceSymbolId, $"{relationship.Kind}: {symbols[relationship.SourceSymbolId].DisplayName} → {symbols[relationship.TargetSymbolId].DisplayName}");
            if (relationship.TargetSymbolId != symbolId)
                Add(relationship.TargetSymbolId, symbols[relationship.TargetSymbolId].Kind + ": " + symbols[relationship.TargetSymbolId].Signature);
        }
        var definition = definitions[0];
        return new(symbolId, symbol.DisplayName, definition.DocumentPath, definition.Range.StartLine + 1,
            index.GitTree + (index.DirtyHash is null ? "" : ":" + index.DirtyHash), facts, nodes, edges, history);
    }

    private static bool IsDefinition(SemanticOccurrence occurrence) => occurrence.Precision == NavigationPrecision.Precise &&
        occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition);

    private static bool Contains(SourceRange enclosing, SourceRange value) =>
        (value.StartLine > enclosing.StartLine || value.StartLine == enclosing.StartLine && value.StartCharacter >= enclosing.StartCharacter) &&
        (value.EndLine < enclosing.EndLine || value.EndLine == enclosing.EndLine && value.EndCharacter <= enclosing.EndCharacter);
}
