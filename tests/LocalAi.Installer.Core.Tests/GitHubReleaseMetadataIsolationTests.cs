using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;
using System.Text;

namespace LocalAi.Installer.Core.Tests;

public sealed class GitHubReleaseMetadataIsolationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "LocalAi-release-metadata-" + Guid.NewGuid().ToString("N"));

    public GitHubReleaseMetadataIsolationTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "caller-owned.txt"), "keep");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_resolutions_return_their_own_signed_metadata(bool shareFeed)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var firstPairWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MetadataRunner(root, async (tag, asset, cancellationToken) =>
        {
            if (tag == "0.2.3" && asset == GitHubReleaseFeed.SignatureAsset)
            {
                firstPairWritten.TrySetResult();
                await resumeFirst.Task.WaitAsync(cancellationToken);
            }
        });
        var firstFeed = new GitHubReleaseFeed(runner, gitHubCliPath: "fixture-gh");
        var secondFeed = shareFeed ? firstFeed : new GitHubReleaseFeed(runner, gitHubCliPath: "fixture-gh");
        Task<ResolvedRelease>? first = null;
        Task<ResolvedRelease>? second = null;
        try
        {
            first = firstFeed.ResolveAsync("0.2.3", root, token);
            await firstPairWritten.Task.WaitAsync(token);
            second = secondFeed.ResolveAsync("0.2.2", root, token);
            var secondResult = await second;
            resumeFirst.TrySetResult();
            var firstResult = await first;

            AssertRelease(firstResult, "0.2.3");
            AssertRelease(secondResult, "0.2.2");
            AssertOnlyCallerFileRemains();
        }
        finally
        {
            resumeFirst.TrySetResult();
            await SettleAsync(first);
            await SettleAsync(second);
        }
    }

    [Fact]
    public async Task Sequential_resolutions_preserve_the_caller_directory()
    {
        var feed = new GitHubReleaseFeed(new MetadataRunner(root), gitHubCliPath: "fixture-gh");
        foreach (var tag in new[] { "0.2.3", "0.2.2" })
        {
            AssertRelease(await feed.ResolveAsync(tag, root, TestContext.Current.CancellationToken), tag);
            AssertOnlyCallerFileRemains();
        }
    }

    [Fact]
    public async Task Refused_metadata_does_not_leave_download_files()
    {
        var feed = new GitHubReleaseFeed(new MetadataRunner(root, corruptManifest: true), gitHubCliPath: "fixture-gh");
        await Assert.ThrowsAsync<ReleaseResolutionException>(
            () => feed.ResolveAsync("0.2.3", root, TestContext.Current.CancellationToken));
        AssertOnlyCallerFileRemains();
    }

    [Fact]
    public async Task Cancellation_after_a_download_cleans_only_its_metadata()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new MetadataRunner(root, (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var feed = new GitHubReleaseFeed(runner, gitHubCliPath: "fixture-gh");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => feed.ResolveAsync("0.2.3", root, cancellation.Token));
        AssertOnlyCallerFileRemains();
    }

    private static void AssertRelease(ResolvedRelease release, string tag)
    {
        Assert.Equal(tag, release.Manifest.ReleaseVersion);
        Assert.Equal(Fixture(tag, GitHubReleaseFeed.ManifestAsset), release.ManifestJson);
        Assert.Equal(Fixture(tag, GitHubReleaseFeed.SignatureAsset), release.Signature);
    }

    private void AssertOnlyCallerFileRemains()
    {
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "caller-owned.txt")));
        Assert.Equal(Path.Combine(root, "caller-owned.txt"), Assert.Single(Directory.EnumerateFileSystemEntries(root)));
    }

    // These two published metadata pairs exercise the production trust anchor offline.
    // Update the fixture pairs deliberately if the production signing key is rotated.
    private static byte[] Fixture(string tag, string asset)
    {
        var extension = asset == GitHubReleaseFeed.SignatureAsset ? "sig" : "json";
        using var stream = typeof(GitHubReleaseMetadataIsolationTests).Assembly.GetManifestResourceStream(
            $"GitHubMetadata.manifest-{tag}.{extension}")
            ?? throw new InvalidOperationException("Missing signed metadata fixture.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task SettleAsync(Task? task)
    {
        if (task is null) return;
        try { await task; }
        catch { /* The primary await reports failures; all fixture operations must settle. */ }
    }

    private sealed class MetadataRunner(
        string ownedRoot,
        Func<string, string, CancellationToken, Task>? afterDownload = null,
        bool corruptManifest = false) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("release", arguments[0]);
            Assert.Equal("download", arguments[1]);
            Assert.Contains("--clobber", arguments);
            var tag = arguments[2];
            Assert.Contains(tag, new[] { "0.2.3", "0.2.2" });
            var directory = Path.GetFullPath(ValueAfter(arguments, "--dir"));
            var normalizedRoot = Path.GetFullPath(ownedRoot);
            Assert.True(directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var asset = ValueAfter(arguments, "--pattern");
            Assert.Contains(asset, new[] { GitHubReleaseFeed.ManifestAsset, GitHubReleaseFeed.SignatureAsset });
            var bytes = corruptManifest && asset == GitHubReleaseFeed.ManifestAsset
                ? Encoding.UTF8.GetBytes("{}") : Fixture(tag, asset);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, asset), bytes, cancellationToken);
            if (afterDownload is not null) await afterDownload(tag, asset, cancellationToken);
            return new ProcessResult(0, "", "", false, false);
        }

        private static string ValueAfter(IReadOnlyList<string> arguments, string flag)
        {
            var index = arguments.ToList().IndexOf(flag);
            Assert.InRange(index, 0, arguments.Count - 2);
            return arguments[index + 1];
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
