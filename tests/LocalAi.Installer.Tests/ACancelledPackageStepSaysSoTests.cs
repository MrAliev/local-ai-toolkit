using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// #613, run 20260927-233709-update-from-rc19: an update cancelled while its package was being
/// copied ended with the package row "not attempted", and the log went from "copying … and
/// verifying..." straight to "Cancelled." without saying what became of the package. The step had
/// begun, so the row says the run was cancelled, and names the release still installed as the
/// machine reads after the cancellation.
/// </summary>
public sealed class ACancelledPackageStepSaysSoTests : IDisposable
{
    private const string Cancelled = "Cancelled. Actions already applied were left in place.";

    private readonly TemporaryDirectory root = new("LocalAi-package-cancelled");

    public void Dispose() => root.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_on_a_machine_without_LocalAi_says_the_run_was_cancelled(bool fromFolder)
    {
        var wizard = await RunCancelledAtTheDownload(fromFolder, afterCancel: () => { });

        var package = wizard.Finish.Results[0];
        Assert.Equal(PageLabels.ResultPackage, package.Title);
        Assert.Equal(RunMark.Failed, package.Mark);
        Assert.Equal("not installed: the run was cancelled", package.Detail);
        AssertFollows(
            wizard,
            fromFolder
                ? $"LocalAi package 0.1.51: copying from {root.Path} and verifying..."
                : "LocalAi package 0.1.51: downloading and verifying...",
            "LocalAi package 0.1.51: cancelled before activation; not installed.");

        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("не установлен: запуск отменён", wizard.Finish.Results[0].Detail);
    }

    /// <summary>
    /// Written only once the feed has seen the cancellation: the release is read from what the
    /// cancellation left, never recalled from before the run.
    /// </summary>
    [Fact]
    public async Task Cancelled_over_an_installation_names_the_release_still_installed()
    {
        var wizard = await RunCancelledAtTheDownload(
            fromFolder: true,
            afterCancel: () => Install(new InstalledReleaseStore(Bin()).Write, "0.2.1-RC19"));

        var package = wizard.Finish.Results[0];
        Assert.Equal(RunMark.Failed, package.Mark);
        Assert.Equal("not installed: the run was cancelled; 0.2.1-RC19 is still installed", package.Detail);
        AssertFollows(
            wizard,
            $"LocalAi package 0.1.51: copying from {root.Path} and verifying...",
            "LocalAi package 0.1.51: cancelled before activation; not installed. 0.2.1-RC19 is still the active version.");

        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("не установлен: запуск отменён; по-прежнему установлен 0.2.1-RC19", wizard.Finish.Results[0].Detail);
    }

    [Fact]
    public async Task A_release_record_that_cannot_be_read_names_no_release()
    {
        var wizard = await RunCancelledAtTheDownload(
            fromFolder: false,
            afterCancel: () => Install(
                (_, _) => File.WriteAllText(Path.Combine(Bin(), InstalledRelease.FileName), "{ not json"),
                "0.2.1-RC19"));

        Assert.Equal("not installed: the run was cancelled", wizard.Finish.Results[0].Detail);
        AssertFollows(
            wizard,
            "LocalAi package 0.1.51: downloading and verifying...",
            "LocalAi package 0.1.51: cancelled before activation; not installed.");
    }

    /// <summary>Stopped before the package step began, the row still says it was not attempted.</summary>
    [Fact]
    public async Task A_run_cancelled_before_the_package_step_leaves_it_not_attempted()
    {
        InstallerWizardViewModel? wizard = null;
        var feed = new GatedFeed(() => { });
        wizard = Wizard(feed, token =>
        {
            wizard!.Cancel();
            return Task.FromCanceled<EnvironmentDiagnosis>(token);
        });
        await Prepare(wizard, fromFolder: false);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.IsCanceled);
        Assert.False(feed.Reached.IsCompleted);
        var package = wizard.Finish.Results[0];
        Assert.Equal(RunMark.None, package.Mark);
        Assert.Equal(PageLabels.ResultNotAttempted, package.Detail);
        Assert.DoesNotContain(Lines(wizard), line => line.Contains("cancelled before activation", StringComparison.Ordinal));
    }

    /// <summary>A real run with its real journal, cancelled through its token while the feed holds the package.</summary>
    private async Task<InstallerWizardViewModel> RunCancelledAtTheDownload(bool fromFolder, Action afterCancel)
    {
        var feed = new GatedFeed(afterCancel);
        var wizard = Wizard(feed, _ => Task.FromResult(HermeticDiagnosis.Supported()));
        await Prepare(wizard, fromFolder);

        var run = wizard.RunAsync(TestContext.Current.CancellationToken);
        await feed.Reached.WaitAsync(TestContext.Current.CancellationToken);
        wizard.Cancel();

        Assert.False(await run);
        Assert.True(wizard.IsCanceled);
        return wizard;
    }

    private InstallerWizardViewModel Wizard(
        IReleaseFeed feed,
        Func<CancellationToken, Task<EnvironmentDiagnosis>> detectEnvironment)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            feed,
            detectEnvironment: detectEnvironment)
        {
            EnableDependencyActions = true,
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Combine("runtime"),
            ScratchRoot = root.Combine("scratch"),
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        return wizard;
    }

    private async Task Prepare(InstallerWizardViewModel wizard, bool fromFolder)
    {
        wizard.Package.SourceFolder = fromFolder ? root.Path : string.Empty;
        await wizard.ResolvePackageAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.Package.HasPackage);
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);
    }

    private string Bin() => root.Combine("runtime", "bin");

    /// <summary>An active version directory, and its release record as <paramref name="record"/> writes it.</summary>
    private void Install(Action<string, string> record, string release)
    {
        const string directory = "17385b2a6070";
        Directory.CreateDirectory(Bin());
        File.WriteAllText(Path.Combine(Bin(), "current.json"), $$"""{"version":"{{directory}}"}""");
        record(directory, release);
    }

    private static void AssertFollows(InstallerWizardViewModel wizard, string started, string outcome)
    {
        var log = Lines(wizard);
        var at = Array.IndexOf(log, started);
        Assert.True(at >= 0, "The log does not say the package step started:\n" + string.Join("\n", log));
        Assert.Equal([started, outcome, Cancelled], log.Skip(at).Take(3));
    }

    private static string[] Lines(InstallerWizardViewModel wizard) =>
        wizard.Finish.Progress!.Split(Environment.NewLine);

    /// <summary>Holds the package until the run is cancelled, then does what the test says happened meanwhile.</summary>
    private sealed class GatedFeed(Action afterCancel) : IReleaseFeed
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

        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => reached.Task;

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult(Release.Manifest.ReleaseVersion);

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(Release);

        public async Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct)
        {
            reached.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                afterCancel();
                throw;
            }

            throw new InvalidOperationException("Only a cancelled run gets past this feed.");
        }
    }
}
