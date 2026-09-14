namespace LocalAi.Documentation;

public sealed record DocumentationFact(string Id, string SymbolId, string Text);
public sealed record DiagramNode(string Id, string Label);
public sealed record DiagramEdge(string From, string To, string Kind);
public sealed record DocumentationHistory(string Commit, string Message);
public sealed record DocumentationInput(
    string SymbolId, string Name, string SourcePath, int SourceLine, string Snapshot,
    IReadOnlyList<DocumentationFact> Facts, IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges, IReadOnlyList<DocumentationHistory> History);
public sealed record DocumentationClaim(string[] FactIds, string English, string Russian);
public sealed record DocumentationDraft(
    DocumentationClaim[] Claims, string[] Examples, string DiagramSvg);

public sealed class DocumentationValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(", ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
