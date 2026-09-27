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

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51", packageChecked: true);

        Assert.Equal("Release 0.1.51 verified, 200 MB to copy from the folder.", page.StatusText);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal("Релиз 0.1.51 проверен, из папки будет скопировано 200 МБ.", page.StatusText);
    }

    /// <summary>
    /// "Verified" on a folder release means its package matched the signed manifest. A release
    /// selected without that check says only what was checked (#612, P2).
    /// </summary>
    [Fact]
    public void A_folder_release_whose_package_was_not_checked_names_only_the_signature()
    {
        var page = new PackagePageViewModel { SourceFolder = @"D:\releases\0.1.51" };

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "Release 0.1.51: signature verified, 200 MB to copy from the folder.",
            page.StatusText);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Релиз 0.1.51: подпись проверена, из папки будет скопировано 200 МБ.",
            page.StatusText);
    }

    /// <summary>
    /// A download cannot be hashed before it is downloaded, so on the page only the manifest's
    /// signature has been checked (#612, P2).
    /// </summary>
    [Fact]
    public void A_release_from_github_is_downloaded()
    {
        var page = new PackagePageViewModel();

        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal("Release 0.1.51: signature verified, 200 MB to download.", page.StatusText);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal("Релиз 0.1.51: подпись проверена, скачать 200 МБ.", page.StatusText);
    }

    [Fact]
    public void While_the_package_in_the_folder_is_checked_the_page_says_so()
    {
        var page = new PackagePageViewModel { SourceFolder = @"D:\releases\0.1.51" };
        page.BeginResolving();

        page.BeginCheckingPackage();

        Assert.Equal("Checking the package in the folder…", page.StatusText);
        Assert.True(page.IsResolving);
        Assert.False(page.HasPackage);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal("Проверяю пакет в папке…", page.StatusText);
    }

    [Fact]
    public void A_package_that_does_not_match_its_manifest_resolves_no_release()
    {
        var page = new PackagePageViewModel { SourceFolder = @"D:\releases\0.1.51" };
        page.BeginResolving();
        page.BeginCheckingPackage();

        page.ReportPackageMismatch();

        Assert.Equal(
            "No release resolved. localai-package.zip in the folder does not match the release " +
            "manifest beside it — it is incomplete, altered or from another release. Copy all " +
            "three files again from one release",
            page.StatusText);
        Assert.Equal(PackageSourceState.Unavailable, page.State);
        Assert.False(page.HasPackage);
        Assert.Null(page.Resolved);
        Assert.False(page.IsResolving);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Релиз не определён. localai-package.zip в папке не соответствует манифесту релиза " +
            "рядом с ним — файл неполный, изменён или из другого релиза. Скопируйте заново все " +
            "три файла одного релиза",
            page.StatusText);
    }

    /// <summary>
    /// A run over the release already installed puts back what differs from it, and stops what
    /// runs from that version only when it does. "Nothing will change" was a promise the run no
    /// longer makes, and "leave the LocalAi version untouched" one it no longer keeps. Activation
    /// stops everything the version owns — its tools, the broker, a sync under way — so "servers"
    /// named too little.
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
            "from the release are put back; if any are, LocalAi's running tools and broker are " +
            "stopped",
            page.ReviewText);

        page.ReleaseVersion = PackagePageViewModel.LatestTag;
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "LocalAi package: 0.1.51 is already installed — unless a newer one is published " +
            "before you press Update, files that are missing or differ from the release are put " +
            "back; if any are, LocalAi's running tools and broker are stopped",
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
            "от релиза, возвращаются на место; если такие нашлись, запущенные инструменты и " +
            "брокер LocalAi останавливаются",
            page.ReviewText);

        page.ReleaseVersion = PackagePageViewModel.LatestTag;
        page.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            "Пакет LocalAi: 0.1.51 уже установлен — если до нажатия «Обновить» не выйдет более " +
            "новый, файлы, которых нет или которые отличаются от релиза, возвращаются на место; " +
            "если такие нашлись, запущенные инструменты и брокер LocalAi останавливаются",
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
    /// A repair whose activation then fails keeps the files it put back: they hold the release's
    /// own content. The result carried them and nothing said so — the row and the log line of a
    /// package that did not install named only the failure.
    /// </summary>
    [Fact]
    public void A_failed_repair_names_the_files_it_put_back_and_kept()
    {
        var result = new ReleaseInstallResult(
            LocalAiPackageInstallStatus.RolledBack,
            "0123456789ab",
            "0123456789ab",
            @"C:\v\0123456789ab",
            "Activation failed and the prior version remained selected.",
            RestoredFiles: ["codesearch.exe", "pdfium.dll"]);
        var outcome = PackageOutcome.FromResult(result);

        Assert.Equal(
            "rolled back — Activation failed and the prior version remained selected; " +
            "put back from the release and kept: codesearch.exe, pdfium.dll",
            RunResults.PackageDetail(outcome, installed: false));
        Assert.Equal(
            "LocalAi package: RolledBack. Activation failed and the prior version remained " +
            "selected. Put back from the release and kept: codesearch.exe, pdfium.dll.",
            InstallerWizardViewModel.PackageLogLine(result));

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "откачено — Activation failed and the prior version remained selected; " +
            "из релиза возвращены и оставлены: codesearch.exe, pdfium.dll",
            RunResults.PackageDetail(outcome, installed: false));
    }

    /// <summary>
    /// The installer also refuses to repair the version already active when its folder holds
    /// something the release does not ship. That version stays installed and selected, so "not
    /// installed" said the opposite of what happened. An inactive version keeps its own words.
    /// </summary>
    [Fact]
    public void A_refused_repair_of_the_active_version_says_it_was_left_as_it_was()
    {
        const string Reason = "The immutable target version already exists with different content.";
        var active = PackageOutcome.FromResult(new ReleaseInstallResult(
            LocalAiPackageInstallStatus.ImmutableConflict,
            "0123456789ab",
            "0123456789ab",
            @"C:\v\0123456789ab",
            Reason));
        var inactive = PackageOutcome.FromResult(new ReleaseInstallResult(
            LocalAiPackageInstallStatus.ImmutableConflict,
            "0123456789ab",
            "ba9876543210",
            @"C:\v\0123456789ab",
            Reason));

        Assert.Equal(
            "left as it was: its version folder holds files or folders that are not part of " +
            "the release, so it was not repaired — " + Reason,
            RunResults.PackageDetail(active, installed: false));
        Assert.Equal(
            "not installed: that version directory already holds different files — " + Reason,
            RunResults.PackageDetail(inactive, installed: false));

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "оставлен как был: в каталоге этой версии есть файлы или папки не из релиза, " +
            "поэтому он не восстановлен — " + Reason,
            RunResults.PackageDetail(active, installed: false));
        Assert.Equal(
            "не установлен: в каталоге этой версии уже лежат другие файлы — " + Reason,
            RunResults.PackageDetail(inactive, installed: false));
    }

    /// <summary>
    /// A package step that installs or repairs deletes the launcher copies beyond the machine's
    /// retention bound, and the review is meant to list everything the run does. A run without a
    /// package reaches no such step and says nothing about it. An update keeps a copy of the
    /// launcher it replaces, so at the bound it deletes the oldest.
    /// </summary>
    [Fact]
    public void The_review_says_which_launcher_copies_the_package_step_deletes()
    {
        using var machine = new TemporaryDirectory("LocalAi-review-backups");
        var layout = Machine(machine, copies: 5, launcher: true);
        new RuntimeRetentionPolicyStore(layout.Root).Write(
            RuntimeRetentionPolicy.Default with { LauncherBackups = 5 });
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair, () => null)
        {
            RuntimeRoot = layout.Root,
        };

        Assert.DoesNotContain("copies of localai-launcher.exe", wizard.ReviewText, StringComparison.Ordinal);

        wizard.Package.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Contains(
            "Earlier copies of localai-launcher.exe: all but the newest 5 are deleted",
            wizard.ReviewGroups[0].Lines);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Contains(
            "Прежние копии localai-launcher.exe: все, кроме 5 самых новых, удаляются",
            wizard.ReviewGroups[0].Lines);
    }

    /// <summary>
    /// A clean installation was told earlier copies are deleted when there were none (run
    /// 20260927-010506-clean-install-folder). The run deletes what exceeds the bound: the copies
    /// already there, and the one it makes of a launcher it replaces, which a repair of the
    /// installed release does not.
    /// </summary>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(2, true, true, false)]
    [InlineData(2, true, false, true)]
    [InlineData(3, true, true, true)]
    public void The_review_names_launcher_copies_only_when_the_run_deletes_some(
        int copies,
        bool launcher,
        bool installed,
        bool deletes)
    {
        using var machine = new TemporaryDirectory("LocalAi-review-backups");
        var layout = Machine(machine, copies, launcher);
        new RuntimeRetentionPolicyStore(layout.Root).Write(
            RuntimeRetentionPolicy.Default with { LauncherBackups = 2 });
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair, () => null)
        {
            RuntimeRoot = layout.Root,
        };
        wizard.Package.InstalledVersionDirectory = installed ? "0123456789ab" : null;

        wizard.Package.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        Assert.Equal(
            deletes,
            wizard.ReviewText!.Contains("copies of localai-launcher.exe", StringComparison.Ordinal));
    }

    private static InstallationLayout Machine(TemporaryDirectory machine, int copies, bool launcher)
    {
        var layout = InstallationLayout.FromLocalAppData(machine.Path);
        for (var index = 0; index < copies; index++)
        {
            Directory.CreateDirectory(Path.Combine(layout.InstallerBackupsRoot, "launcher-" + index));
        }

        if (launcher)
        {
            Directory.CreateDirectory(layout.LauncherDirectory);
            File.WriteAllText(layout.LauncherPath, "installed-launcher");
        }

        return layout;
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

    /// <summary>The log under that rail went on saying "downloading": only the rail was fixed.</summary>
    [Fact]
    public async Task A_run_from_a_folder_logs_that_it_is_copying()
    {
        using var logs = new TemporaryDirectory("LocalAi-folder-log");

        var log = await LogOfARunFrom(logs, logs.Path);

        Assert.Contains(
            $"LocalAi package 0.1.51: copying from {logs.Path} and verifying...",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain("downloading", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_from_github_logs_that_it_is_downloading()
    {
        using var logs = new TemporaryDirectory("LocalAi-download-log");

        var log = await LogOfARunFrom(logs, folder: null);

        Assert.Contains(
            "LocalAi package 0.1.51: downloading and verifying...",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain("copying", log, StringComparison.Ordinal);
    }

    /// <summary>The feed stops the run as soon as the package is asked for.</summary>
    private static async Task<string?> LogOfARunFrom(TemporaryDirectory logs, string? folder)
    {
        var feed = new FolderFeed(Release("0.1.51"));
        feed.Reported.TrySetResult();
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
        wizard.Package.SourceFolder = folder ?? string.Empty;
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard.Finish.Progress;
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
