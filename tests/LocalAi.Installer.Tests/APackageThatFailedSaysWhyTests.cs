using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// #612, run 20260927-173013-package-validation on RC29: a package that failed during the run left
/// its row "not attempted", its journal step Running, and one line in the log, "Failed: Release
/// package verification failed.", for a changed file and a full disk alike. The row now says what
/// happened by what the reader does next, the log keeps the facts behind it, and the step is
/// closed as failed.
/// </summary>
public sealed class APackageThatFailedSaysWhyTests
{
    private const string NotInstalled = " The package was not installed.";
    private const string Folder = @"D:\releases\0.1.51";

    private const string FolderMismatch =
        "not installed: localai-package.zip in the folder does not match the release manifest beside " +
        "it — it is incomplete, altered or from another release. Copy all three files again from one release";

    private const string DownloadMismatch =
        "not installed: the downloaded package does not match the signed release manifest, so it was " +
        "not used. Run the installer again to download it anew";

    public static TheoryData<string, bool, string, string> Failures() => new()
    {
        {
            "size", true, FolderMismatch,
            "LocalAi package: localai-package.zip is 100 bytes; the signed manifest says 209715200." + NotInstalled
        },
        {
            "overflow", true, FolderMismatch,
            "LocalAi package: localai-package.zip is larger than the 209715200 bytes the signed manifest says." +
            NotInstalled
        },
        {
            "hash", true, FolderMismatch,
            $"LocalAi package: localai-package.zip does not match the SHA-256 in the signed manifest (manifest " +
            $"{new string('A', 64)}, file {new string('B', 64)})." + NotInstalled
        },
        {
            "hash", false, DownloadMismatch,
            $"LocalAi package: localai-package.zip does not match the SHA-256 in the signed manifest (manifest " +
            $"{new string('A', 64)}, file {new string('B', 64)})." + NotInstalled
        },
        {
            "size", false, DownloadMismatch,
            "LocalAi package: localai-package.zip is 100 bytes; the signed manifest says 209715200." + NotInstalled
        },
        {
            "contents", false,
            "not installed: the package matches its release manifest, but its contents failed the installer's safety checks",
            "LocalAi package: the package matches the signed manifest, but its contents failed the installer's " +
            "checks." + NotInstalled
        },
        {
            "missing", true,
            "not installed: localai-package.zip is not in the folder",
            $"LocalAi package: '{Folder}' does not hold localai-package.zip." + NotInstalled
        },
        {
            "no-access", true,
            "not installed: this account cannot read localai-package.zip in the folder",
            $"LocalAi package: localai-package.zip in '{Folder}' could not be read: Access to the path is denied." +
            NotInstalled
        },
        {
            "unreadable", true,
            "not installed: localai-package.zip in the folder could not be read. Technical details: The file is in use.",
            $"LocalAi package: localai-package.zip in '{Folder}' could not be read: The file is in use." + NotInstalled
        },
        {
            "too-large", true,
            "not installed: localai-package.zip in the folder is larger than any release publishes",
            $"LocalAi package: localai-package.zip in '{Folder}' is 5 bytes, which is larger than this installer " +
            "will read (4 bytes)." + NotInstalled
        },
        {
            "download", false,
            "not installed: the package could not be downloaded from GitHub. Technical details: Could not download " +
            "'localai-package.zip' from release '0.1.51': the server answered 503 Service Unavailable.",
            "LocalAi package: Could not download 'localai-package.zip' from release '0.1.51': the server answered " +
            "503 Service Unavailable." + NotInstalled
        },
        {
            "not-written", false,
            "not installed: the package could not be written to the installer's working folder. Technical details: " +
            "There is not enough space on the disk.",
            @"LocalAi package: could not write the package to C:\scratch\.package.zip: There is not enough space " +
            "on the disk." + NotInstalled
        },
        {
            "copy-not-written", true,
            "not installed: the package could not be written to the installer's working folder. Technical details: " +
            "There is not enough space on the disk.",
            @"LocalAi package: could not write the package to C:\scratch\localai-package.zip: There is not enough " +
            "space on the disk." + NotInstalled
        },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_row_the_log_and_the_journal_say_why_the_package_was_not_installed(
        string failure,
        bool fromFolder,
        string row,
        string line)
    {
        using var logs = new TemporaryDirectory("LocalAi-package-failed");

        var wizard = await RunEndedBy(logs, Failure(failure), fromFolder);

        var package = wizard.Finish.Results[0];
        Assert.Equal(PageLabels.ResultPackage, package.Title);
        Assert.Equal(RunMark.Failed, package.Mark);
        Assert.Equal(row, package.Detail);
        var log = Lines(wizard);
        Assert.Contains(line, log);
        // The run goes on as it does without a release, rather than ending on the generic line.
        Assert.DoesNotContain(log, entry => entry.StartsWith("Failed: ", StringComparison.Ordinal));
        var step = PackageStep(logs);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
        Assert.Equal(line, step.Detail);
    }

    /// <summary>Kept as facts, so the row is worded again when the language changes.</summary>
    [Fact]
    public async Task The_row_follows_the_language()
    {
        using var logs = new TemporaryDirectory("LocalAi-package-failed");
        var wizard = await RunEndedBy(logs, Failure("unreadable"), fromFolder: true);

        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(
            "не установлен: не удалось прочитать localai-package.zip в папке. Технические сведения: The file is in use.",
            wizard.Finish.Results[0].Detail);
    }

    [Fact]
    public void Every_cause_has_its_words_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal(
            [
                "не установлен: localai-package.zip в папке не соответствует манифесту релиза рядом с ним — файл " +
                "неполный, изменён или из другого релиза. Скопируйте заново все три файла одного релиза",
                "не установлен: скачанный пакет не соответствует подписанному манифесту релиза, поэтому не " +
                "использован. Запустите установщик снова, чтобы скачать его заново",
                "не установлен: пакет соответствует манифесту релиза, но его содержимое не прошло проверку " +
                "безопасности установщика",
                "не установлен: в папке нет localai-package.zip",
                "не установлен: у этой учётной записи нет доступа на чтение к localai-package.zip в папке",
                "не установлен: не удалось прочитать localai-package.zip в папке. Технические сведения: why",
                "не установлен: localai-package.zip в папке больше, чем бывает в релизе",
                "не установлен: не удалось скачать пакет с GitHub. Технические сведения: why",
                "не установлен: не удалось записать пакет в рабочую папку установщика. Технические сведения: why",
            ],
            Enum.GetValues<PackageFailureKind>().Select(kind => RunResults.PackageDetail(
                new PackageOutcome(PackageOutcomeKind.Failed, Reason: "why", Failure: kind),
                installed: false)));
    }

