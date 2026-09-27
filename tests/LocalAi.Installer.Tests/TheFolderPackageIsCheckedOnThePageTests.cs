using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The package page called a folder release "verified" once its manifest's signature checked
/// out, and a package in the folder that did not match that manifest surfaced only on the finish
/// page, after the prerequisites had been installed (run 20260927-173013-package-validation,
/// variants 04–08). The page now hashes the package before it says so (#612, P2).
/// </summary>
public sealed class TheFolderPackageIsCheckedOnThePageTests : IDisposable
{
    private const long TwoHundredMegabytes = 200L * 1024 * 1024;

    private readonly TemporaryDirectory scratch = new("LocalAi-folder-package-check");

    public void Dispose() => scratch.Dispose();

    [Fact]
    public async Task The_page_says_verified_only_once_the_package_matches()
    {
        var feed = new CheckedFolderFeed(Release("0.1.51"));
        var wizard = Wizard(feed);

        var resolving = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        var check = await CheckAskedBy(resolving, feed);

        Assert.Equal("Checking the package in the folder…", wizard.Package.StatusText);
        Assert.True(wizard.Package.IsResolving);
        Assert.False(wizard.Package.HasPackage);

        check.Answer.SetResult(true);
        await resolving;

        Assert.Equal(
            "Release 0.1.51 verified, 200 MB to copy from the folder.",
            wizard.Package.StatusText);
        Assert.True(wizard.Package.HasPackage);
        Assert.False(wizard.Package.IsResolving);
    }

    [Fact]
    public async Task A_package_that_does_not_match_resolves_no_release()
    {
        var feed = new CheckedFolderFeed(Release("0.1.51"));
        var wizard = Wizard(feed);

        var resolving = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        (await CheckAskedBy(resolving, feed)).Answer.SetResult(false);
        await resolving;

        Assert.StartsWith(
            "No release resolved. localai-package.zip in the folder does not match",
            wizard.Package.StatusText,
            StringComparison.Ordinal);
        Assert.Equal(PackageSourceState.Unavailable, wizard.Package.State);
        Assert.Null(wizard.Package.Resolved);
        Assert.False(wizard.Package.IsResolving);
        // An update folds the page away; a release that failed its check brings it back, as any
        // failed resolve does.
        Assert.False(wizard.IsReleaseFolded);
    }

    /// <summary>
    /// A hash of the old folder answers nobody: it is cancelled, and an answer it gives anyway is
    /// not put on the page.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Editing_the_question_while_the_package_is_hashed_drops_the_hash(
        bool folderEdited,
        bool honoursCancellation)
    {
        var feed = new CheckedFolderFeed(Release("0.1.51"), honoursCancellation);
        var wizard = Wizard(feed);

        var resolving = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        var check = await CheckAskedBy(resolving, feed);
        if (folderEdited)
        {
            wizard.Package.SourceFolder = scratch.Combine("another-folder");
        }
        else
        {
            wizard.Package.ReleaseVersion = "0.1.52";
        }

        Assert.True(check.Token.IsCancellationRequested);
        check.Answer.TrySetResult(true);
        await resolving;

        Assert.Equal(PackageSourceState.NotChecked, wizard.Package.State);
        Assert.Null(wizard.Package.Resolved);
        Assert.False(wizard.Package.IsResolving);
        Assert.Equal("Press Check again to look up this release.", wizard.Package.StatusText);
    }

    /// <summary>
    /// The older hash finishing last neither answers nor ends the newer check.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_check_while_the_package_is_hashed_drops_the_old_hash(
        bool honoursCancellation)
    {
        var feed = new CheckedFolderFeed(Release("0.1.51"), honoursCancellation);
        var wizard = Wizard(feed);

        var first = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        var firstCheck = await CheckAskedBy(first, feed);
        var second = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        var secondCheck = await CheckAskedBy(second, feed);

        Assert.True(firstCheck.Token.IsCancellationRequested);
        firstCheck.Answer.TrySetResult(false);
        await first;

        Assert.True(wizard.Package.IsResolving);
        Assert.Equal("Checking the package in the folder…", wizard.Package.StatusText);

        secondCheck.Answer.SetResult(true);
        await second;

        Assert.Equal(
            "Release 0.1.51 verified, 200 MB to copy from the folder.",
            wizard.Package.StatusText);
        Assert.True(wizard.Package.HasPackage);
    }

    /// <summary>
    /// The check on the page is a first answer. The run still copies the package and verifies
    /// the copy, which is what is installed.
    /// </summary>
    [Fact]
    public async Task The_run_still_copies_the_package_the_page_checked()
    {
        var feed = new CheckedFolderFeed(Release("0.1.51"));
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
        wizard.Package.SourceFolder = scratch.Path;
        var resolving = wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        (await CheckAskedBy(resolving, feed)).Answer.SetResult(true);
        await resolving;
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(feed.Copied);
    }

    /// <summary>The hash the resolve asked for; a resolve that ends without asking fails here.</summary>
    private static async Task<Check> CheckAskedBy(Task resolving, CheckedFolderFeed feed)
    {
        var check = feed.NextCheck(TestContext.Current.CancellationToken);
        var first = await Task.WhenAny(check, resolving);
        Assert.True(
            first == check,
            "The resolve finished without checking the package in the folder.");
        return await check;
    }

    private InstallerWizardViewModel Wizard(CheckedFolderFeed feed)
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair, () => null, feed)
        {
            ScratchRoot = scratch.Combine("scratch"),
        };
        wizard.Package.SourceFolder = scratch.Path;
        return wizard;
    }

    private sealed record Check(CancellationToken Token, TaskCompletionSource<bool> Answer);

    /// <summary>
    /// A folder feed whose hash is answered by the test. It ignores cancellation unless told to
    /// honour it, so an answer that arrives after it is also covered.
    /// </summary>
    private sealed class CheckedFolderFeed(ResolvedRelease release, bool honoursCancellation = false)
        : IReleaseFeed, IReleasePackageCheck
    {
        private readonly Queue<TaskCompletionSource<Check>> requested = new();
        private readonly Queue<Check> pending = new();
        private readonly Lock gate = new();

        public bool Copied { get; private set; }

        public Task<Check> NextCheck(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (pending.TryDequeue(out var check))
                {
                    return Task.FromResult(check);
                }

                var waiter = new TaskCompletionSource<Check>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                requested.Enqueue(waiter);
                return waiter.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<bool> PackageMatchesAsync(
            ReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            var check = new Check(
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (gate)
            {
                if (requested.TryDequeue(out var waiter))
                {
                    waiter.SetResult(check);
                }
                else
                {
                    pending.Enqueue(check);
                }
            }

            return honoursCancellation
                ? check.Answer.Task.WaitAsync(cancellationToken)
                : check.Answer.Task;
        }

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult(release.Manifest.ReleaseVersion);

        public Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(release);

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct)
        {
            Copied = true;
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
