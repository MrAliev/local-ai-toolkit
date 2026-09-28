namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The READMEs stay the one place the documentation is written; docs/ holds the same text a
/// section to a page, so it can be read — and served by GitHub Pages — one part at a time.
/// </summary>
public sealed class DocumentationSiteTests
{
    private const string WriteSwitch = "LOCALAI_WRITE_DOCUMENTATION_SITE";

    private const string English =
        """
        ![logo](docs/images/logo.png)

        # Tool

        [Русская версия](README.ru.md)

        Intro, under the [licence](LICENSE).

        ---

        ## First part

        See [the detail](#deep-detail) and [this one](#first-sub).

        ### First sub

        ```text
        ### not a heading
        [not a link](LICENSE)
        ```

        ## Second part

        ### Deep detail

        ![diagram](docs/images/d.svg), [the scripts](scripts) and [one of them](scripts/run.ps1#L2).

        """;

    private const string Russian =
        """
        ![логотип](docs/images/logo.png)

        # Tool

        [English version](README.md)

        Вступление, по [лицензии](LICENSE).

        ---

        ## Первая часть

        См. [подробности](#подробности).

        ### Первый подраздел

        ## Вторая часть

        ### Подробности

        Текст.

        """;

    private static IReadOnlyDictionary<string, string> Site() =>
        DocumentationSite.Build(English, Russian, path => path == "scripts");

