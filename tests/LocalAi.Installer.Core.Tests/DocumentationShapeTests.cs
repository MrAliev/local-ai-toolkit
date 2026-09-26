using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every document in this repository exists in English and in Russian, and each one links to
/// the other.
///
/// The rule was written down long before anything checked it, and by the time anyone looked,
/// thirty-eight files under docs/superpowers had a translation and no way to reach it, one
/// document existed in Russian only, and one plan in English only. A rule nothing enforces is
/// a rule that decays quietly — which is exactly what a documentation rule cannot afford,
/// because nobody notices a missing translation until they are the person who needed it.
///
/// Test fixtures are exempt: files under a Fixtures directory are inputs to a test corpus,
/// not documentation, and translating one would change what the corpus measures.
/// </summary>
public sealed class DocumentationShapeTests
{
    private const string RussianSuffix = ".ru.md";
    private const string EnglishLink = "[English version](";
    private const string RussianLink = "[Русская версия](";

    [Fact]
    public void Every_document_has_a_translation()
    {
        var missing = Documents(RepositoryRoot())
            .Where(document => !File.Exists(Pair(document)))
            .Select(Relative)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These documents exist in one language only: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_document_links_to_its_translation()
    {
        var unlinked = new List<string>();
        foreach (var document in Documents(RepositoryRoot()))
        {
            var text = File.ReadAllText(document);
            var expected = Path.GetFileName(Pair(document));
            var marker = document.EndsWith(RussianSuffix, StringComparison.OrdinalIgnoreCase)
                ? EnglishLink
                : RussianLink;
            if (!text.Contains(marker + expected + ")", StringComparison.Ordinal))
            {
                unlinked.Add(Relative(document));
            }
        }

        Assert.True(
            unlinked.Count == 0,
            "These documents do not link to their translation: " +
            string.Join(", ", unlinked.OrderBy(path => path, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Excluded folders are never entered, so what lies inside them cannot fail the walk: a
    /// self-nesting node_modules under publish once did, with PathTooLongException.
    /// </summary>
    [Fact]
    public void An_excluded_folder_that_cannot_be_read_does_not_stop_the_walk()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-doc-walk-" + Guid.NewGuid().ToString("N"));
        var locked = Directory.CreateDirectory(Path.Combine(root, "publish", "locked"));
        File.WriteAllText(Path.Combine(root, "README.md"), "[Русская версия](README.ru.md)");
        File.WriteAllText(Path.Combine(root, "README.ru.md"), "[English version](README.md)");
        var deny = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var found = Documents(root)
                .Select(document => Path.GetRelativePath(root, document).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(["README.md", "README.ru.md"], found);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Every link into this repository reaches a file that exists. Most of docs/ was deleted on
    /// purpose, and a link left pointing into it promises a page there is none of.
    /// </summary>
    [Fact]
    public void Every_link_into_the_repository_reaches_a_file_that_exists()
    {
        var dead = Documents(RepositoryRoot())
            .SelectMany(document => DeadLinks(document).Select(link => $"{Relative(document)} -> {link}"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(dead.Length == 0, "These links lead nowhere: " + string.Join(", ", dead));
    }

    [Fact]
    public void A_link_to_a_deleted_page_is_reported_and_nothing_else_is()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-doc-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "images"));
        File.WriteAllText(Path.Combine(root, "docs", "kept.md"), string.Empty);
        File.WriteAllText(Path.Combine(root, "docs", "images", "mark.png"), string.Empty);
        var document = Path.Combine(root, "README.md");
        File.WriteAllText(
            document,
            """
            [kept](docs/kept.md#top) ![mark](docs/images/mark.png "Mark") [here](#section)
            [web](https://example.com/docs/overview.md) [folder](docs/images)
            [gone](docs/overview.md)
            [absolute](https://github.com/MrAliev/local-ai-toolkit/blob/main/docs/mcp-tools.md#tools)
            [absolute kept](https://github.com/MrAliev/local-ai-toolkit/blob/main/docs/kept.md)
            <img src="docs/images/missing.svg">
            [policy]: docs/code-signing-policy.md
            """);
        try
        {
            Assert.Equal(
                [
                    "docs/overview.md",
                    "https://github.com/MrAliev/local-ai-toolkit/blob/main/docs/mcp-tools.md#tools",
                    "docs/images/missing.svg",
                    "docs/code-signing-policy.md",
                ],
                DeadLinks(document));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly Regex LinkTargets = new(
        """\]\((?<target>[^)\s]+)(?:\s+"[^"]*")?\)|(?:src|href)="(?<target>[^"]+)"|^\s*\[[^\]]+\]:\s*(?<target>\S+)""",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex OwnRepository = new(
        @"^https://github\.com/MrAliev/local-ai-toolkit/(?:blob|tree|raw)/[^/]+/(?<path>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Link targets in one document that name a path in this repository and miss. A relative
    /// target resolves against the document's folder, an absolute one into this repository
    /// against its root; anything else on the web and an in-page anchor are not this test's
    /// business.
    /// </summary>
    private static List<string> DeadLinks(string document)
    {
        var folder = Path.GetDirectoryName(document)!;
        var root = RootOf(folder);
        var dead = new List<string>();
        foreach (Match match in LinkTargets.Matches(File.ReadAllText(document)))
        {
            var target = match.Groups["target"].Value;
            var own = OwnRepository.Match(target);
            string relative;
            string baseFolder;
            if (own.Success)
            {
                relative = own.Groups["path"].Value;
                baseFolder = root;
            }
            else if (target.StartsWith('#') || target.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }
            else
            {
                relative = target;
                baseFolder = folder;
            }

            var path = Uri.UnescapeDataString(relative.Split('#')[0]);
            var full = Path.GetFullPath(Path.Combine(baseFolder, path));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                dead.Add(target);
            }
        }

        return dead;
    }

    /// <summary>The repository root when there is one above the folder, else the folder itself — a test's own temporary tree.</summary>
    private static string RootOf(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        return folder;
    }

    private static string Pair(string document) =>
        document.EndsWith(RussianSuffix, StringComparison.OrdinalIgnoreCase)
            ? document[..^RussianSuffix.Length] + ".md"
            : document[..^".md".Length] + RussianSuffix;

    private static string Relative(string document) =>
        Path.GetRelativePath(RepositoryRoot(), document).Replace('\\', '/');

    /// <summary>
    /// Every markdown file the repository ships, minus test fixtures and anything under a
    /// build output directory — a published tree can carry a copy of somebody else's readme.
    /// </summary>
    private static IEnumerable<string> Documents(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var document in Directory.EnumerateFiles(directory, "*.md"))
            {
                yield return document;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!Excluded(directory == root, Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Checked before descending: build output, test fixtures, and the untracked trees at the root.</summary>
    private static bool Excluded(bool atRoot, string name) =>
        atRoot
            ? name.Equals("publish", StringComparison.OrdinalIgnoreCase) ||
              name.Equals(".claude", StringComparison.OrdinalIgnoreCase) ||
              name.Equals(".github", StringComparison.OrdinalIgnoreCase)
            : name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("Fixtures", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
