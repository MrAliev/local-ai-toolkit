using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Builds the page-by-page site under docs/ from the two READMEs: a page per <c>##</c> section,
/// a home page with the contents, and a contents list in each README itself. Nothing here is
/// written by hand, so a page cannot drift from the README it came from.
/// </summary>
internal static class DocumentationSite
{
    internal const string GeneratedMark = "<!-- Generated from the README by DocumentationSiteTests. -->";

    private const string Repository = "https://github.com/MrAliev/local-ai-toolkit";
    private const string ContentsStart = "<!-- contents:begin (generated, do not edit) -->";
    private const string ContentsEnd = "<!-- contents:end -->";

    private sealed record Language(
        string Suffix,
        string PairSuffix,
        string PairLabel,
        string Contents,
        string OnThisPage,
        string PageByPage);

    private static readonly Language EnglishText = new(
        ".md",
        ".ru.md",
        "Русская версия",
        "Contents",
        "On this page",
        "[Read it page by page](docs/README.md) — the same text, one section per page.");

    private static readonly Language RussianText = new(
        ".ru.md",
        ".md",
        "English version",
        "Оглавление",
        "На этой странице",
        "[Читать по страницам](docs/README.ru.md) — тот же текст, по разделу на страницу.");

    private sealed record Heading(int Level, string Text, string Anchor);

    private sealed record Section(string Title, List<string> Lines, List<Heading> Headings);

    private sealed record Target(int Page, string Anchor, bool IsTitle);

    private sealed class Document
    {
        public required List<string> Lines { get; init; }
        public required int FirstSection { get; init; }
        public required List<string> Intro { get; init; }
        public required List<Section> Sections { get; init; }
        public required List<Heading> ReadmeHeadings { get; init; }
        public required Dictionary<string, Target> Anchors { get; init; }
    }

    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex LinkTarget = new(
        """(?<=\]\()(?<target>[^)\s]+)(?=(?:\s+"[^"]*")?\))|(?<=(?:src|href)=")(?<target>[^"]+)(?=")|(?<=^\s*\[[^\]]+\]:\s*)(?<target>\S+)""",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyDictionary<string, string> Build(
        string english,
        string russian,
        Func<string, bool> isDirectory)
    {
        var en = Parse(english, "README.md");
        var ru = Parse(russian, "README.ru.md");
        if (en.Sections.Count != ru.Sections.Count)
        {
            throw new InvalidOperationException(
                $"README.md has {en.Sections.Count} sections and README.ru.md has {ru.Sections.Count}; each section needs its translation.");
        }

        var slugs = en.Sections.Select(section => Anchor(section.Title)).ToArray();
        var site = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (document, language, readme) in new[] { (en, EnglishText, "README.md"), (ru, RussianText, "README.ru.md") })
        {
            site[readme] = Readme(document, language);
            site["docs/README" + language.Suffix] = Home(document, language, slugs, isDirectory);
            for (var index = 0; index < document.Sections.Count; index++)
            {
                site["docs/" + slugs[index] + language.Suffix] = Page(document, language, slugs, index, isDirectory);
            }
        }

        return site;
    }

