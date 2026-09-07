using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Core.Chunking;

/// <summary>
/// One definition as the chunker sees it: where its name is written, and where its body runs —
/// or a null body for a declaration the indexer named but reported no span for.
/// </summary>
/// <remarks>
/// The name range is carried alongside the body because the body alone cannot be named. A symbol
/// id is composed by the indexer — <c>shapes/Invoice#total().</c> from scip-python, <c>local 3</c>
/// for anything scip-typescript declares inside a function body — and the display name is empty
/// more often than not. The identifier as written in the source is always there, and slicing the
/// name range out of the file is the one way to get it that works for both languages.
///
/// A null <paramref name="Body"/> is the shape <c>export const X = memo(() =&gt; …)</c> has:
/// scip-typescript names it and reports no <c>enclosing_range</c> for it. The chunker infers the
/// boundary rather than dropping the declaration, which is why the catalog carries it at all.
/// </remarks>
public sealed record SymbolDefinition(SourceRange Name, SourceRange? Body);

/// <summary>
/// Definition bodies per file, taken from the semantic index that was built before embedding.
/// </summary>
public sealed class SymbolDefinitionCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SymbolDefinition>> _byPath;

    private SymbolDefinitionCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<SymbolDefinition>> byPath) =>
        _byPath = byPath;

    /// <summary>
    /// What a build with no semantic phase, a disabled adapter or a missing indexer produces.
    /// Every file then goes to the chunker it went to before this existed.
    /// </summary>
    public static SymbolDefinitionCatalog Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<SymbolDefinition>>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => _byPath.Count == 0;

    public IReadOnlyList<SymbolDefinition> For(string relPath) =>
        _byPath.TryGetValue(Normalize(relPath), out var definitions)
            ? definitions
            : [];

    /// <summary>
    /// A short, stable digest of exactly the definitions <see cref="For"/> hands the chunker for
    /// one file — the spans it cuts on, in the order it receives them.
    ///
    /// It exists so a later build can tell whether a file whose bytes are unchanged would still be
    /// cut the same way. It would not always: these definitions come from the semantic index,
    /// which is rebuilt on its own schedule, so a file can keep every byte and change its
    /// boundaries. Reusing its chunks then would keep vectors that describe text at spans the
    /// chunk list no longer names (#388).
    ///
    /// Empty for a file with no definitions, which is a fact about the catalogue rather than about
    /// the file: the same path resolves to a windowed chunker in that state and a symbol-aware one
    /// in the other, so the two must not compare equal.
    /// </summary>
    public string Fingerprint(string relPath)
    {
        var definitions = For(relPath);
        if (definitions.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(definitions.Count * 24);
        foreach (var definition in definitions)
        {
            Append(builder, definition.Name);
            builder.Append(definition.Body is { } body ? '=' : '-');
            if (definition.Body is { } present)
            {
                Append(builder, present);
            }

            builder.Append(';');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];

        static void Append(StringBuilder builder, SourceRange range) =>
            builder
                .Append(range.StartLine).Append(',')
                .Append(range.StartCharacter).Append(',')
                .Append(range.EndLine).Append(',')
                .Append(range.EndCharacter);
    }

    /// <summary>
    /// Collects every definition occurrence the chunker can cut on: the ones that report a body,
    /// and the declarations a SCIP adapter named without reporting one.
    /// </summary>
    /// <remarks>
    /// References are skipped because they are not definitions. A definition without a body span
    /// is kept only when it is a SCIP declaration, because only there is the boundary inferable
    /// and only there is the result worth a vector:
    ///
    /// <list type="bullet">
    /// <item>The scheme has to be a SCIP one. The Roslyn and XAML indexers also report definitions
    /// with no body — every <c>x:Key</c> and <c>x:Name</c> in every XAML file — and a resource key
    /// is not a declaration whose extent can be read off the next declaration. Those ids start
    /// with <c>dotnet</c>; a SCIP one starts with the adapter's scheme.</item>
    /// <item>Locals are skipped. scip-typescript reports <c>local 3</c> for everything declared
    /// inside a function body, with no name in the id and no body span, and those regions stay
    /// with the sliding window rather than being guessed at.</item>
    /// <item>The last descriptor has to be a term (<c>.</c>) or a type (<c>#</c>). Measured on a
    /// 2 653-file React repository, dropping that condition admits 13 308 meta descriptors
    /// (<c>authority0:</c>) — one per property of every object literal in the repository — and a
    /// vector per property of a config object is exactly the corpus bloat symbol-aware chunking
    /// avoids for C# DTOs.</item>
    /// </list>
    /// </remarks>
    public static SymbolDefinitionCatalog FromSemanticIndex(SemanticIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var byPath = new Dictionary<string, List<SymbolDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in index.Occurrences)
        {
            if (!occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition))
            {
                continue;
            }

            var body = occurrence.EnclosingRange;
            if (body is null && !IsInferableDeclaration(occurrence.SymbolId))
            {
                continue;
            }

            var path = Normalize(occurrence.DocumentPath);
            if (!byPath.TryGetValue(path, out var definitions))
            {
                definitions = [];
                byPath[path] = definitions;
            }

            definitions.Add(new SymbolDefinition(occurrence.Range, body));
        }

        return new SymbolDefinitionCatalog(
            byPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SymbolDefinition>)pair.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a bodiless definition is a declaration whose extent the chunker may infer.
    /// </summary>
    /// <remarks>
    /// A future adapter — scip-go, scip-java — arrives with its own scheme and is admitted by the
    /// same prefix. One that does not spell its scheme <c>scip-…</c> would have to be named here,
    /// and until it is, its files keep the window they have today rather than gaining a boundary
    /// nobody measured.
    /// </remarks>
    private static bool IsInferableDeclaration(string symbolId) =>
        symbolId.StartsWith("scip-", StringComparison.Ordinal) &&
        !symbolId.StartsWith("scip-local ", StringComparison.Ordinal) &&
        symbolId.Length > 0 &&
        (symbolId[^1] == '.' || symbolId[^1] == '#');

    // The semantic index stores forward slashes and the scanner hands out platform separators.
    // A catalog keyed one way and queried the other silently degrades every file to the window,
    // which is the failure that looks exactly like "the feature does nothing".
    private static string Normalize(string relPath) => relPath.Replace('\\', '/');
}