    [Fact]
    public void Every_section_becomes_a_page_in_each_language_and_names_its_pair()
    {
        var site = Site();

        Assert.Contains("[Русская версия](first-part.ru.md)", site["docs/first-part.md"], StringComparison.Ordinal);
        Assert.Contains("[English version](first-part.md)", site["docs/first-part.ru.md"], StringComparison.Ordinal);
        Assert.Contains("[Русская версия](second-part.ru.md)", site["docs/second-part.md"], StringComparison.Ordinal);
        Assert.Contains("[English version](second-part.md)", site["docs/second-part.ru.md"], StringComparison.Ordinal);
        Assert.Contains("[Русская версия](README.ru.md)", site["docs/README.md"], StringComparison.Ordinal);
        Assert.Contains("[English version](README.md)", site["docs/README.ru.md"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Jekyll takes a page's title from its first line, so the title comes before anything else.
    /// The line under it stays put from page to page; the neighbours wait at the bottom, for a
    /// reader who has finished this one.
    /// </summary>
    [Fact]
    public void A_page_opens_with_its_title_and_ends_with_its_neighbours()
    {
        var site = Site();

        Assert.StartsWith(
            "# First part\n\n[Contents](README.md#contents) · [Русская версия](first-part.ru.md)\n",
            site["docs/first-part.md"],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "# Вторая часть\n\n[Оглавление](README.ru.md#оглавление) · [English version](second-part.md)\n",
            site["docs/second-part.ru.md"],
            StringComparison.Ordinal);
        Assert.EndsWith(
            "\n---\n\n[Contents](README.md#contents) · [Second part](second-part.md) →\n\n" + DocumentationSite.GeneratedMark + "\n",
            site["docs/first-part.md"],
            StringComparison.Ordinal);
        Assert.EndsWith(
            "\n---\n\n← [Первая часть](first-part.ru.md) · [Оглавление](README.ru.md#оглавление)\n\n" + DocumentationSite.GeneratedMark + "\n",
            site["docs/second-part.ru.md"],
            StringComparison.Ordinal);
    }

    /// <summary>A page with two subsections or more lists them under its top line.</summary>
    [Fact]
    public void A_page_with_subsections_lists_them_and_one_with_a_single_one_does_not()
    {
        // The sample's line endings are the checkout's, so they are settled before a line is matched.
        var english = English.ReplaceLineEndings("\n")
            .Replace("## Second part\n", "## Second part\n\n### Overview\n", StringComparison.Ordinal);

        var site = DocumentationSite.Build(english, Russian, _ => false);

        Assert.Contains(
            "**On this page**\n\n- [Overview](#overview)\n- [Deep detail](#deep-detail)\n",
            site["docs/second-part.md"],
            StringComparison.Ordinal);
        Assert.DoesNotContain("**On this page**", site["docs/first-part.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_into_another_section_follows_it_to_that_page()
    {
        var site = Site();

        Assert.Contains("[the detail](second-part.md#deep-detail)", site["docs/first-part.md"], StringComparison.Ordinal);
        Assert.Contains("[this one](#first-sub)", site["docs/first-part.md"], StringComparison.Ordinal);
        Assert.Contains("[подробности](second-part.ru.md#подробности)", site["docs/first-part.ru.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_a_heading_that_does_not_exist_is_refused()
    {
        var broken = English.Replace("(#deep-detail)", "(#no-such-heading)", StringComparison.Ordinal);

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            DocumentationSite.Build(broken, Russian, _ => false));

        Assert.Contains("#no-such-heading", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Anything outside docs/ is not part of the site, so a link to it goes to GitHub instead.</summary>
    [Fact]
    public void A_repository_path_leads_to_github_and_an_image_stays_in_the_site()
    {
        var site = Site();
        var page = site["docs/second-part.md"];

        Assert.Contains("![diagram](images/d.svg)", page, StringComparison.Ordinal);
        Assert.Contains("[the scripts](https://github.com/MrAliev/local-ai-toolkit/tree/main/scripts)", page, StringComparison.Ordinal);
        Assert.Contains("[one of them](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/run.ps1#L2)", page, StringComparison.Ordinal);
        Assert.Contains("![logo](images/logo.png)", site["docs/README.md"], StringComparison.Ordinal);
        Assert.Contains("[licence](https://github.com/MrAliev/local-ai-toolkit/blob/main/LICENSE)", site["docs/README.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Headings_rise_one_level_and_code_is_left_alone()
    {
        var page = Site()["docs/first-part.md"];

        Assert.Contains("\n## First sub\n", page, StringComparison.Ordinal);
        Assert.Contains("\n### not a heading\n[not a link](LICENSE)\n", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The home page opens with the README's title, then the rest of its introduction, then the
    /// contents every page links back to.
    /// </summary>
    [Fact]
    public void The_home_page_opens_with_the_title_and_lists_every_page_and_its_subsections()
    {
        var site = Site();

        Assert.StartsWith("# Tool\n\n![logo](images/logo.png)\n", site["docs/README.md"], StringComparison.Ordinal);
        Assert.Contains(
            "## Contents\n\n- [First part](first-part.md)\n  - [First sub](first-part.md#first-sub)\n" +
            "- [Second part](second-part.md)\n  - [Deep detail](second-part.md#deep-detail)\n",
            site["docs/README.md"],
            StringComparison.Ordinal);
        Assert.Contains("## Оглавление\n\n- [Первая часть](first-part.ru.md)\n", site["docs/README.ru.md"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The README gains a contents list of its own headings; building from the README that
    /// already carries one gives the same README back, so the list is never read as a section.
    /// </summary>
    [Fact]
    public void The_readme_gains_a_contents_list_that_a_second_build_leaves_as_it_is()
    {
        var site = Site();
        var readme = site["README.md"];

        Assert.Contains(
            "Intro, under the [licence](LICENSE).\n\n<!-- contents:begin (generated, do not edit) -->\n**Contents**\n\n" +
            "- [First part](#first-part)\n  - [First sub](#first-sub)\n- [Second part](#second-part)\n  - [Deep detail](#deep-detail)\n\n" +
            "[Read it page by page](docs/README.md) — the same text, one section per page.\n<!-- contents:end -->\n\n---\n\n## First part\n",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "**Оглавление**\n\n- [Первая часть](#первая-часть)\n  - [Первый подраздел](#первый-подраздел)\n" +
            "- [Вторая часть](#вторая-часть)\n  - [Подробности](#подробности)\n\n" +
            "[Читать по страницам](docs/README.ru.md) — тот же текст, по разделу на страницу.\n<!-- contents:end -->\n",
            site["README.ru.md"],
            StringComparison.Ordinal);

        var again = DocumentationSite.Build(readme, site["README.ru.md"], path => path == "scripts");

        Assert.Equal(site, again);
    }

    [Theory]
    [InlineData("Two ways to use it", "two-ways-to-use-it")]
    [InlineData("Impact evidence: C# pilot", "impact-evidence-c-pilot")]
    [InlineData("Step 1. How to run anything", "step-1-how-to-run-anything")]
    [InlineData("Поиск по коду — не текстовый поиск", "поиск-по-коду--не-текстовый-поиск")]
    [InlineData("Это работает по умолчанию, и Вы это видите", "это-работает-по-умолчанию-и-вы-это-видите")]
    [InlineData("The `index_status` answer", "the-index_status-answer")]
    public void Anchors_are_the_ones_github_gives(string heading, string anchor) =>
        Assert.Equal(anchor, DocumentationSite.Anchor(heading));

    /// <summary>
    /// What is in docs/ is what the READMEs build. Set LOCALAI_WRITE_DOCUMENTATION_SITE=1 and run
    /// this test to write it; a page left over from a heading that has since changed is named.
    /// </summary>
    [Fact]
    public void The_site_in_docs_is_the_one_the_readmes_build()
    {
        var root = RepositoryRoot();
        var site = DocumentationSite.Build(
            File.ReadAllText(Path.Combine(root, "README.md")),
            File.ReadAllText(Path.Combine(root, "README.ru.md")),
            path => Directory.Exists(Path.Combine(root, path)));

        if (Environment.GetEnvironmentVariable(WriteSwitch) == "1")
        {
            foreach (var (path, text) in site)
            {
                File.WriteAllText(Path.Combine(root, path), text, new System.Text.UTF8Encoding(false));
            }
        }

        var stale = site
            .Where(entry => !File.Exists(Path.Combine(root, entry.Key)) ||
                            Normalised(File.ReadAllText(Path.Combine(root, entry.Key))) != entry.Value)
            .Select(entry => entry.Key)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
                .Where(page => File.ReadAllText(page).Contains(DocumentationSite.GeneratedMark, StringComparison.Ordinal))
                .Select(page => "docs/" + Path.GetFileName(page))
                .Where(page => !site.ContainsKey(page)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"These files are not what the READMEs build; set {WriteSwitch}=1 and run this test again: " +
            string.Join(", ", stale));
    }

    private static string Normalised(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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
