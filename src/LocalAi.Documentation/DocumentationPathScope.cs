namespace LocalAi.Documentation;

/// <summary>A literal repository-relative file or directory selector, with path-segment boundaries.</summary>
public static class DocumentationPathScope
{
    public static string? Normalize(string? path)
    {
        if (path is null) return null;
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Contains('\0'))
            throw new DocumentationValidationException(["scope_invalid"]);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..", StringComparer.Ordinal)) throw new DocumentationValidationException(["scope_invalid"]);
        var normalized = string.Join('/', parts.Where(part => part != "."));
        return normalized.Length == 0 ? "." : normalized;
    }

    public static bool Matches(string documentPath, string? normalizedPath) => normalizedPath is null or "."
        || documentPath == normalizedPath || documentPath.StartsWith(normalizedPath + "/", StringComparison.Ordinal);
}
