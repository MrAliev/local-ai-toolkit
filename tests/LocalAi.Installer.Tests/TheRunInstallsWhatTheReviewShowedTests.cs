using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A run started while the release was still being checked had a review saying the package would
/// not be installed, and installed it anyway whenever the check finished before the package step
/// (#612). The review now waits for the check, so no run starts from a list it could not describe.
/// </summary>
public sealed class TheRunInstallsWhatTheReviewShowedTests : IDisposable
{
    private readonly TemporaryDirectory scratch = new("LocalAi-run-follows-review");

    public void Dispose() => scratch.Dispose();

    /// <summary>
    /// The check still at the manifest, or at the hash of a folder's package, and a tick given to
    /// the review by other means than its box, which is held.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_cannot_start_while_the_check_is_running(bool fromFolder)
    {
        var feed = fromFolder ? new GatedFolderFeed() : new GatedFeed();
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = scratch.Path,
            RuntimeRoot = scratch.Combine("no-runtime-root"),
            ScratchRoot = scratch.Combine("scratch"),
        };
        wizard.Models.CatalogModels.Clear();
        if (feed is GatedFolderFeed)
        {
            wizard.Package.SourceFolder = scratch.Path;
        }

        var resolving = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        var first = await Task.WhenAny(feed.Check.Reached, resolving)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(first == feed.Check.Reached, "The resolve finished without reaching the check.");
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);

        Assert.True(wizard.Package.IsResolving);
        Assert.False(wizard.CanRun);
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);

        feed.Check.Open();
        await resolving;

        Assert.False(feed.Fetched, "The run fetched a package the review had not shown.");
    }

    /// <summary>Held shut until the test opens it; says when something has arrived at it.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource opened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => arrived.Task;

        public async Task Pass()
        {
            arrived.TrySetResult();
            await opened.Task;
        }

        public void Open() => opened.TrySetResult();
    }

    /// <summary>A feed whose manifest check waits at its gate. It ignores cancellation.</summary>
    private class GatedFeed : IReleaseFeed
    {
        public Gate Check { get; } = new();

        public bool Fetched { get; private set; }

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken cancellationToken) =>
            Task.FromResult(Release.Manifest.ReleaseVersion);

        public virtual async Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            await Check.Pass();
            return Release;
        }

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken cancellationToken)
        {
            Fetched = true;
            throw new IOException("the test stops the run here");
        }
    }

    /// <summary>A folder whose manifest is answered at once and whose package hash waits at the gate.</summary>
    private sealed class GatedFolderFeed : GatedFeed, IReleasePackageCheck
    {
        public override Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(Release);

        public async Task<bool> PackageMatchesAsync(
            ReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            await Check.Pass();
            return true;
        }
    }

    private static readonly ResolvedRelease Release = new(
        new ReleaseManifest(
            schemaVersion: 1,
            releaseVersion: "0.1.51",
            versionDirectory: "0123456789ab",
            modelCatalogVersion: "1",
            protocolVersion: 1,
            buildCompatibilityId: "test",
            packageUri: new Uri("https://example.invalid/0.1.51/localai-package.zip"),
            packageSize: 200L * 1024 * 1024,
            packageSha256: new string('a', 64),
            requiresAuthenticode: false,
            models: []),
        [1],
        [2]);
}
