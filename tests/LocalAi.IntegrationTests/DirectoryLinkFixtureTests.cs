using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DirectoryLinkFixtureTests
{
    [Fact]
    public async Task Cancellation_before_start_does_not_create_a_link()
    {
        using var fixture = new TemporaryDirectory("directory-link-cancelled");
        var target = fixture.Combine("target");
        var link = fixture.Combine("link");
        Directory.CreateDirectory(target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DirectoryLinkFixture.CreateAsync(link, target, cancellation.Token));
        Assert.False(Directory.Exists(link));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("space & caret^ (group)")]
    public async Task Legal_path_values_create_the_exact_directory_link(string name)
    {
        using var fixture = new TemporaryDirectory("directory-link-values");
        var parent = fixture.Combine(name);
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);
        try
        {
            await DirectoryLinkFixture.CreateAsync(link, target, TestContext.Current.CancellationToken);
            AssertExactTarget(link, target);
        }
        finally { if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link); }
    }

    [Theory]
    [InlineData("%LOCALAI_DIRECTORY_LINK_VALUE%", false)]
    [InlineData("%LOCALAI_DIRECTORY_LINK_VALUE%", true)]
    [InlineData("bang!value", false)]
    [InlineData("bang!value", true)]
    public async Task Interpreted_values_are_refused_on_windows_and_remain_literal_on_other_platforms(string value, bool inTarget)
    {
        using var fixture = new TemporaryDirectory("directory-link-refusal");
        var target = fixture.Combine(inTarget ? value : "target");
        var link = fixture.Combine(inTarget ? "link" : value);
        Directory.CreateDirectory(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                    DirectoryLinkFixture.CreateAsync(link, target, TestContext.Current.CancellationToken));
                Assert.Equal(inTarget ? "target" : "path", error.ParamName);
                Assert.False(Directory.Exists(link));
            }
            else
            {
                await DirectoryLinkFixture.CreateAsync(link, target, TestContext.Current.CancellationToken);
                AssertExactTarget(link, target);
            }
        }
        finally { if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link); }
    }

    private static void AssertExactTarget(string link, string target)
    {
        Assert.True(Directory.Exists(link));
        var actual = new DirectoryInfo(link).ResolveLinkTarget(false)?.FullName;
        Assert.True(string.Equals(Path.GetFullPath(target), actual,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }
}
