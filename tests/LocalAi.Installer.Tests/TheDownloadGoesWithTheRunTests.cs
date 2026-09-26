using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The package is downloaded and unpacked into a directory of this run's own, carrying the
/// marker the next start looks for, and the run takes it with it when it ends. A shared
/// directory left every interrupted run's 800 MB in %TEMP% for good.
/// </summary>
public sealed class TheDownloadGoesWithTheRunTests
{
    [Fact]
    public async Task The_package_is_downloaded_into_a_directory_of_the_runs_own()
    {
        using var temp = new TemporaryDirectory("LocalAi-run-scratch");
        var scratchRoot = temp.Combine("scratch");
        var feed = new StoppingFeed(temp.Path);
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()))
        {
            EnableDependencyActions = true,
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = temp.Combine("no-runtime-root"),
            ScratchRoot = scratchRoot,
        };
        wizard.Models.CatalogModels.Clear();
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.Package.HasPackage);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.NotNull(feed.DownloadedInto);
        Assert.Equal(scratchRoot, Path.GetDirectoryName(feed.DownloadedInto));
        Assert.True(feed.MarkerWasThere);
        Assert.False(Directory.Exists(feed.DownloadedInto));
    }

    /// <summary>Writes a package only inside the test's own directory, never the machine's.</summary>
    private sealed class StoppingFeed(string testRoot) : IReleaseFeed
    {
        public string? DownloadedInto { get; private set; }

        public bool MarkerWasThere { get; private set; }

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult("0.1.51");

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(new ResolvedRelease(
                new ReleaseManifest(
                    schemaVersion: 1,
                    releaseVersion: "0.1.51",
                    versionDirectory: "0123456789ab",
                    modelCatalogVersion: "1",
                    protocolVersion: 1,
                    buildCompatibilityId: "test",
                    packageUri: new Uri("https://example.invalid/0.1.51/localai-package.zip"),
                    packageSize: 1024,
                    packageSha256: new string('a', 64),
                    requiresAuthenticode: false,
                    models: []),
                [1],
                [2]));

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct)
        {
            DownloadedInto = workingDirectory;
            MarkerWasThere = File.Exists(
                Path.Combine(workingDirectory, InstallerScratchDirectory.MarkerFileName));
            if (workingDirectory.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(Path.Combine(workingDirectory, "localai-package.zip"), "package");
            }

            throw new IOException("the test stops the run here");
        }
    }
}