    /// <summary>
    /// A failure the run cannot name keeps the row that says it stopped with an error, and the
    /// handler that ends the run — but the row is no longer "not attempted", and the step is closed.
    /// </summary>
    [Fact]
    public async Task Any_other_failure_says_it_stopped_with_an_error()
    {
        using var logs = new TemporaryDirectory("LocalAi-package-failed");

        var wizard = await RunEndedBy(logs, new InvalidOperationException("the feed broke"), fromFolder: false);

        var package = wizard.Finish.Results[0];
        Assert.Equal(RunMark.Failed, package.Mark);
        Assert.Equal(
            "not installed: the installation stopped with an error. Technical details: the feed broke",
            package.Detail);
        Assert.Contains("Failed: the feed broke", Lines(wizard));
        var step = PackageStep(logs);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
        Assert.Equal("the feed broke", step.Detail);
    }

    private static Exception Failure(string failure) => failure switch
    {
        "size" => new PackageVerificationException(PackageVerificationProblem.SizeMismatch)
        {
            ExpectedSize = 209715200,
            ActualSize = 100,
        },
        "overflow" => new PackageVerificationException(PackageVerificationProblem.LargerThanManifest)
        {
            ExpectedSize = 209715200,
        },
        "hash" => new PackageVerificationException(PackageVerificationProblem.HashMismatch)
        {
            ExpectedSha256 = new string('A', 64),
            ActualSha256 = new string('B', 64),
        },
        "contents" => new PackageVerificationException(PackageVerificationProblem.ContentsRejected),
        "missing" => new ReleaseResolutionException(
            $"'{Folder}' does not hold localai-package.zip.",
            refusal: new(ReleaseRefusalKind.MissingFiles, Files: ["localai-package.zip"])),
        "no-access" => new ReleaseResolutionException(
            $"localai-package.zip in '{Folder}' could not be read: Access to the path is denied.",
            new UnauthorizedAccessException("Access to the path is denied."),
            new(ReleaseRefusalKind.FileAccessDenied, File: "localai-package.zip")),
        "unreadable" => new ReleaseResolutionException(
            $"localai-package.zip in '{Folder}' could not be read: The file is in use.",
            new IOException("The file is in use."),
            new(ReleaseRefusalKind.FileUnreadable, File: "localai-package.zip", Detail: "The file is in use.")),
        "too-large" => new ReleaseResolutionException(
            $"localai-package.zip in '{Folder}' is 5 bytes, which is larger than this installer will read (4 bytes).",
            refusal: new(ReleaseRefusalKind.FileTooLarge, File: "localai-package.zip")),
        "download" => new ReleaseResolutionException(
            "Could not download 'localai-package.zip' from release '0.1.51': the server answered 503 Service " +
            "Unavailable.",
            refusal: new(
                ReleaseRefusalKind.DownloadFailed,
                Release: "0.1.51",
                File: "localai-package.zip",
                Detail: "Could not download 'localai-package.zip' from release '0.1.51': the server answered 503 " +
                    "Service Unavailable.")),
        "not-written" => new PackageVerificationException(PackageVerificationProblem.NotWritten)
        {
            Path = @"C:\scratch\.package.zip",
            Reason = "There is not enough space on the disk.",
        },
        "copy-not-written" => new WorkingCopyWriteException(
            @"C:\scratch\localai-package.zip",
            new IOException("There is not enough space on the disk.")),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    /// <summary>A real run with its real journal, whose feed fails as the package is asked for.</summary>
    private static async Task<InstallerWizardViewModel> RunEndedBy(
        TemporaryDirectory logs,
        Exception failure,
        bool fromFolder)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            new FailingFeed(failure),
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
            RuntimeRoot = logs.Combine("no-runtime-root"),
            ScratchRoot = logs.Combine("scratch"),
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        wizard.Package.SourceFolder = fromFolder ? logs.Path : string.Empty;
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.Package.HasPackage);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard;
    }

    private static string[] Lines(InstallerWizardViewModel wizard) =>
        wizard.Finish.Progress!.Split(Environment.NewLine);

    private static InstallerRunStep PackageStep(TemporaryDirectory logs)
    {
        var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(logs.Path, "journal-*.json")));
        return Assert.Single(journal.Snapshot.Steps, step => step.Kind == InstallerRunEffectKind.PackageActivation);
    }

    private sealed class FailingFeed(Exception failure) : IReleaseFeed
    {
        private static readonly ResolvedRelease Release = new(
            new ReleaseManifest(
                schemaVersion: 1,
                releaseVersion: "0.1.51",
                versionDirectory: "0123456789ab",
                modelCatalogVersion: "1",
                protocolVersion: 1,
                buildCompatibilityId: "test",
                packageUri: new Uri("https://example.invalid/0.1.51/localai-package.zip"),
                packageSize: 209715200,
                packageSha256: new string('A', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [2]);

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult(Release.Manifest.ReleaseVersion);

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(Release);

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct) =>
            Task.FromException<string>(failure);
    }
}
