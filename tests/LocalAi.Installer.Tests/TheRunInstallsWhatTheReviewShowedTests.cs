using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A run started while the release was still being checked had a review saying the package would
/// not be installed, and installed it anyway whenever the check finished before the package step
/// (#612). The run does what the review showed.
/// </summary>
public sealed class TheRunInstallsWhatTheReviewShowedTests : IDisposable
{
    private readonly TemporaryDirectory scratch = new("LocalAi-run-follows-review");

    public void Dispose() => scratch.Dispose();

    /// <summary>The check still at the manifest, or at the hash of a folder's package.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_check_that_finishes_after_the_run_started_installs_nothing(bool fromFolder)
    {
        var feed = fromFolder ? new GatedFolderFeed() : new GatedFeed();
        var started = await RunStartedDuringTheCheck(feed);

        feed.Check.Open();
        await started.Resolving;
        started.Environment.Open();
        await started.Run;

        Assert.False(feed.Fetched, "The run fetched the package the review said it would not install.");
        Assert.Contains(
            "LocalAi package: no verified release was selected, so nothing was installed.",
            started.Wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Equal("no release", started.Wizard.VersionContext);
    }

    /// <summary>The run does not go on hashing a package it is not going to install.</summary>
    [Fact]
    public async Task Starting_the_run_stops_the_hash_of_the_folder_package()
    {
        var feed = new GatedFolderFeed();
        var started = await RunStartedDuringTheCheck(feed);

        Assert.True(feed.CheckToken.IsCancellationRequested);

        feed.Check.Open();
        await started.Resolving;
        started.Environment.Open();
        await started.Run;
    }

    /// <summary>
    /// The review read while the check is running, and the run started from it. The run waits at
    /// its first check of the machine, before the package step, until the test opens it. An update
    /// over a machine with no runtime root, so the run writes nothing outside this test's folder.
    /// </summary>
    private async Task<Started> RunStartedDuringTheCheck(GatedFeed feed)
    {
        var environment = new Gate();
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: async _ =>
            {
                await environment.Pass();
                return HermeticDiagnosis.Supported();
            })
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
        await Reached(feed.Check, resolving, "The resolve finished without reaching the check.");
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);
        Assert.True(wizard.Package.IsResolving);
        Assert.Contains(
            "LocalAi package: not resolved — it will not be installed",
            wizard.ReviewText,
            StringComparison.Ordinal);

        var run = wizard.RunAsync(TestContext.Current.CancellationToken);
        await Reached(environment, run, "The run ended before its check of the machine.");
        return new(wizard, resolving, run, environment);
    }

    private static async Task Reached(Gate gate, Task task, string otherwise)
    {
        var first = await Task.WhenAny(gate.Reached, task)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(first == gate.Reached, otherwise);
    }

    private sealed record Started(
        InstallerWizardViewModel Wizard,
        Task Resolving,
        Task<bool> Run,
        Gate Environment);

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
        public CancellationToken CheckToken { get; private set; }

        public override Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(Release);

        public async Task<bool> PackageMatchesAsync(
            ReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            CheckToken = cancellationToken;
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
