using System.Text;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class GitHubCliUpdateSourceTests
{
    private const string Metadata = """
        {"tag_name":"0.2.3","assets":[
          {"id":17,"name":"release-manifest.json"},
          {"id":19,"name":"release-manifest.sig"},
          {"id":23,"name":"localai-package.zip"}]}
        """;

    [Fact]
    public async Task Only_manifest_and_signature_are_requested_from_the_fixed_api_host()
    {
        var calls = new List<(IReadOnlyList<string> Arguments, int Maximum)>();
        var source = new GitHubCliUpdateSource((arguments, maximum, token) =>
        {
            calls.Add((arguments, maximum));
            return Task.FromResult(Encoding.UTF8.GetBytes(arguments[5].EndsWith("/latest", StringComparison.Ordinal)
                ? Metadata : arguments[5].EndsWith("/17", StringComparison.Ordinal) ? "manifest" : "signature"));
        });
        var result = await source.FetchAsync("owner/project", TestContext.Current.CancellationToken);
        Assert.Equal("0.2.3", result.Tag);
        Assert.Equal("manifest", Encoding.UTF8.GetString(result.Manifest));
        Assert.Equal("signature", Encoding.UTF8.GetString(result.Signature));
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.Equal(new[] { "api", "--hostname", "github.com", "--method", "GET" }, call.Arguments.Take(5));
            Assert.DoesNotContain(call.Arguments, argument => argument.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(call.Arguments, argument => argument.Contains("/23", StringComparison.Ordinal));
        });
        Assert.Equal(65536, calls[1].Maximum);
        Assert.Equal(4096, calls[2].Maximum);
        Assert.Contains("Accept: application/octet-stream", calls[1].Arguments);
    }

    [Theory]
    [InlineData("../owner/project")]
    [InlineData("https://other.invalid/owner/project")]
    [InlineData("--hostname=other.invalid")]
    public async Task Repository_cannot_change_the_api_host_or_arguments(string repository)
    {
        var called = false;
        var source = new GitHubCliUpdateSource((arguments, maximum, token) =>
        {
            called = true;
            return Task.FromResult(Array.Empty<byte>());
        });
        var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() =>
            source.FetchAsync(repository, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateCheckFailure.InvalidResponse, exception.Failure);
        Assert.False(called);
    }

    [Fact]
    public async Task Duplicate_named_assets_are_rejected_before_any_download()
    {
        var calls = 0;
        var source = new GitHubCliUpdateSource((arguments, maximum, token) =>
        {
            calls++;
            return Task.FromResult(Encoding.UTF8.GetBytes(Metadata.Replace(
                "\"localai-package.zip\"", "\"release-manifest.json\"", StringComparison.Ordinal)));
        });
        var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() =>
            source.FetchAsync("owner/project", TestContext.Current.CancellationToken));
        Assert.Equal(UpdateCheckFailure.AssetsUnavailable, exception.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Metadata_exceeding_the_read_bound_is_rejected()
    {
        var source = new GitHubCliUpdateSource((arguments, maximum, token) =>
            Task.FromResult(new byte[maximum + 1]));
        var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() =>
            source.FetchAsync("owner/project", TestContext.Current.CancellationToken));
        Assert.Equal(UpdateCheckFailure.InvalidResponse, exception.Failure);
    }
}
