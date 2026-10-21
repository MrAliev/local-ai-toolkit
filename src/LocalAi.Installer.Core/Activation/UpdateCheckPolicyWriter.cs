using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Activation;

/// <summary>
/// Stores the answer to the update-check question, under the same rule the residency policy
/// follows: never be the thing that creates the LocalAi root.
///
/// A store's <c>Write</c> creates its parent with a plain <c>Directory.CreateDirectory</c>,
/// which inherits whatever %LOCALAPPDATA% grants, and the installation layout lease refuses a
/// root that carries inherited access rules — permanently, with a message that names the
/// condition and not the cure. A first run that fails at the package step must not leave that
/// behind, so this writes only into an installation that already exists.
///
/// Saying no is a write too. Somebody who unticks the box on a reinstall is changing a setting
/// that may already say yes, and skipping the write because the answer was "no" would leave the
/// old yes in place.
/// </summary>
public static class UpdateCheckPolicyWriter
{
    public static ResidencyPolicyOutcome Apply(string runtimeRoot, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (!Directory.Exists(runtimeRoot))
        {
            return ResidencyPolicyOutcome.SkippedWithoutInstallation;
        }

        if (Holds(runtimeRoot, enabled))
        {
            return ResidencyPolicyOutcome.Unchanged;
        }

        var store = new UpdateCheckPolicyStore(runtimeRoot);
        // This checkbox takes consent to anonymous access only. A separately opted-in CLI
        // account must not survive under a disclosure promising that no account is used.
        store.Write(store.Read() with { Enabled = enabled, Authentication = UpdateCheckAuthentication.Anonymous });
        return ResidencyPolicyOutcome.Applied;
    }

    /// <summary>
    /// Whether the settings file already answers this way, in the place a write would put it.
    /// </summary>
    public static bool Holds(string runtimeRoot, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var target = Path.GetFullPath(
            RuntimeDirectories.SettingsFileForWriting(runtimeRoot, UpdateCheckPolicy.FileName));
        if (!File.Exists(target))
        {
            return false;
        }

        var store = new UpdateCheckPolicyStore(runtimeRoot);
        var policy = store.Read();
        return policy.Enabled == enabled && policy.Authentication == UpdateCheckAuthentication.Anonymous &&
            string.Equals(Path.GetFullPath(store.FilePath), target, StringComparison.OrdinalIgnoreCase);
    }
}
