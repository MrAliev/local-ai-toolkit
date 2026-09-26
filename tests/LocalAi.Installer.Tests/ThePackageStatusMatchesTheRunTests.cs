using LocalAi.Contracts;
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
