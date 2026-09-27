using System.Net;
using System.Text;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The path a first installation takes: a public release, read with no account at all.
///
/// Requiring `gh auth login` — and before it a GitHub account, and before that an invitation —
/// to install a published tool was three obstacles in front of a download anyone is allowed to
/// make. What replaces them is plain HTTPS, and what does not change is that nothing is
/// believed because of where it came from: the manifest is still checked against the embedded
/// key, and the package against the hash inside it.
/// </summary>
public sealed class AnonymousReleaseFeedTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Anonymous.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolves_latest_from_the_redirect_rather_than_the_api()
    {
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-toolkit/releases/latest",
            "https://github.com/MrAliev/local-ai-toolkit/releases/tag/0.1.45");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
        // The anonymous API allows sixty calls an hour per address, and an installer that
        // spends one where a redirect already answers is one that fails on a shared network.
        Assert.DoesNotContain(
            handler.Requests,
            uri => uri.Host.Equals("api.github.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_the_api_when_the_redirect_says_nothing()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-toolkit/releases/latest",
            HttpStatusCode.OK,
            "<html>a page, not a redirect</html>");
        handler.Respond(
            "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/latest",
            HttpStatusCode.OK,
            """{"tag_name":"0.1.45"}""");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    [Fact]
    public async Task An_explicit_tag_is_never_looked_up()
    {
        var handler = new ScriptedHandler();
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var tag = await feed.ResolveTagAsync("0.1.44", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.44", tag);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Downloads_a_package_through_a_redirect_and_reports_progress()
    {
        var payload = Encoding.UTF8.GetBytes(new string('p', 40_000));
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/0.1.45/localai-package.zip",
            "https://objects.githubusercontent.com/package");
        handler.RespondBytes("https://objects.githubusercontent.com/package", payload);
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));
        // Deliberately not Progress<T>: that one posts its callbacks to a synchronization
        // context and delivers them whenever the scheduler gets to it, so this assertion
        // passed on a developer machine and failed on a CI runner with an empty list. A
        // test must not depend on how busy the machine is.
        var seen = new SynchronousProgress();

        var path = await feed.DownloadPackageAsync(
            "0.1.45",
            root,
            seen,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken));
        // Progress is observed from the transfer itself rather than polled off a growing
        // file, so the last report is the real total rather than whatever the poll caught.
        Assert.Equal(payload.Length, seen.Reports[^1]);
        Assert.True(
            seen.Reports.Zip(seen.Reports.Skip(1)).All(pair => pair.First <= pair.Second),
            "progress must only ever grow");
    }

    /// <summary>
    /// A redirect off HTTPS is refused rather than followed. The signature would catch a
    /// substituted manifest anyway, but a download that silently leaves TLS is worth refusing
    /// before the bytes arrive, not after.
    /// </summary>
    [Fact]
    public async Task Refuses_a_redirect_that_leaves_https()
    {
        var handler = new ScriptedHandler();
        handler.Redirect(
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/0.1.45/localai-package.zip",
            "http://mirror.invalid/package");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "0.1.45",
                root,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_missing_asset_by_name()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/9.9.9/localai-package.zip",
            HttpStatusCode.NotFound,
            "Not Found");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.DownloadPackageAsync(
                "9.9.9",
                root,
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("9.9.9", exception.Message, StringComparison.Ordinal);
        Assert.Contains("localai-package.zip", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest that fails the embedded key is refused, whatever transport carried it. This
    /// is the property that makes an anonymous download acceptable in the first place.
    /// </summary>
    [Fact]
    public async Task Refuses_a_manifest_that_fails_verification()
    {
        var handler = new ScriptedHandler();
        handler.Respond(
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/0.1.45/release-manifest.json",
            HttpStatusCode.OK,
            """{"SchemaVersion":1,"ReleaseVersion":"0.1.45"}""");
        handler.RespondBytes(
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/0.1.45/release-manifest.sig",
            new byte[64]);
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.45", root, TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.ManifestRejectedOnGitHub, exception.Refusal.Kind);
        Assert.Equal("0.1.45", exception.Refusal.Release);
    }

    /// <summary>
    /// A 404 for the newest release is an answer: a private repository, or one with nothing
    /// published, looks like that from outside. Advice about the internet connection was the
    /// wrong remedy on every machine where GitHub had answered.
    /// </summary>
    [Fact]
    public async Task A_github_that_answers_without_a_release_is_not_called_unreachable()
    {
        var handler = new ScriptedHandler();
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.NoPublishedRelease, exception.Refusal.Kind);
        Assert.Equal("MrAliev/local-ai-toolkit", exception.Refusal.Repository);
        Assert.Equal(
            "GitHub named no newest release of MrAliev/local-ai-toolkit (web page answered 404 Not " +
            "Found; API answered 404 Not Found). A private repository, or one without published " +
            "releases, looks like this from outside.",
            exception.Message);
    }

    /// <summary>
    /// One answer used to stand for both, so an API out of quota answering 403 beside the web
    /// page's 404 was logged as 403, followed by a sentence about private repositories.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.Forbidden, "404 Not Found", "403 Forbidden")]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.NotFound, "403 Forbidden", "404 Not Found")]
    public async Task Both_answers_are_logged_and_one_404_is_enough_to_suggest_a_private_repository(
        HttpStatusCode page,
        HttpStatusCode api,
        string pageAnswered,
        string apiAnswered)
    {
        var handler = new ScriptedHandler();
        handler.Respond("https://github.com/MrAliev/local-ai-toolkit/releases/latest", page, "page");
        handler.Respond(
            "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/latest",
            api,
            "api");
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.NoPublishedRelease, exception.Refusal.Kind);
        Assert.Equal(
            $"GitHub named no newest release of MrAliev/local-ai-toolkit (web page answered " +
            $"{pageAnswered}; API answered {apiAnswered}). A private repository, or one without " +
            "published releases, looks like this from outside.",
            exception.Message);
    }

    /// <summary>
    /// Without a 404 nothing says the repository is private or has nothing published, so the
    /// answers go to the page as technical details rather than as that reason.
    /// </summary>
    [Theory]
    [InlineData("tagless", "web page answered 200 OK; API answered 200 OK")]
    [InlineData("forbidden", "web page answered 403 Forbidden; API answered 403 Forbidden")]
    [InlineData(
        "unanswered",
        "web page answered no answer: The connection was reset; API answered 403 Forbidden")]
    public async Task Answers_without_a_404_are_not_read_as_a_private_repository(
        string answers,
        string logged)
    {
        const string Page = "https://github.com/MrAliev/local-ai-toolkit/releases/latest";
        const string Api = "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/latest";
        var handler = new ScriptedHandler();
        switch (answers)
        {
            case "tagless":
                handler.Respond(Page, HttpStatusCode.OK, "<html>a page, not a redirect</html>");
                handler.Respond(Api, HttpStatusCode.OK, "{}");
                break;
            case "forbidden":
                handler.Respond(Page, HttpStatusCode.Forbidden, "blocked");
                handler.Respond(Api, HttpStatusCode.Forbidden, "rate limit exceeded");
                break;
            default:
                handler.Throw(Page, new HttpRequestException("The connection was reset."));
                handler.Respond(Api, HttpStatusCode.Forbidden, "rate limit exceeded");
                break;
        }

        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.Unclassified, exception.Refusal.Kind);
        Assert.Equal(
            $"GitHub named no newest release of MrAliev/local-ai-toolkit ({logged}).",
            exception.Message);
        Assert.Equal(exception.Message, exception.Refusal.Detail);
    }

    /// <summary>Only a lookup where neither path got an answer is a connection problem.</summary>
    [Fact]
    public async Task No_answer_from_either_path_is_called_unreachable()
    {
        var handler = new ScriptedHandler();
        handler.Throw(
            "https://github.com/MrAliev/local-ai-toolkit/releases/latest",
            new HttpRequestException("No such host is known. (github.com:443)"));
        handler.Throw(
            "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/latest",
            new HttpRequestException("No such host is known. (api.github.com:443)"));
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.GitHubUnreachable, exception.Refusal.Kind);
        Assert.Equal(
            "Could not reach GitHub to determine the newest release of MrAliev/local-ai-toolkit: " +
            "No such host is known. (api.github.com:443). Check this computer's internet " +
            "connection, then try again.",
            exception.Message);
    }

    [Fact]
    public async Task One_answer_from_github_rules_the_connection_out()
    {
        var handler = new ScriptedHandler();
        handler.Throw(
            "https://github.com/MrAliev/local-ai-toolkit/releases/latest",
            new HttpRequestException("The connection was reset."));
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.NoPublishedRelease, exception.Refusal.Kind);
        Assert.Equal(
            "GitHub named no newest release of MrAliev/local-ai-toolkit (web page answered no " +
            "answer: The connection was reset; API answered 404 Not Found). A private repository, " +
            "or one without published releases, looks like this from outside.",
            exception.Message);
    }

    /// <summary>The page names the file and the release, and sends the reader to the version field.</summary>
    [Fact]
    public async Task A_release_without_its_manifest_names_both()
    {
        var handler = new ScriptedHandler();
        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("9.9.9", root, TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.AssetNotFound, exception.Refusal.Kind);
        Assert.Equal("9.9.9", exception.Refusal.Release);
        Assert.Equal("release-manifest.json", exception.Refusal.File);
    }

    /// <summary>Every other way a download fails keeps its own English message as the details.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("declared-size")]
    [InlineData("streamed-size")]
    [InlineData("insecure-redirect")]
    [InlineData("redirect-loop")]
    [InlineData("timeout")]
    [InlineData("network")]
    public async Task Any_other_failed_download_names_the_file_and_the_release(string failure)
    {
        const string Manifest =
            "https://github.com/MrAliev/local-ai-toolkit/releases/download/0.1.45/release-manifest.json";
        var handler = new ScriptedHandler();
        switch (failure)
        {
            case "status":
                handler.Respond(Manifest, HttpStatusCode.InternalServerError, "boom");
                break;
            case "declared-size":
                handler.Respond(Manifest, HttpStatusCode.OK, new string('m', (64 * 1024) + 1));
                break;
            case "streamed-size":
                handler.RespondUnsized(Manifest, new byte[(64 * 1024) + 1]);
                break;
            case "insecure-redirect":
                handler.Redirect(Manifest, "http://mirror.invalid/release-manifest.json");
                break;
            case "redirect-loop":
                handler.Redirect(Manifest, Manifest);
                break;
            case "timeout":
                handler.Throw(Manifest, new TaskCanceledException("The request timed out."));
                break;
            default:
                handler.Throw(Manifest, new HttpRequestException("The connection was reset."));
                break;
        }

        using var feed = new AnonymousReleaseFeed(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.1.45", root, TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.DownloadFailed, exception.Refusal.Kind);
        Assert.Equal("0.1.45", exception.Refusal.Release);
        Assert.Equal("release-manifest.json", exception.Refusal.File);
        Assert.Equal(exception.Message, exception.Refusal.Detail);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SynchronousProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];

        public void Report(long value) => Reports.Add(value);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> answers =
            new(StringComparer.OrdinalIgnoreCase);

        public List<Uri> Requests { get; } = [];

        public void Redirect(string from, string to) =>
            answers[from] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(to);
                return response;
            };

        public void Respond(string uri, HttpStatusCode status, string body) =>
            answers[uri] = () => new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };

        public void RespondBytes(string uri, byte[] body) =>
            answers[uri] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

        /// <summary>A body with no Content-Length, so only the transfer itself can find its size.</summary>
        public void RespondUnsized(string uri, byte[] body) =>
            answers[uri] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnseekableStream(body)),
            };

        public void Throw(string uri, Exception failure) =>
            answers[uri] = () => throw failure;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(
                answers.TryGetValue(uri.AbsoluteUri, out var answer)
                    ? answer()
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("Not Found"),
                    });
        }
    }

    private sealed class UnseekableStream(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }
}
