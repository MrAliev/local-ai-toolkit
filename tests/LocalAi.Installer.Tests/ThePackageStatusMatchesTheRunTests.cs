using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// What the package page and the run's progress line say about the release, against what the
/// run does with it: a folder is copied from rather than downloaded, and only a recognised
/// installation counts as already having the release.
/// </summary>
public sealed class ThePackageStatusMatchesTheRunTests
{
    private const long TwoHundredMegabytes = 200L * 1024 * 1024;

    [Fact]
    public void A_release_from_a_folder_is_copied_rather_than_downloaded()
    {
        var page = new PackagePageViewModel { SourceFolder = @"D:\releases\0.1.51" };

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal("Release 0.1.51 verified, 200 MB to copy from the folder.", page.StatusText);
    }

    [Fact]
    public void A_release_from_github_is_downloaded()
    {
        var page = new PackagePageViewModel();

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal("Release 0.1.51 verified, 200 MB to download.", page.StatusText);
    }

    /// <summary>
    /// A run over the release already installed puts back what differs from it, and stops the
    /// running servers only when it does. "Nothing will change" was a promise the run no longer
    /// makes, and "leave the LocalAi version untouched" one it no longer keeps.
    /// </summary>
    [Fact]
    public void An_installed_release_says_what_a_repair_puts_back()
    {
        var page = new PackagePageViewModel
        {
            InstalledVersionDirectory = "0123456789ab",
            ReleaseVersion = "0.1.51",
            ActionText = "Update",
        };

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "Release 0.1.51 is already installed. Continuing re-runs the other steps and puts " +
            "back any of its files that are missing or differ from the release.",
            page.StatusText);
        Assert.Equal(
            "LocalAi package: 0.1.51 is already installed — files that are missing or differ " +
            "from the release are put back; if any are, LocalAi's running servers are stopped",
            page.ReviewText);

