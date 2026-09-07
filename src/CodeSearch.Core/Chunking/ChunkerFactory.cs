namespace CodeSearch.Core.Chunking;

/// <summary>
/// Decides which chunker (if any) handles a given file. Returning null means "don't index this".
/// </summary>
public static class ChunkerFactory
{
    private static readonly RoslynChunker CSharp = new();
    private static readonly GenericTextChunker Generic = new();

    /// <summary>
    /// Text formats worth embedding. Deliberately excludes lock files, minified bundles and
    /// anything else that is machine-written: those add tens of thousands of chunks and never
    /// answer a question anyone actually asks.
    /// </summary>
    private static readonly HashSet<string> GenericExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".vue", ".svelte",
        ".py", ".go", ".java", ".kt", ".rs", ".rb", ".php",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".ino",
        ".sql", ".graphql", ".proto",
        ".xaml", ".xml", ".razor", ".cshtml", ".html", ".htm", ".css", ".scss", ".less",
        ".md", ".mdx", ".txt", ".adoc",
        ".yml", ".yaml", ".toml", ".ini", ".conf",
        ".ps1", ".psm1", ".sh", ".bash",
        ".tf", ".bicep",
        ".csproj", ".props", ".targets", ".fsproj", ".vbproj",
        ".json",
    };

    private static readonly string[] ExcludedFileMarkers =
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", ".min.js", ".min.css",
        ".designer.cs", ".g.cs", ".g.i.cs", "assemblyinfo.cs", ".generated.cs",
    };

    public static bool IsIndexable(string path) => Resolve(path) is not null;

    /// <summary>
    /// The chunker for a file, given what the semantic index knows about it.
    /// </summary>
    /// <remarks>
    /// A file the adapters covered and reported definitions for is cut on those definitions.
    /// Everything else resolves exactly as it did before: C# through Roslyn, the rest through the
    /// window. A missing indexer, a disabled adapter or a language nothing parses therefore
    /// degrades to the previous behaviour rather than failing the build — which is the rule the
    /// whole feature is allowed to exist under.
    /// </remarks>
    public static IChunker? Resolve(string path, SymbolDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var chunker = Resolve(path);
        if (chunker is not GenericTextChunker)
        {
            return chunker;
        }

        var definitions = catalog.For(path);
        return definitions.Count > 0 ? new SymbolAwareChunker(definitions) : chunker;
    }

    /// <summary>
    /// Which chunker <see cref="Resolve(string, SymbolDefinitionCatalog)"/> would return, in a
    /// form that can be written into an index and compared to a later build's answer.
    ///
    /// A file's chunks may be reused only when this matches as well as its content hash. The two
    /// are genuinely independent: a path whose chunker comes from its extension is settled by the
    /// path alone, but a windowed path can be promoted to a symbol-aware one — or demoted back —
    /// purely by the semantic index moving underneath it, with every byte of the file unchanged
    /// (#388).
    ///
    /// The windowed state carries the empty fingerprint rather than no marker at all, so "the
    /// catalogue knows nothing about this file" and "the catalogue names three definitions in it"
    /// cannot compare equal.
    /// </summary>
    /// <summary>
    /// Bumped when the cutting itself changes, so every file is re-chunked on the next sync
    /// rather than keeping chunks whose boundaries or labels no longer describe them.
    ///
    /// This is the lever a format version would otherwise be used for, and it is the honest one:
    /// what changed is the chunker, and the chunker is exactly what reuse compares. r2 is the
    /// pass that stopped counting a trailing newline as a line and stopped announcing part one of
    /// a part count that was never cut.
    /// </summary>
    private const string Revision = "r2";

    public static string IdentityOf(string path, SymbolDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var chunker = Resolve(path);
        return chunker switch
        {
            null => string.Empty,
            not GenericTextChunker => Revision + ":" + chunker.GetType().Name,
            _ => Revision + ":window:" + catalog.Fingerprint(path),
        };
    }

    public static IChunker? Resolve(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var marker in ExcludedFileMarkers)
        {
            if (fileName.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return CSharp;
        }

        return GenericExtensions.Contains(ext) ? Generic : null;
    }
}
