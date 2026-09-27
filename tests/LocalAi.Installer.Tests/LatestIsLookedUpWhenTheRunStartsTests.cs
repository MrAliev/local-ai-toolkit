using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The review promises "whatever is newest when you press" the button, and the run looked the
/// newest release up only after the prerequisites were installed (#612). It is looked up when the
/// run starts, so the rail names the release being installed from then on.
/// </summary>
public sealed class LatestIsLookedUpWhenTheRunStartsTests : IDisposable
{
    private readonly TemporaryDirectory scratch = new("LocalAi-latest-at-start");

    public void Dispose() => scratch.Dispose();

    [Fact]
    public async Task A_release_published_while_the_wizard_waited_is_taken_before_the_prerequisites()
    {
        var feed = new PublishingFeed();
        var wizard = await Confirmed(feed);
        feed.Newest = "0.1.52";

        var seen = await AtTheFirstCheckOfTheMachine(wizard);

        Assert.Equal("0.1.52", seen.Rail);
        Assert.Contains(
            "LocalAi package: 0.1.52 was published after this release was checked; resolving it instead.",
            seen.Log,
            StringComparison.Ordinal);
        Assert.Equal("0.1.52", feed.Fetched);
    }

    /// <summary>A lookup the feed refuses keeps the release the review named.</summary>
    [Fact]
    public async Task A_lookup_that_fails_at_the_start_keeps_the_checked_release()
    {
        var feed = new PublishingFeed();
        var wizard = await Confirmed(feed);
        feed.Unreachable = true;

        var seen = await AtTheFirstCheckOfTheMachine(wizard);

        Assert.Equal("0.1.51", seen.Rail);
        Assert.Contains(
            "LocalAi package: the newest release could not be re-checked (the feed could not be " +
            "reached); continuing with 0.1.51.",
            seen.Log,
            StringComparison.Ordinal);
        Assert.Equal("0.1.51", feed.Fetched);
    }

    /// <summary>
    /// From a folder another answer means its files were replaced after the page checked them; the
    /// run says so before anything else happens and copies what the folder holds now.
    /// </summary>
    [Fact]
    public async Task A_folder_replaced_after_the_page_check_is_named_so_at_the_start()
    {
        var feed = new ReplacedFolderFeed();
        var wizard = await Confirmed(feed, fromFolder: true);
        feed.Holds = "0.1.52";

        var seen = await AtTheFirstCheckOfTheMachine(wizard);

        Assert.Equal("0.1.52", seen.Rail);
        Assert.Contains(
            "LocalAi package: the folder now holds 0.1.52, not the 0.1.51 checked on the package " +
            "page; resolving it instead.",
            seen.Log,
            StringComparison.Ordinal);
        Assert.Equal("0.1.52", feed.Fetched);
    }

    /// <summary>Cancel pressed while the newest release is looked up ends the run as cancelled.</summary>
    [Fact]
    public async Task Cancelling_the_lookup_ends_the_run_as_cancelled()
    {
        var feed = new PublishingFeed();
        var wizard = await Confirmed(feed, gated: false);
        feed.Lookup = new Gate();

        var run = wizard.RunAsync(TestContext.Current.CancellationToken);
        await Reached(feed.Lookup, run, "The run ended before it looked the newest release up.");
        wizard.Cancel();

        Assert.False(await run);
        Assert.True(wizard.IsCanceled);
        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
        Assert.Contains(
            "Cancelled. Actions already applied were left in place.",
            wizard.Finish.Progress,
            StringComparison.Ordinal);
        Assert.Null(feed.Fetched);
    }

    /// <summary>What the rail and the log said when the run first checked the machine.</summary>
    private sealed record Seen(string Rail, string Log);

    private Gate? environment;

    private async Task<Seen> AtTheFirstCheckOfTheMachine(InstallerWizardViewModel wizard)
    {
        var gate = environment!;
        var run = wizard.RunAsync(TestContext.Current.CancellationToken);
        await Reached(gate, run, "The run ended before its check of the machine.");
        var seen = new Seen(wizard.VersionContext, wizard.Finish.Progress ?? string.Empty);
        gate.Open();
        Assert.False(await run);
        return seen;
    }

    /// <summary>
    /// An update over a machine with no runtime root, standing on the review with release 0.1.51
    /// checked and confirmed. When <paramref name="gated"/>, the run's first check of the machine
    /// waits at a gate; the run stops at the download.
    /// </summary>
    private async Task<InstallerWizardViewModel> Confirmed(
        IReleaseFeed feed,
        bool fromFolder = false,
        bool gated = true)
    {
        var gate = environment = gated ? new Gate() : null;
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: async _ =>
            {
                if (gate is not null)
                {
                    await gate.Pass();
                }

                return HermeticDiagnosis.Supported();
            })
        {
            EnableDependencyActions = true,
            LogDirectory = scratch.Path,
            RuntimeRoot = scratch.Combine("no-runtime-root"),
            ScratchRoot = scratch.Combine("scratch"),
        };
        wizard.Models.CatalogModels.Clear();
        if (fromFolder)
        {
            wizard.Package.SourceFolder = scratch.Path;
        }

        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.1.51", wizard.Package.ResolvedTag);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);
        return wizard;
    }

    private static async Task Reached(Gate gate, Task task, string otherwise)
    {
        var first = await Task.WhenAny(gate.Reached, task)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(first == gate.Reached, otherwise);
    }

    /// <summary>Held shut until the test opens it; says when something has arrived at it.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource opened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => arrived.Task;

        public async Task Pass(CancellationToken cancellationToken = default)
        {
            arrived.TrySetResult();
            await opened.Task.WaitAsync(cancellationToken);
        }

        public void Open() => opened.TrySetResult();
    }

    /// <summary>GitHub, as far as the wizard can tell: "latest" is whatever the test last published.</summary>
    private sealed class PublishingFeed : IReleaseFeed
    {
        public string Newest { get; set; } = "0.1.51";

        public bool Unreachable { get; set; }

        /// <summary>When set, the lookup of "latest" waits here, honouring cancellation.</summary>
        public Gate? Lookup { get; set; }

        /// <summary>The tag the run asked to download, or null if it never did.</summary>
        public string? Fetched { get; private set; }

        public async Task<string> ResolveTagAsync(string requestedTag, CancellationToken cancellationToken)
        {
            if (Lookup is { } lookup)
            {
                await lookup.Pass(cancellationToken);
            }

            return Unreachable
                ? throw new ReleaseResolutionException("the feed could not be reached")
                : Newest;
        }

        public Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(Release(tag));

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken cancellationToken)
        {
            Fetched = tag;
            throw new IOException("the test stops the run here");
        }
    }

    /// <summary>A folder whose package matches its manifest, and whose files the test can replace.</summary>
    private sealed class ReplacedFolderFeed : IReleaseFeed, IReleasePackageCheck
    {
        public string Holds { get; set; } = "0.1.51";

        public string? Fetched { get; private set; }

        public Task<bool> PackageMatchesAsync(ReleaseManifest manifest, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken cancellationToken) =>
            Task.FromResult(Holds);

        public Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(Release(Holds));

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken cancellationToken)
        {
            Fetched = tag;
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
                packageSize: 200L * 1024 * 1024,
                packageSha256: new string('a', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [2]);
}
