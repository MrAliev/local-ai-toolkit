using System.Net;
using System.Xml.Linq;
using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The package page is the one place that says why Next leads to a run without the package. It
/// repeated the feed's English exception message, and several of those named a cause nobody had
/// checked (run 20260927-173013). The page words the reason by its kind now, in the reader's
/// language; the English message stays the run log's.
/// </summary>
public sealed class ThePackagePageSaysWhyTests : IDisposable
{
    private readonly TemporaryDirectory root = new("LocalAi-package-says-why");

    public void Dispose() => root.Dispose();

    [Theory]
    [InlineData("MissingFiles")]
    [InlineData("NoSuchFolder")]
    [InlineData("FolderAccessDenied")]
    [InlineData("ManifestRejectedInFolder")]
    [InlineData("ManifestRejectedOnGitHub")]
    [InlineData("OtherReleaseInFolder")]
    [InlineData("FileAccessDenied")]
    [InlineData("FileUnreadable")]
    [InlineData("FileTooLarge")]
    [InlineData("GitHubUnreachable")]
    [InlineData("NoPublishedRelease")]
    [InlineData("NoPublishedReleaseNorCli")]
    [InlineData("AssetNotFound")]
    [InlineData("DownloadFailed")]
    [InlineData("Unclassified")]
    public void Each_reason_is_worded_by_its_kind_in_the_language_on_screen(string kind)
    {
        var (refusal, english, russian) = Case(kind);
        var page = new PackagePageViewModel();

        page.ReportUnavailable(new ReleaseResolutionException("The English line for the log.", refusal: refusal));

        Assert.Equal(PackageSourceState.Unavailable, page.State);
        Assert.Equal("No release resolved. " + english, page.StatusText);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal("Релиз не определён. " + russian, page.StatusText);
    }

