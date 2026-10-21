using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

public sealed class UpdateCheckAuthenticationConsentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-update-auth-consent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Anonymous_installer_choice_does_not_silently_reuse_a_cli_account()
    {
        Directory.CreateDirectory(root);
        var policies = new UpdateCheckPolicyStore(root);
        policies.Write(new UpdateCheckPolicy(1, true, 72) { Authentication = UpdateCheckAuthentication.GitHubCli });
        Assert.False(UpdateCheckPolicyWriter.Holds(root, true));
        Assert.Equal(ResidencyPolicyOutcome.Applied, UpdateCheckPolicyWriter.Apply(root, true));
        var stored = policies.Read();
        Assert.True(stored.Enabled);
        Assert.Equal(72, stored.IntervalHours);
        Assert.Equal(UpdateCheckAuthentication.Anonymous, stored.Authentication);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
