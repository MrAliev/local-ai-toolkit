using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class PolicyCommandPrivateUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-private-update-cli-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    [Fact]
    public void Explicit_authenticated_mode_is_persisted_without_enabling_network_checks()
    {
        Assert.Equal(0, Run("set", "--update-check-authentication", "GitHubCli"));
        var store = new UpdateCheckPolicyStore(root);
        Assert.False(store.Read().Enabled);
        using var json = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
        Assert.Equal("GitHubCli", json.RootElement.GetProperty("Authentication").GetString());
        Assert.Contains("GitHub CLI", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("account", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fresh_policy_show_describes_anonymous_authentication_without_network_access()
    {
        Assert.Equal(0, Run("show"));
        Assert.Contains("update check authentication: Anonymous", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Invalid_authentication_mode_does_not_change_existing_consent()
    {
        Assert.Equal(0, Run("set", "--update-check", "on"));
        var path = new UpdateCheckPolicyStore(root).FilePath;
        var before = File.ReadAllBytes(path);
        Assert.Equal(2, Run("set", "--update-check-authentication", "EnvironmentToken"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Authenticated_consent_is_printed_in_the_readers_language()
    {
        using var reading = TestCulture.Reading("ru");
        Assert.Equal(0, Run("set", "--update-check-authentication", "GitHubCli"));
        Assert.Contains("GitHub CLI", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("учётную запись", output.ToString(), StringComparison.Ordinal);
    }

    private int Run(params string[] args) => PolicyCommand.Execute(args, root, output, error);

    [Fact]
    public void Existing_enabled_policy_without_authentication_stays_anonymous()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, UpdateCheckPolicy.FileName),
            "{\"SchemaVersion\":1,\"Enabled\":true,\"IntervalHours\":24}");
        var policy = new UpdateCheckPolicyStore(root).Read();
        Assert.True(policy.Enabled);
        Assert.Equal(UpdateCheckAuthentication.Anonymous, policy.Authentication);
    }

    [Theory]
    [InlineData("\"UnknownAccount\"")]
    [InlineData("4")]
    public void Invalid_authentication_policy_fails_closed(string value)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, UpdateCheckPolicy.FileName),
            "{\"SchemaVersion\":1,\"Enabled\":true,\"IntervalHours\":24,\"Authentication\":" + value + "}");
        Assert.Equal(UpdateCheckPolicy.Default, new UpdateCheckPolicyStore(root).Read());
    }

    [Fact]
    public void Enabling_with_authenticated_mode_does_not_claim_anonymous_only_consent()
    {
        Assert.Equal(0, Run("set", "--update-check-authentication", "GitHubCli"));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, Run("set", "--update-check", "on"));
        Assert.Contains(UpdateCheckPolicy.AuthenticatedDisclosure, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UpdateCheckPolicy.Disclosure, output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