    /// <summary>
    /// The fallback feed carries what the GitHub CLI said on a refusal of any kind, and the page
    /// showed it after one kind only.
    /// </summary>
    [Theory]
    [InlineData("MissingFiles")]
    [InlineData("NoSuchFolder")]
    [InlineData("FolderAccessDenied")]
    [InlineData("ManifestRejectedInFolder")]
    [InlineData("ManifestRejectedOnGitHub")]
    [InlineData("OtherReleaseInFolder")]
    [InlineData("FileAccessDenied")]
    [InlineData("FileUnreadable")]
    [InlineData("FileTooLarge")]
    [InlineData("GitHubUnreachable")]
    [InlineData("NoPublishedRelease")]
    [InlineData("AssetNotFound")]
    [InlineData("DownloadFailed")]
    [InlineData("Unclassified")]
    public void What_the_github_cli_said_follows_a_reason_of_any_kind_once(string kind)
    {
        var (refusal, english, russian) = Case(kind);
        var page = new PackagePageViewModel();

        page.ReportUnavailable(new ReleaseResolutionException(
            "The English line for the log.",
            refusal: refusal with { GitHubCliDetail = "gh: not logged in." }));

        Assert.Equal(
            "No release resolved. " + english + " The GitHub CLI could not get it either; if the " +
            "repository is private, sign in with gh auth login. Technical details: gh: not " +
            "logged in.",
            page.StatusText);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Релиз не определён. " + russian + " GitHub CLI тоже не смог его получить; если " +
            "репозиторий закрытый, войдите командой gh auth login. Технические сведения: gh: " +
            "not logged in.",
            page.StatusText);
    }

    /// <summary>The chain the wizard builds when the GitHub CLI is installed, up to the page.</summary>
    [Fact]
    public async Task What_the_github_cli_said_reaches_the_page_after_an_unreachable_github()
    {
        var feed = new FallbackReleaseFeed(
            new FailingFeed(new ReleaseResolutionException(
                "Could not reach GitHub to determine the newest release of owner/name: No such " +
                "host is known. Check this computer's internet connection, then try again.",
                refusal: new(ReleaseRefusalKind.GitHubUnreachable))),
            new FailingFeed(new ReleaseResolutionException("error connecting to api.github.com")));
        var failure = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));
        var page = new PackagePageViewModel();

        page.ReportUnavailable(failure);

        Assert.Equal(
            "No release resolved. Could not reach GitHub to find the newest release. Check this " +
            "computer's internet connection, or choose a folder that holds the release files. The " +
            "GitHub CLI could not get it either; if the repository is private, sign in with gh " +
            "auth login. Technical details: error connecting to api.github.com",
            page.StatusText);
    }

    /// <summary>
    /// A 403 is not what a private repository looks like from outside, so answers without a 404
    /// reach the page as technical details instead of as "no published release".
    /// </summary>
    [Fact]
    public async Task Github_answers_without_a_404_are_given_as_technical_details()
    {
        using var client = new HttpClient(new Answering(HttpStatusCode.Forbidden));
        using var feed = new AnonymousReleaseFeed(client);
        var failure = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));
        var page = new PackagePageViewModel();

        page.ReportUnavailable(failure);

        Assert.Equal(
            "No release resolved. The release could not be checked. Technical details: GitHub " +
            "named no newest release of MrAliev/local-ai-toolkit (web page answered 403 " +
            "Forbidden; API answered 403 Forbidden).",
            page.StatusText);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Релиз не определён. Не удалось проверить релиз. Технические сведения: GitHub named " +
            "no newest release of MrAliev/local-ai-toolkit (web page answered 403 Forbidden; API " +
            "answered 403 Forbidden).",
            page.StatusText);
    }

    /// <summary>
    /// A release signed correctly for another installer was resolved; only this installer cannot
    /// take it. It was reported as "failed verification" under "No release resolved", and the
    /// review named the request — "latest" — as the incompatible release.
    /// </summary>
    [Fact]
    public void An_incompatible_release_is_named_as_found_and_not_as_unresolved()
    {
        var page = new PackagePageViewModel();

        page.ReportIncompatible(Incompatible("0.9.0"));

        Assert.Equal(PackageSourceState.Incompatible, page.State);
        Assert.Equal(
            "Release 0.9.0 is signed correctly but is not compatible with this installer. Use " +
            "the installer published with release 0.9.0.",
            page.StatusText);
        Assert.Equal("LocalAi package: 0.9.0 is not compatible — it will not be installed", page.ReviewText);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Релиз 0.9.0 подписан верно, но несовместим с этим установщиком. Используйте " +
            "установщик, опубликованный вместе с релизом 0.9.0.",
            page.StatusText);
        Assert.Equal("Пакет LocalAi: 0.9.0 несовместим — он не будет установлен", page.ReviewText);
    }

    /// <summary>What the page shows is what the run log is given, and nothing about an older question.</summary>
    [Fact]
    public void The_run_log_gets_the_reason_the_page_shows_and_no_older_one()
    {
        var page = new PackagePageViewModel();
        Assert.Null(page.LastCheckFailure);

        page.ReportUnavailable(new ReleaseResolutionException("The folder is gone."));
        Assert.Equal("The folder is gone.", page.LastCheckFailure);

        page.SourceFolder = root.Combine("elsewhere");
        Assert.Null(page.LastCheckFailure);

        page.ReportIncompatible(Incompatible("0.9.0"));
        Assert.Equal("The release is for another installer.", page.LastCheckFailure);

        page.BeginResolving();
        Assert.Null(page.LastCheckFailure);
    }

    [Fact]
    public async Task A_check_that_finds_an_incompatible_release_says_which()
    {
        var wizard = Wizard(new FailingFeed(Incompatible("0.9.0")));

        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PackageSourceState.Incompatible, wizard.Package.State);
        Assert.StartsWith(
            "Release 0.9.0 is signed correctly",
            wizard.Package.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_of_no_known_kind_is_given_as_technical_details()
    {
        var wizard = Wizard(new FailingFeed(new IOException("There is not enough space on the disk.")));

        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "No release resolved. The release could not be checked. Technical details: There " +
            "is not enough space on the disk.",
            wizard.Package.StatusText);
    }

    /// <summary>
    /// From the entry point: the folder the installer was started from, offered with two of the
    /// three files gone, and checked by the folder feed the wizard builds for it.
    /// </summary>
    [Fact]
    public async Task A_start_folder_missing_files_is_offered_and_names_every_missing_one()
    {
        var folder = root.Combine("release");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "localai-package.zip"), "placeholder");
        var wizard = Wizard(feed: null);

        wizard.Package.OfferLocalFolder(InstallerWizardViewModel.StartedFrom(
            Path.Combine(folder, "LocalAi.Installer.exe"),
            root.Combine("unpacked")));
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(folder, wizard.Package.SourceFolder);
        Assert.Equal(
            "No release resolved. Not in the folder: release-manifest.json, release-manifest.sig. " +
            "A release folder needs all three files of one release: release-manifest.json, " +
            "release-manifest.sig, localai-package.zip. To install from GitHub instead, erase the " +
            "folder path above.",
            wizard.Package.StatusText);
    }

    /// <summary>The run log said nothing at all about why no release had been selected.</summary>
    [Fact]
    public async Task A_run_without_a_release_logs_what_the_last_check_said()
    {
        const string Said =
            "GitHub named no newest release of owner/name (web page answered 404 Not Found; API " +
            "answered 404 Not Found). A private repository, or one without published releases, " +
            "looks like this from outside.";
        var log = await LogOfARunWithout(new FailingFeed(new ReleaseResolutionException(
            Said,
            refusal: new(ReleaseRefusalKind.NoPublishedRelease, Repository: "owner/name"))));

        Assert.Contains(
            "LocalAi package: no verified release was selected, so nothing was installed. The " +
            "last check of the release said: " + Said + " Return to the package step and check " +
            "the release before continuing.",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_whose_release_was_never_checked_keeps_the_line_it_had()
    {
        var log = await LogOfARunWithout(
            new FailingFeed(new InvalidOperationException("nothing asks the feed here")),
            resolve: false);

        Assert.Contains(
            "LocalAi package: no verified release was selected, so nothing was installed. " +
            "Return to the package step and check the release before continuing.",
            log,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The only line on the page that says why the run will go ahead without the package was
    /// grey. In the warning colour, at normal weight: the bold warning style is heavy for three
    /// lines of text.
    /// </summary>
    [Fact]
    public void A_refusal_on_the_package_page_is_in_the_warning_colour_at_normal_weight()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var status = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding Package.StatusText}");
        var style = status.Element(presentation + "TextBlock.Style")?.Element(presentation + "Style");

        Assert.NotNull(style);
        Assert.Equal("{StaticResource Muted}", style.Attribute("BasedOn")?.Value);
        var triggers = style.Descendants(presentation + "DataTrigger").ToArray();
        Assert.Equal(
            [
                "{x:Static vm:PackageSourceState.Incompatible}",
                "{x:Static vm:PackageSourceState.Unavailable}",
            ],
            triggers
                .Where(trigger => trigger.Attribute("Binding")?.Value == "{Binding Package.State}")
                .Select(trigger => trigger.Attribute("Value")?.Value)
                .Order(StringComparer.Ordinal));
        Assert.All(triggers, trigger =>
        {
            var setters = trigger.Elements(presentation + "Setter").ToArray();
            Assert.Contains(setters, setter =>
                setter.Attribute("Property")?.Value == "Foreground" &&
                setter.Attribute("Value")?.Value == "{DynamicResource WarnText}");
            Assert.DoesNotContain(setters, setter => setter.Attribute("Property")?.Value == "FontWeight");
        });
    }

    private static (ReleaseRefusal Refusal, string English, string Russian) Case(string kind) => kind switch
    {
        "MissingFiles" => (
            new(ReleaseRefusalKind.MissingFiles, Files: ["release-manifest.json", "localai-package.zip"]),
            "Not in the folder: release-manifest.json, localai-package.zip. A release folder needs " +
            "all three files of one release: release-manifest.json, release-manifest.sig, " +
            "localai-package.zip. To install from GitHub instead, erase the folder path above.",
            "В папке нет: release-manifest.json, localai-package.zip. Папке релиза нужны все три " +
            "файла одного релиза: release-manifest.json, release-manifest.sig, localai-package.zip. " +
            "Чтобы установить с GitHub, сотрите путь к папке выше."),
        "NoSuchFolder" => (
            new(ReleaseRefusalKind.NoSuchFolder),
            "This folder does not exist. Correct the path, or erase it to install from GitHub.",
            "Такой папки нет. Исправьте путь или сотрите его, чтобы установить с GitHub."),
        "FolderAccessDenied" => (
            new(ReleaseRefusalKind.FolderAccessDenied),
            "This account cannot open the folder. Give it read access, or choose another folder.",
            "У этой учётной записи нет доступа к папке. Дайте ей право на чтение или выберите " +
            "другую папку."),
        "ManifestRejectedInFolder" => (
            new(ReleaseRefusalKind.ManifestRejectedInFolder),
            "The release manifest in the folder failed its signature check: it is damaged, " +
            "altered or not signed with the LocalAi release key. Copy the three files again from " +
            "the release.",
            "Манифест релиза в папке не прошёл проверку подписи: он повреждён, изменён или " +
            "подписан не ключом релизов LocalAi. Скопируйте три файла релиза заново."),
        "ManifestRejectedOnGitHub" => (
            new(ReleaseRefusalKind.ManifestRejectedOnGitHub, Release: "0.1.45"),
            "The manifest of release 0.1.45 on GitHub failed its signature check and was not used.",
            "Манифест релиза 0.1.45 на GitHub не прошёл проверку подписи и не использован."),
        "OtherReleaseInFolder" => (
            new(ReleaseRefusalKind.OtherReleaseInFolder, Release: "0.1.44", Requested: "0.1.45"),
            "This folder holds release 0.1.44, not 0.1.45. To install 0.1.44, set the version " +
            "above to latest; to install 0.1.45, choose a folder that holds it.",
            "В этой папке релиз 0.1.44, а не 0.1.45. Чтобы установить 0.1.44, укажите выше версию " +
            "latest; чтобы установить 0.1.45, выберите папку с ним."),
        "FileAccessDenied" => (
            new(ReleaseRefusalKind.FileAccessDenied, File: "release-manifest.sig"),
            "This account cannot read release-manifest.sig in the folder. Give it read access, or " +
            "copy the three files somewhere it can read.",
            "У этой учётной записи нет доступа на чтение к release-manifest.sig в папке. Дайте ей " +
            "право на чтение или скопируйте три файла туда, где оно есть."),
        "FileUnreadable" => (
            new(ReleaseRefusalKind.FileUnreadable, File: "release-manifest.json", Detail: "The file is in use."),
            "release-manifest.json in the folder could not be read. Technical details: The file " +
            "is in use.",
            "Не удалось прочитать release-manifest.json в папке. Технические сведения: The file " +
            "is in use."),
        "FileTooLarge" => (
            new(ReleaseRefusalKind.FileTooLarge, File: "release-manifest.json"),
            "release-manifest.json in the folder is larger than any release publishes, so it is " +
            "not the release's file. Copy it again from the release.",
            "release-manifest.json в папке больше, чем бывает в релизе, — это не файл релиза. " +
            "Скопируйте его из релиза заново."),
        "GitHubUnreachable" => (
            new(ReleaseRefusalKind.GitHubUnreachable),
            "Could not reach GitHub to find the newest release. Check this computer's internet " +
            "connection, or choose a folder that holds the release files.",
            "Не удалось связаться с GitHub, чтобы найти самый новый релиз. Проверьте подключение " +
            "к интернету или выберите папку с файлами релиза."),
        "NoPublishedRelease" => (
            new(ReleaseRefusalKind.NoPublishedRelease, Repository: "MrAliev/local-ai-toolkit"),
            "GitHub shows this computer no published release of MrAliev/local-ai-toolkit. Choose " +
            "a folder that holds the release files.",
            "На GitHub нет опубликованных релизов MrAliev/local-ai-toolkit, доступных этому " +
            "компьютеру. Выберите папку с файлами релиза."),
        "NoPublishedReleaseNorCli" => (
            new(
                ReleaseRefusalKind.NoPublishedRelease,
                Repository: "MrAliev/local-ai-toolkit",
                GitHubCliDetail: "gh: not logged in."),
            "GitHub shows this computer no published release of MrAliev/local-ai-toolkit. Choose " +
            "a folder that holds the release files. The GitHub CLI could not get it either; if " +
            "the repository is private, sign in with gh auth login. Technical details: gh: not " +
            "logged in.",
            "На GitHub нет опубликованных релизов MrAliev/local-ai-toolkit, доступных этому " +
            "компьютеру. Выберите папку с файлами релиза. GitHub CLI тоже не смог его получить; " +
            "если репозиторий закрытый, войдите командой gh auth login. Технические сведения: " +
            "gh: not logged in."),
        "AssetNotFound" => (
            new(ReleaseRefusalKind.AssetNotFound, Release: "9.9.9", File: "release-manifest.json"),
            "GitHub has no release-manifest.json in release 9.9.9, or no such release. Check the " +
            "version above.",
            "На GitHub нет файла release-manifest.json в релизе 9.9.9 или нет такого релиза. " +
            "Проверьте версию выше."),
        "DownloadFailed" => (
            new(
                ReleaseRefusalKind.DownloadFailed,
                Release: "0.1.45",
                File: "release-manifest.json",
                Detail: "The server answered 500."),
            "Could not download release-manifest.json of release 0.1.45 from GitHub. Technical " +
            "details: The server answered 500.",
            "Не удалось скачать release-manifest.json релиза 0.1.45 с GitHub. Технические " +
            "сведения: The server answered 500."),
        _ => (
            new(ReleaseRefusalKind.Unclassified, Detail: "Something else happened."),
            "The release could not be checked. Technical details: Something else happened.",
            "Не удалось проверить релиз. Технические сведения: Something else happened."),
    };

    private static ReleaseResolutionException Incompatible(string release) =>
        new(
            "The release is for another installer.",
            new ReleaseVerificationException("The release is not compatible with this installer.", release),
            new(ReleaseRefusalKind.Incompatible, Release: release));

    private InstallerWizardViewModel Wizard(IReleaseFeed? feed) =>
        new(StartChoice.Install, () => null, feed)
        {
            LogDirectory = root.Combine("logs"),
            ScratchRoot = root.Combine("scratch"),
        };

    /// <summary>
    /// An update over a machine with no runtime root, so the run writes nothing outside this
    /// test's folder and stops at the package step for want of a release.
    /// </summary>
    private async Task<string?> LogOfARunWithout(IReleaseFeed feed, bool resolve = true)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Combine("no-runtime-root"),
            ScratchRoot = root.Combine("scratch"),
        };
        wizard.Models.CatalogModels.Clear();
        if (resolve)
        {
            await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        }

        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard.Finish.Progress;
    }

    private sealed class FailingFeed(Exception failure) : IReleaseFeed
    {
        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromException<string>(failure);

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromException<ResolvedRelease>(failure);

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct) =>
            throw new NotSupportedException("a run without a release never downloads one");
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