        page.ReleaseVersion = PackagePageViewModel.LatestTag;
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "LocalAi package: 0.1.51 is already installed — unless a newer one is published " +
            "before you press Update, files that are missing or differ from the release are put " +
            "back; if any are, LocalAi's running servers are stopped",
            page.ReviewText);
    }

    [Fact]
    public void An_installed_release_says_what_a_repair_puts_back_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;
        var page = new PackagePageViewModel
        {
            InstalledVersionDirectory = "0123456789ab",
            ReleaseVersion = "0.1.51",
            ActionText = "Обновить",
        };

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "Релиз 0.1.51 уже установлен. Продолжение повторит остальные шаги и вернёт на " +
            "место те его файлы, которых нет или которые отличаются от релиза.",
            page.StatusText);
        Assert.Equal(
            "Пакет LocalAi: 0.1.51 уже установлен — файлы, которых нет или которые отличаются " +
            "от релиза, возвращаются на место; если такие нашлись, запущенные серверы LocalAi " +
            "останавливаются",
            page.ReviewText);

        page.ReleaseVersion = PackagePageViewModel.LatestTag;
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "Пакет LocalAi: 0.1.51 уже установлен — если до нажатия «Обновить» не выйдет более " +
            "новый, файлы, которых нет или которые отличаются от релиза, возвращаются на место; " +
            "если такие нашлись, запущенные серверы LocalAi останавливаются",
            page.ReviewText);
    }

    /// <summary>
    /// What a repair put back, on the finish page and in the log. The package installer reports
    /// it; without these the run that repaired an installation read exactly like one that found
    /// nothing wrong.
    /// </summary>
    [Fact]
    public void The_finish_row_and_the_log_name_what_a_repair_put_back()
    {
        var result = new ReleaseInstallResult(
            LocalAiPackageInstallStatus.AlreadyInstalled,
            "0123456789ab",
            "0123456789ab",
            @"C:\v\0123456789ab",
            null,
            RestoredFiles: ["codesearch.exe", "pdfium.dll"]);
        var outcome = PackageOutcome.FromResult(result);

        Assert.Equal(
            "already installed, version 0123456789ab; put back from the release: " +
            "codesearch.exe, pdfium.dll",
            RunResults.PackageDetail(outcome, installed: true));
        Assert.Equal(
            @"LocalAi package: AlreadyInstalled, version 0123456789ab at C:\v\0123456789ab. " +
            "Put back from the release: codesearch.exe, pdfium.dll.",
            InstallerWizardViewModel.PackageLogLine(result));

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "уже был установлен, версия 0123456789ab; из релиза возвращены: " +
            "codesearch.exe, pdfium.dll",
            RunResults.PackageDetail(outcome, installed: true));
    }

    [Fact]
    public void A_repair_that_put_nothing_back_says_nothing_about_it()
    {
        var result = new ReleaseInstallResult(
            LocalAiPackageInstallStatus.AlreadyInstalled,
            "0123456789ab",
            "0123456789ab",
            @"C:\v\0123456789ab",
            null,
            RestoredFiles: []);

        Assert.Equal(
            "already installed, version 0123456789ab",
            RunResults.PackageDetail(PackageOutcome.FromResult(result), installed: true));
        Assert.Equal(
            @"LocalAi package: AlreadyInstalled, version 0123456789ab at C:\v\0123456789ab.",
            InstallerWizardViewModel.PackageLogLine(result));
    }

    /// <summary>
    /// The package installer refuses an unrecognised installation whatever its version, so its
    /// version is not one this run would leave untouched.
    /// </summary>
    [Fact]
    public void Only_a_recognised_installation_names_its_version()
    {
        Assert.Null(InstallerWizardViewModel.ActiveVersionDirectory(
            new ExistingLocalAiSnapshot(ExistingLocalAiState.Unrecognized, "0123456789ab", @"C:\v", "odd")));
        Assert.Equal(
            "0123456789ab",
            InstallerWizardViewModel.ActiveVersionDirectory(
                new ExistingLocalAiSnapshot(ExistingLocalAiState.Compatible, "0123456789ab", @"C:\v", null)));
    }

    /// <summary>
    /// The rail during a run from a folder said "Downloading" while the package was read from
    /// disk. The feed stops the run once both lines have been shown.
    /// </summary>
    [Fact]
    public async Task A_run_from_a_folder_says_it_is_copying()
    {
        using var logs = new TemporaryDirectory("LocalAi-folder-progress");
        var feed = new FolderFeed(Release("0.1.51"));
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
            RuntimeRoot = logs.Combine("no-runtime-root"),
            ScratchRoot = logs.Combine("scratch"),
        };
        wizard.Models.CatalogModels.Clear();
        wizard.Package.SourceFolder = logs.Path;
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.Package.HasPackage);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);
        var said = new List<string>();
        wizard.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(InstallerWizardViewModel.ProgressText))
            {
                return;
            }

            said.Add(wizard.ProgressText);
            if (wizard.ProgressText.Contains(" of ", StringComparison.Ordinal))
            {
                feed.Reported.TrySetResult();
            }
        };

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Copying the LocalAi package from the folder...", said);
        Assert.Contains("Copying the LocalAi package from the folder: 100 of 200 MB", said);
        Assert.DoesNotContain(said, line => line.StartsWith("Downloading", StringComparison.Ordinal));
    }

    private sealed class FolderFeed(ResolvedRelease release) : IReleaseFeed
    {
        public TaskCompletionSource Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult(release.Manifest.ReleaseVersion);

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(release);

        public async Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct)
        {
            bytesDownloaded?.Report(TwoHundredMegabytes / 2);
            await Reported.Task.WaitAsync(ct);
            throw new IOException("the test stops the run here");
        }
    }

    private static ResolvedRelease Release(string version) =>
        new(
            new ReleaseManifest(
                schemaVersion: 1,
                releaseVersion: version,
                versionDirectory: "0123456789ab",
                modelCatalogVersion: "1",
                protocolVersion: 1,
                buildCompatibilityId: "test",
                packageUri: new Uri($"https://example.invalid/{version}/localai-package.zip"),
                packageSize: TwoHundredMegabytes,
                packageSha256: new string('a', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [2]);
}
