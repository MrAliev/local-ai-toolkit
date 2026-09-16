using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Resources;

namespace CodeSearch.Core.Search;

public sealed record SearchEvaluationScope(int SchemaVersion, IReadOnlyList<string> ExcludedPaths)
{
    public string Method => "exclude_before_ranking_v1";
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ExcludedPaths)));

    public static SearchEvaluationScope Create(string root, string corpusPath, SearchEvaluationCorpus corpus)
    {
        SearchEvaluationCorpus.Validate(corpus);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        corpusPath = Path.GetFullPath(corpusPath);
        var exclusions = new List<string>(corpus.ExcludedPaths ?? []);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (corpusPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            exclusions.Add(Path.GetRelativePath(root, corpusPath));
        }

        var paths = SearchPathExclusions.Normalize(exclusions);
        foreach (var item in corpus.Cases)
        {
            foreach (var target in item.Relevant)
            {
                if (SearchPathExclusions.Contains(paths, target.Path))
                {
                    throw new InvalidDataException(IndexText.EvaluationTargetExcluded(target.Path, item.Id));
                }
            }
        }

        return new(1, paths);
    }
}

internal static class SearchPathExclusions
{
    public static string[] Normalize(IReadOnlyList<string> paths) => paths.Select(path =>
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException(IndexText.EvaluationExcludedPathBlank);
        var normalized = path.Replace('\\', '/');
        var segments = normalized.TrimEnd('/').Split('/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('*') ||
            normalized.Contains('?') || segments.Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException(IndexText.EvaluationExcludedPathInvalid(path));
        }

        return normalized;
    }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    public static bool Contains(IReadOnlyList<string> exclusions, string path)
    {
        if (exclusions.Count == 0) return false;
        var normalized = path.Replace('\\', '/');
        return exclusions.Any(exclusion => exclusion.EndsWith('/')
            ? normalized.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase)
            : normalized.Equals(exclusion, StringComparison.OrdinalIgnoreCase));
    }
}
