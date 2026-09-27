using LocalAi.Installer.Core.Releases;
using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Anonymous first, the GitHub CLI behind it — and the one case that must never be retried.
/// </summary>
public sealed class FallbackReleaseFeedTests
{
    [Fact]
    public async Task Uses_the_anonymous_feed_and_leaves_the_cli_alone()
    {
        var anonymous = new StubFeed("0.1.45");
        var cli = new StubFeed("0.1.44");
        var feed = new FallbackReleaseFeed(anonymous, cli);

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
        Assert.Equal(0, cli.Calls);
        Assert.Null(feed.FallbackReason);
    }

    /// <summary>
    /// A private fork answers 404 to an anonymous reader, which is indistinguishable from a
    /// wrong tag. Both are worth a second attempt through a signed-in CLI.
    /// </summary>
    [Fact]
    public async Task Falls_back_when_the_anonymous_feed_cannot_read_the_release()
    {
        var anonymous = new StubFeed(new ReleaseResolutionException("404 from the release host."));
        var cli = new StubFeed("0.1.45");
        var feed = new FallbackReleaseFeed(anonymous, cli);

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
        Assert.Equal(1, cli.Calls);
        Assert.Contains("404", feed.FallbackReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that must not be retried. A manifest that failed the embedded key is not a
    /// transport problem, and asking a second transport for the same document until one of
    /// them is believed is the shape of the attack this design refuses.
    /// </summary>
    [Fact]
    public async Task Never_retries_a_verification_failure()
    {
        var anonymous = new StubFeed(new ReleaseResolutionException(
            "The manifest failed verification and will not be used.",
            new ReleaseVerificationException("bad signature")));
        var cli = new StubFeed("0.1.45");
        var feed = new FallbackReleaseFeed(anonymous, cli);

        await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(0, cli.Calls);
    }

    [Fact]
    public async Task Reports_both_failures_when_neither_path_works()
    {
        var anonymous = new StubFeed(new ReleaseResolutionException("the network is blocked"));
        var cli = new StubFeed(new ReleaseResolutionException("not signed in"));
        var feed = new FallbackReleaseFeed(anonymous, cli);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Contains("the network is blocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not signed in", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page words the anonymous path's reason, and says the CLI did not find the release
    /// either, with what the CLI said as the details.
    /// </summary>
    [Fact]
    public async Task When_the_cli_fails_too_the_first_reason_stays_and_the_cli_s_is_carried()
    {
        var anonymous = new StubFeed(new ReleaseResolutionException(
            "GitHub named no newest release.",
            refusal: new(ReleaseRefusalKind.NoPublishedRelease, Repository: "owner/name")));
        var cli = new StubFeed(new ReleaseResolutionException("not signed in"));
        var feed = new FallbackReleaseFeed(anonymous, cli);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.NoPublishedRelease, exception.Refusal.Kind);
        Assert.Equal("owner/name", exception.Refusal.Repository);
        Assert.Equal("not signed in", exception.Refusal.GitHubCliDetail);
    }

    /// <summary>
    /// Saying the CLI did not find the release would be false when it found one whose manifest
    /// was refused, so that refusal is the one reported.
    /// </summary>
    [Fact]
    public async Task A_manifest_the_cli_found_and_refused_is_what_is_reported()
    {
        var anonymous = new StubFeed(new ReleaseResolutionException(
            "Release '0.1.45' has no asset named 'release-manifest.json'.",
            refusal: new(ReleaseRefusalKind.AssetNotFound, Release: "0.1.45", File: "release-manifest.json")));
        var cli = new StubFeed(new ReleaseResolutionException(
            "The manifest for release '0.1.45' failed verification and will not be used.",
            new ReleaseVerificationException("Release verification failed."),
            new(ReleaseRefusalKind.ManifestRejectedOnGitHub, Release: "0.1.45")));
        var feed = new FallbackReleaseFeed(anonymous, cli);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.45", "unused", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.ManifestRejectedOnGitHub, exception.Refusal.Kind);
        Assert.Null(exception.Refusal.GitHubCliDetail);
    }

    [Fact]
    public async Task Works_without_a_secondary_feed_at_all()
    {
        var feed = new FallbackReleaseFeed(new StubFeed("0.1.45"), null);

        Assert.Equal(
            "0.1.45",
            await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));
    }

    private sealed class StubFeed : IReleaseFeed
    {
        private readonly string? tag;
        private readonly Exception? failure;

        public StubFeed(string tag) => this.tag = tag;

        public StubFeed(Exception failure) => this.failure = failure;

        public int Calls { get; private set; }

        public Task<string> ResolveTagAsync(
            string requestedTag,
            CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null ? Task.FromResult(tag!) : Task.FromException<string>(failure);
        }

        public Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<ResolvedRelease>(
                failure ?? new InvalidOperationException("not configured"));
        }

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken cancellationToken)
        {
            Calls++;
            return failure is null
                ? Task.FromResult(Path.Combine(workingDirectory, "localai-package.zip"))
                : Task.FromException<string>(failure);
        }
    }
}
