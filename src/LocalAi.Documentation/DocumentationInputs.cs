using CodeSearch.Core.Semantics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LocalAi.Documentation;

public static class DocumentationInputs
{
    public static IReadOnlyList<DocumentationUnitInput> Create(SemanticIndex index,
        IReadOnlyDictionary<string, string> sources, Func<IReadOnlyList<string>, IReadOnlyList<DocumentationHistory>> history,
        int limit = 5)
        => CreateScoped(index, path => sources.TryGetValue(path, out var source) ? source :
            throw new DocumentationValidationException(["source_missing"]), history, limit);

    public static IReadOnlyList<DocumentationUnitInput> CreateScoped(SemanticIndex index,
        Func<string, string> readSource, Func<IReadOnlyList<string>, IReadOnlyList<DocumentationHistory>> history,
        int limit = 5, string? path = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var selectedPath = DocumentationPathScope.Normalize(path);
        var definitions = index.Occurrences.Where(o => o.Precision == NavigationPrecision.Precise && o.Roles.HasFlag(SemanticOccurrenceRoles.Definition))
            .OrderBy(o => o.DocumentPath, StringComparer.Ordinal).ThenBy(o => o.Range.StartLine).ThenBy(o => o.Range.StartCharacter).ToArray();
        var symbols = index.Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var files = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        SourceText Source(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(path, out var text)) files.Add(path, text = SourceText.From(readSource(path)));
            return text;
        }
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        var publicPositions = new HashSet<(string Path, int Line, int Column)>();
        bool PublicType(SemanticOccurrence definition)
        {
            var path = definition.DocumentPath;
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            if (parsed.Add(path))
            {
                var text = Source(path);
                var tree = CSharpSyntaxTree.ParseText(text);
                foreach (var type in tree.GetRoot().DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).Where(IsPublic))
                {
                    var identifier = type is BaseTypeDeclarationSyntax declaration ? declaration.Identifier : ((DelegateDeclarationSyntax)type).Identifier;
                    var location = text.Lines.GetLinePosition(identifier.SpanStart);
                    publicPositions.Add((path, location.Line, location.Character));
                }
            }
            return publicPositions.Contains((path, definition.Range.StartLine, definition.Range.StartCharacter));
        }
        var selected = definitions.Where(o =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return DocumentationPathScope.Matches(o.DocumentPath, selectedPath) && symbols[o.SymbolId].Kind == SemanticSymbolKind.Type && PublicType(o);
            })
            .Select(o => o.SymbolId).Distinct(StringComparer.Ordinal).Take(limit);
        var units = new List<DocumentationUnitInput>();
        foreach (var symbolId in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = definitions.Where(o => o.SymbolId == symbolId).ToArray();
            var ownedSymbols = definitions.Where(d => own.Any(o => Inside(o, d))).Select(o => o.SymbolId).Append(symbolId).ToHashSet(StringComparer.Ordinal);
            var references = index.Occurrences.Where(o => o.Precision == NavigationPrecision.Precise && o.Roles.HasFlag(SemanticOccurrenceRoles.Reference) &&
                    (ownedSymbols.Contains(o.SymbolId) || own.Any(d => Inside(d, o))))
                .OrderBy(o => o.DocumentPath, StringComparer.Ordinal).ThenBy(o => o.Range.StartLine).ThenBy(o => o.Range.StartCharacter).ThenBy(o => o.SymbolId, StringComparer.Ordinal).ToArray();
            var relationships = index.Relationships.Where(r => ownedSymbols.Contains(r.SourceSymbolId) || ownedSymbols.Contains(r.TargetSymbolId))
                .OrderBy(r => r.SourceSymbolId, StringComparer.Ordinal).ThenBy(r => r.TargetSymbolId, StringComparer.Ordinal).ThenBy(r => r.Kind).ToArray();
            var dependencyIds = references.Select(r => r.SymbolId).Concat(relationships.SelectMany(r => new[] { r.SourceSymbolId, r.TargetSymbolId }))
                .Where(id => !ownedSymbols.Contains(id)).ToHashSet(StringComparer.Ordinal);
            var dependencyDefinitions = definitions.Where(o => dependencyIds.Contains(o.SymbolId)).ToArray();
            string Body(SemanticOccurrence definition)
            {
                var text = Source(definition.DocumentPath);
                if (definition.EnclosingRange is not { } range) return text.ToString();
                var start = text.Lines.GetPosition(new(range.StartLine, range.StartCharacter));
                var end = text.Lines.GetPosition(new(range.EndLine, range.EndCharacter));
                return text.ToString(TextSpan.FromBounds(start, end));
            }
            var paths = own.Concat(dependencyDefinitions).Select(o => o.DocumentPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var historyItems = history(paths);
            var content = IndexedDocumentationFacts.Create(index, symbolId, historyItems);
            var sourceHash = Hash(own.Select(o => new { o.DocumentPath, Body = Body(o), symbols[o.SymbolId].Signature }).ToArray());
            var referenceHash = Hash(new { references, relationships,
                Definitions = dependencyDefinitions.Select(o => new { o.DocumentPath, o.SymbolId, Body = Body(o), symbols[o.SymbolId].Signature }).ToArray(),
                Symbols = dependencyIds.Order(StringComparer.Ordinal).Select(id => symbols.GetValueOrDefault(id)).ToArray(),
                content.Facts, content.Nodes, content.Edges });
            units.Add(new(content, sourceHash, referenceHash, Hash(historyItems))
            {
                SourcePaths = own.Select(o => o.DocumentPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                DependencyPaths = dependencyDefinitions.Select(o => o.DocumentPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedPath is not null && units.Count == 0) throw new DocumentationValidationException(["scope_empty"]);
        return units;
    }

    private static bool IsPublic(SyntaxNode type) => type.AncestorsAndSelf().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).All(declaration =>
        Modifiers(declaration).Any(SyntaxKind.PublicKeyword) || declaration.Parent is InterfaceDeclarationSyntax &&
        !Modifiers(declaration).Any(SyntaxKind.PrivateKeyword) && !Modifiers(declaration).Any(SyntaxKind.ProtectedKeyword) && !Modifiers(declaration).Any(SyntaxKind.InternalKeyword));

    private static SyntaxTokenList Modifiers(SyntaxNode node) => node is BaseTypeDeclarationSyntax type ? type.Modifiers : ((DelegateDeclarationSyntax)node).Modifiers;

    private static bool Inside(SemanticOccurrence enclosing, SemanticOccurrence occurrence) => enclosing.DocumentPath == occurrence.DocumentPath &&
        enclosing.EnclosingRange is { } range &&
        (occurrence.Range.StartLine > range.StartLine || occurrence.Range.StartLine == range.StartLine && occurrence.Range.StartCharacter >= range.StartCharacter) &&
        (occurrence.Range.EndLine < range.EndLine || occurrence.Range.EndLine == range.EndLine && occurrence.Range.EndCharacter <= range.EndCharacter);

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
