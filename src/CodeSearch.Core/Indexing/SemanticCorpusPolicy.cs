namespace CodeSearch.Core.Indexing;

/// <summary>
/// Which files a change to would change what the semantic layer knows.
///
/// The semantic phase is the expensive half of a sync — 36.72 seconds of 49.67 on this
/// repository, against 1.33 for a sync with nothing to do — and it runs again from nothing on
/// every edit, because the overlay it would reuse is keyed by a hash that every save changes
/// (#385). Most edits change nothing it could see. This is the line between the two.
///
/// The boundary is strict on purpose: a file counts unless it plainly cannot matter. Being wrong
/// in the safe direction costs half a minute of rebuilding; being wrong the other way answers
/// navigation from an overlay that no longer describes the code, which is the one thing the index
/// promises never to do.
/// </summary>
public static class SemanticCorpusPolicy
{
    /// <summary>
    /// What the SCIP TypeScript adapter is fed. One list, consulted both by the adapter choosing
    /// files and by the boundary below — two copies of it would let the boundary disagree with
    /// what was actually read.
    /// </summary>
    public static bool IsTypeScriptSource(string relativePath) =>
        HasExtension(relativePath, ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs");

    /// <summary>
    /// What the SCIP Python adapter is fed. `.pyi` belongs here because the adapter reads it: a
    /// stub declares the types everything importing the module is navigated by, so leaving it out
    /// let an edit to one pass for non-semantic and answered navigation from an overlay built
    /// before it.
    /// </summary>
    public static bool IsPythonSource(string relativePath) =>
        HasExtension(relativePath, ".py", ".pyi");

    /// <summary>
    /// The two halves are different in kind. The first is source the layer reads: C# through
    /// Roslyn, XAML through <c>XamlSemanticIndexer</c>, and the two languages the SCIP adapters
    /// cover. The second is what the solution is made of — nothing reads these as source, and
    /// changing one changes what the layer would find in everything else: a project reference
    /// added, a target framework moved, a file glob widened.
    /// </summary>
    public static bool ChangesSemantics(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return IsSemanticSource(relativePath) || ShapesTheBuild(relativePath);
    }

    private static bool IsSemanticSource(string relativePath) =>
        HasExtension(relativePath, ".cs", ".xaml") ||
        IsTypeScriptSource(relativePath) ||
        IsPythonSource(relativePath);

    /// <summary>
    /// `.json` is not here as an extension, deliberately: a repository is full of data in it, and
    /// only these two names are read by a build. The MSBuild files are matched by extension
    /// because their names vary — <c>Directory.Build.props</c>, <c>Directory.Packages.props</c>,
    /// a project's own <c>.csproj</c>.
    /// </summary>
    private static bool ShapesTheBuild(string relativePath) =>
        HasExtension(relativePath, ".csproj", ".props", ".targets", ".sln", ".slnx") ||
        HasFileName(relativePath, "tsconfig.json", "package.json");

    private static bool HasExtension(string relativePath, params string[] extensions) =>
        extensions.Any(extension =>
            relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compared on the last segment rather than on the whole path, and with either separator:
    /// Git hands these back with forward slashes and Windows with backslashes, and the same file
    /// has to answer the same way whichever arrives.
    /// </summary>
    private static bool HasFileName(string relativePath, params string[] names)
    {
        var name = relativePath[
            (relativePath.LastIndexOfAny(['/', '\\']) + 1)..];
        return names.Any(candidate =>
            string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
    }
}