    /// <summary>The anchor GitHub gives a heading: lower case, words joined by hyphens, punctuation dropped.</summary>
    internal static string Anchor(string heading)
    {
        var builder = new StringBuilder();
        foreach (var rune in Plain(heading).ToLowerInvariant().EnumerateRunes())
        {
            if (rune.Value is ' ' or '-')
            {
                builder.Append('-');
            }
            else if (Rune.GetUnicodeCategory(rune) is
                     UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                     UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
                     UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
                     UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
                     UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or
                     UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation)
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>What a heading reads as once rendered: no code ticks, no emphasis, a link's text only.</summary>
    private static string Plain(string heading) =>
        Regex.Replace(heading, @"\[([^\]]*)\]\([^)]*\)", "$1", RegexOptions.CultureInvariant)
            .Replace("`", "", StringComparison.Ordinal)
            .Replace("*", "", StringComparison.Ordinal);

    private static string Unique(string anchor, Dictionary<string, int> seen)
    {
        if (seen.TryGetValue(anchor, out var count))
        {
            seen[anchor] = count + 1;
            return anchor + "-" + count.ToString(CultureInfo.InvariantCulture);
        }

        seen[anchor] = 1;
        return anchor;
    }

    private static Document Parse(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var start = lines.IndexOf(ContentsStart);
        if (start >= 0)
        {
            var end = lines.IndexOf(ContentsEnd, start);
            if (end < 0)
            {
                throw new InvalidOperationException($"{name} opens its contents list and never closes it.");
            }

            var removed = end - start + 1;
            if (end + 1 < lines.Count && lines[end + 1].Length == 0)
            {
                removed++;
            }

            lines.RemoveRange(start, removed);
        }

        var global = new Dictionary<string, int>(StringComparer.Ordinal);
        var readmeHeadings = new List<Heading>();
        var intro = new List<string>();
        var sections = new List<Section>();
        var anchors = new Dictionary<string, Target>(StringComparer.Ordinal);
        var local = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstSection = -1;
        foreach (var (line, index, inCode) in Walk(lines))
        {
            var match = inCode ? Match.Empty : HeadingLine.Match(line);
            if (match.Success)
            {
                var level = match.Groups[1].Value.Length;
                var title = match.Groups[2].Value;
                var readmeAnchor = Unique(Anchor(title), global);
                readmeHeadings.Add(new Heading(level, title, readmeAnchor));
                if (level == 2)
                {
                    firstSection = firstSection < 0 ? index : firstSection;
                    local = new Dictionary<string, int>(StringComparer.Ordinal);
                    sections.Add(new Section(title, [], []));
                    anchors[readmeAnchor] = new Target(sections.Count - 1, Unique(Anchor(title), local), IsTitle: true);
                    continue;
                }

                var pageAnchor = Unique(Anchor(title), local);
                anchors[readmeAnchor] = new Target(sections.Count - 1, pageAnchor, IsTitle: false);
                sections.LastOrDefault()?.Headings.Add(new Heading(level, title, pageAnchor));
            }

            if (sections.Count == 0)
            {
                intro.Add(line);
            }
            else
            {
                sections[^1].Lines.Add(match.Success && match.Groups[1].Value.Length > 2 ? line[1..] : line);
            }
        }

        if (sections.Count == 0)
        {
            throw new InvalidOperationException($"{name} has no ## sections to make pages of.");
        }

        return new Document
        {
            Lines = lines,
            FirstSection = firstSection,
            Intro = intro,
            Sections = sections,
            ReadmeHeadings = readmeHeadings,
            Anchors = anchors,
        };
    }

    /// <summary>Every line with whether it sits inside a fenced code block, fences included.</summary>
    private static IEnumerable<(string Line, int Index, bool InCode)> Walk(List<string> lines)
    {
        string? fence = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].TrimStart();
            var opens = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
                : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
                : null;
            if (fence is null && opens is not null)
            {
                fence = opens;
                yield return (lines[index], index, true);
                continue;
            }

            if (fence is not null)
            {
                if (opens == fence && trimmed.TrimEnd().Trim('`', '~').Length == 0)
                {
                    fence = null;
                }

                yield return (lines[index], index, true);
                continue;
            }

            yield return (lines[index], index, false);
        }
    }

    /// <summary>The README with its contents list after the introduction, ahead of the rule that closes it.</summary>
    private static string Readme(Document document, Language language)
    {
        var block = new List<string> { ContentsStart, $"**{language.Contents}**", "" };
        block.AddRange(document.ReadmeHeadings
            .Where(heading => heading.Level is 2 or 3)
            .Select(heading => $"{Indent(heading.Level - 2)}- [{Plain(heading.Text)}](#{heading.Anchor})"));
        block.AddRange(["", language.PageByPage, ContentsEnd, ""]);

        var lines = new List<string>(document.Lines);
        var at = document.FirstSection;
        while (at > 0 && lines[at - 1].Trim().Length == 0)
        {
            at--;
        }

        lines.InsertRange(at > 0 && lines[at - 1] == "---" ? at - 1 : document.FirstSection, block);
        return string.Join("\n", lines);
    }

