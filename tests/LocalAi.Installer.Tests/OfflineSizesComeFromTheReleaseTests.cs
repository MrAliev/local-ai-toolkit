using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Offline the registry sizes nothing, and the models page said every size was unknown while the
/// verified manifest on the package page carried them all — the very sizes setup weighs.
/// </summary>
public sealed class OfflineSizesComeFromTheReleaseTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public async Task Without_the_registry_the_verified_release_sizes_the_models()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var tags = CatalogueTags();
        var wizard = Wizard(temp, Release(tags), registrySize: null);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(wizard.Package.HasPackage);
        Assert.Equal(
            $"Whole model in video memory — {tags.Length - 1} of {tags.Length} catalogue models fit",
            wizard.Models.RuleSummary);
        Assert.Contains($"{tags[0]} — 2.0 GB, context 8192", wizard.Models.AutomaticSelection);
        Assert.DoesNotContain(
            wizard.Models.AutomaticSelection,
            line => line.StartsWith(tags[1] + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Setup plans only the release's models, so a catalogue model the release does not list is
    /// never set up; "size unknown; set up only if it fits" said it might be.
    /// </summary>
    [Fact]
    public async Task A_catalogue_model_the_release_leaves_out_is_said_not_to_be_set_up()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var tags = CatalogueTags();
        var wizard = Wizard(temp, Release(tags[..^1]), registrySize: null);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"{tags[^1]} — not in this release, so not set up", wizard.Models.AutomaticSelection);
        Assert.Equal(
            $"Whole model in video memory — {tags.Length - 2} of {tags.Length} catalogue models fit; " +
            "not in this release: 1",
            wizard.Models.RuleSummary);

        InstallerCulture.Current = InstallerLanguage.Russian;
        wizard.Models.Relabel();
        Assert.Contains($"{tags[^1]} — нет в этом релизе, поэтому не ставится", wizard.Models.AutomaticSelection);
        Assert.Equal(
            $"Вся модель в видеопамяти — подходящих моделей: {tags.Length - 2} из {tags.Length}; " +
            "нет в этом релизе: 1",
            wizard.Models.RuleSummary);
    }

    /// <summary>Only the release's own gap is worded by the release.</summary>
    [Fact]
    public async Task A_model_the_registry_did_not_size_still_reads_as_unknown()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var tags = CatalogueTags();
        var wizard = Wizard(temp, Release(tags), registrySize: Gigabyte, unsizedTag: tags[^1]);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"{tags[^1]} — size unknown; set up only if it fits", wizard.Models.AutomaticSelection);
        Assert.EndsWith("; size unknown: 1", wizard.Models.RuleSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_no_longer_selected_no_longer_sizes_them()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var tags = CatalogueTags();
        var wizard = Wizard(temp, Release(tags), registrySize: null);
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"{tags[0]} — 2.0 GB, context 8192", wizard.Models.AutomaticSelection);

        wizard.Package.ReleaseVersion = "0.1.52";

        Assert.Equal(
            "Whole model in video memory — model sizes are unknown, so nothing was weighed",
            wizard.Models.RuleSummary);
    }

    [Fact]
    public async Task A_release_listing_none_of_the_models_leaves_them_unsized()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var wizard = Wizard(temp, Release([]), registrySize: null);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(wizard.Package.HasPackage);
        Assert.Equal(
            "Whole model in video memory — model sizes are unknown, so nothing was weighed",
            wizard.Models.RuleSummary);
    }

    [Fact]
    public async Task Sizes_the_registry_gives_are_the_ones_shown()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var tags = CatalogueTags();
        var wizard = Wizard(temp, Release(tags), registrySize: Gigabyte);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            wizard.Models.AutomaticSelection,
            line => line.StartsWith($"{tags[0]} — 1.0 GB, context ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The weighing refuses two adapters with one id. It runs as the release is selected, so a
    /// refusal there turned a verified release into "No release resolved".
    /// </summary>
    [Fact]
    public async Task A_snapshot_the_weighing_refuses_does_not_cost_the_release()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var adapter = new GpuAdapterSnapshot("adapter-1", "Test Adapter", 16UL * 1024 * 1024 * 1024, false);
        var wizard = Wizard(
            temp,
            Release(CatalogueTags()),
            registrySize: null,
            new GpuSnapshot(ObservationState.Available, [adapter, adapter], null));

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(wizard.Package.HasPackage);
        Assert.Equal(
            "Whole model in video memory — model sizes are unknown, so nothing was weighed",
            wizard.Models.RuleSummary);
    }

    /// <summary>
    /// Models are downloaded through Ollama during setup, and the page never said so on a
    /// machine that has no network to download them over.
    /// </summary>
    [Fact]
    public async Task Without_a_network_the_page_says_models_cannot_be_downloaded()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var wizard = Wizard(temp, Release(CatalogueTags()), registrySize: null);

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(wizard.Models.IsOffline);
        Assert.True(wizard.Models.HasNetworkNote);
        Assert.Equal(
            "No network connection: models that are not on this computer yet cannot be " +
            "downloaded now, so they cannot be set up.",
            wizard.Models.NetworkNote);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Подключения к сети нет: модели, которых ещё нет на этом компьютере, сейчас " +
            "скачать не получится, и настроить их не удастся.",
            wizard.Models.NetworkNote);
    }

    [Fact]
    public async Task With_a_network_the_page_says_nothing_about_it()
    {
        using var temp = new TemporaryDirectory("LocalAi-offline-sizes");
        var wizard = Wizard(
            temp,
            Release(CatalogueTags()),
            registrySize: null,
            network: new NetworkSnapshot(ObservationState.Available, null));

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(wizard.Models.IsOffline);
        Assert.False(wizard.Models.HasNetworkNote);
        Assert.Empty(wizard.Models.NetworkNote);
    }

    internal static string[] CatalogueTags()
    {
        var tags = new ModelsPageViewModel().CatalogModels
            .Select(model => model.Tag)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(tags.Length >= 3, "these tests need a catalogue of at least three models");
        return tags;
    }

    /// <summary>
    /// Every tag given, as a signed release lists the catalogue. Each fits a 16 GB adapter at
    /// both contexts except the second, which fits at neither.
    /// </summary>
    internal static ResolvedRelease Release(IReadOnlyList<string> tags) =>
        new(
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
                models: [.. tags.SelectMany((tag, index) =>
                    new[] { 2048, 8192 }.Select(context => index == 1
                        ? new ManifestModel(tag, context, 40 * Gigabyte, 40 * Gigabyte)
                        : new ManifestModel(tag, context, 2 * Gigabyte, 2 * Gigabyte)))]),
            [1],
            [2]);

    private static InstallerWizardViewModel Wizard(
        TemporaryDirectory temp,
        ResolvedRelease release,
        long? registrySize,
        GpuSnapshot? gpu = null,
        NetworkSnapshot? network = null,
        string? unsizedTag = null) =>
        new(
            StartChoice.Install,
            () => null,
            new ReleaseFeed(release),
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported(
                network: network ?? new NetworkSnapshot(ObservationState.Unavailable, null),
                gpu: gpu ?? new GpuSnapshot(
                    ObservationState.Available,
                    [new GpuAdapterSnapshot("adapter-1", "Test Adapter", 16UL * 1024 * 1024 * 1024, false)],
                    null))))
        {
            ModelSizeSource = new FixedRegistry(registrySize, unsizedTag),
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = temp.Combine("no-runtime-root"),
            ScratchRoot = temp.Combine("scratch"),
        };

    internal sealed class FixedRegistry(long? size, string? unsizedTag = null) : IModelSizeSource
    {
        public Task<long?> GetDownloadSizeBytesAsync(string tag, CancellationToken cancellationToken) =>
            Task.FromResult(tag == unsizedTag ? null : size);
    }

    internal sealed class ReleaseFeed(ResolvedRelease release) : IReleaseFeed
    {
        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct) =>
            Task.FromResult(release.Manifest.ReleaseVersion);

        public Task<ResolvedRelease> ResolveAsync(string tag, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(release);

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct) =>
            throw new IOException("these tests do not install");
    }
}