    private static string Home(Document document, Language language, string[] slugs, Func<string, bool> isDirectory)
    {
        var intro = Trim(Rewrite(document.Intro, document, language, slugs, page: -1, isDirectory));
        var title = intro.FindIndex(line => line.StartsWith("# ", StringComparison.Ordinal));
        var lines = new List<string>();
        if (title >= 0)
        {
            lines.AddRange([intro[title], ""]);
            intro.RemoveRange(title, title + 1 < intro.Count && intro[title + 1].Length == 0 ? 2 : 1);
        }

        lines.AddRange(Trim(intro));
        lines.AddRange(["", "## " + language.Contents, ""]);
        for (var index = 0; index < document.Sections.Count; index++)
        {
            var page = slugs[index] + language.Suffix;
            lines.Add($"- [{Plain(document.Sections[index].Title)}]({page})");
            lines.AddRange(document.Sections[index].Headings
                .Where(heading => heading.Level == 3)
                .Select(heading => $"  - [{Plain(heading.Text)}]({page}#{heading.Anchor})"));
        }

        lines.AddRange(["", GeneratedMark]);
        return string.Join("\n", lines) + "\n";
    }

    private static string ContentsLink(Language language) =>
        $"[{language.Contents}](README{language.Suffix}#{Anchor(language.Contents)})";

    private static string Page(Document document, Language language, string[] slugs, int index, Func<string, bool> isDirectory)
    {
        var section = document.Sections[index];
        var lines = new List<string>
        {
            "# " + section.Title,
            "",
            $"{ContentsLink(language)} · [{language.PairLabel}]({slugs[index]}{language.PairSuffix})",
            "",
        };

        var subsections = section.Headings.Where(heading => heading.Level is 3 or 4).ToArray();
        if (subsections.Length >= 2)
        {
            lines.Add($"**{language.OnThisPage}**");
            lines.Add("");
            lines.AddRange(subsections.Select(heading => $"{Indent(heading.Level - 3)}- [{Plain(heading.Text)}](#{heading.Anchor})"));
            lines.Add("");
        }

        var body = Trim(Rewrite(section.Lines, document, language, slugs, index, isDirectory));
        if (body.Count > 0 && body[^1] == "---")
        {
            body = Trim(body.Take(body.Count - 1).ToList());
        }

        lines.AddRange(body);
        lines.Add("");
        lines.Add("---");
        lines.Add("");
        var bottom = new List<string>();
        if (index > 0)
        {
            bottom.Add($"← [{Plain(document.Sections[index - 1].Title)}]({slugs[index - 1]}{language.Suffix})");
        }

        bottom.Add(ContentsLink(language));
        if (index + 1 < document.Sections.Count)
        {
            bottom.Add($"[{Plain(document.Sections[index + 1].Title)}]({slugs[index + 1]}{language.Suffix}) →");
        }

        lines.AddRange([string.Join(" · ", bottom), "", GeneratedMark]);
        return string.Join("\n", lines) + "\n";
    }

    private static List<string> Rewrite(
        List<string> lines,
        Document document,
        Language language,
        string[] slugs,
        int page,
        Func<string, bool> isDirectory) =>
        Walk(lines)
            .Select(entry => entry.InCode
                ? entry.Line
                : LinkTarget.Replace(entry.Line, match => Retarget(match.Groups["target"].Value, document, language, slugs, page, isDirectory)))
            .ToList();

    private static string Retarget(
        string target,
        Document document,
        Language language,
        string[] slugs,
        int page,
        Func<string, bool> isDirectory)
    {
        if (target.StartsWith('#'))
        {
            var anchor = Uri.UnescapeDataString(target[1..]);
            if (!document.Anchors.TryGetValue(anchor, out var found))
            {
                throw new InvalidOperationException(
                    $"README{language.Suffix} links to {target}, and none of its headings has that anchor.");
            }

            if (found.Page == page)
            {
                return "#" + found.Anchor;
            }

            var file = found.Page < 0 ? "README" + language.Suffix : slugs[found.Page] + language.Suffix;
            return found.IsTitle ? file : file + "#" + found.Anchor;
        }

        if (target.Contains(':', StringComparison.Ordinal) || target is "README.md" or "README.ru.md")
        {
            return target;
        }

        if (target.StartsWith("docs/", StringComparison.Ordinal))
        {
            return target["docs/".Length..];
        }

        var path = target.Split('#')[0];
        return $"{Repository}/{(isDirectory(path) ? "tree" : "blob")}/main/{target}";
    }

    private static List<string> Trim(List<string> lines)
    {
        var first = lines.FindIndex(line => line.Trim().Length > 0);
        var last = lines.FindLastIndex(line => line.Trim().Length > 0);
        return first < 0 ? [] : lines.GetRange(first, last - first + 1);
    }

    private static string Indent(int depth) => new(' ', depth * 2);
}
