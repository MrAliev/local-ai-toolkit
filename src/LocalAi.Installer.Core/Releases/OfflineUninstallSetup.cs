using System.Security.Cryptography;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Releases;

public sealed partial class OfflineBundleApplication
{
    private const string BundledInstaller = "LocalAi.Installer.exe";

    private OfflineUninstallPlan PlanUninstall(OfflineBundleResult snapshot, InstallationLayout layout)
    {
        var component = snapshot.Manifest.Components.Single(item => item.Kind == "runtime" && item.Id == "localai");
        var file = snapshot.Manifest.Files.SingleOrDefault(file => file.Path == BundledInstaller);
        if (!component.Files.Contains(BundledInstaller, StringComparer.Ordinal) || file is null || file.Bytes <= 0) throw Invalid();
        var before = uninstaller.Read(layout);
        if (before is not null && !SameInstallation(before.InstallLocation, layout.Root)) throw Invalid();
        var destination = Path.Combine(layout.UninstallerDirectory, BundledInstaller);
        OfflineEnvironmentBundle.EnsureNoLinks(destination);
        return new(BundledInstaller, destination, file.Bytes, file.Sha256, "current-user", uninstaller.RegistrySubKey, before);
    }

    private async Task<PreparedUninstaller> PrepareUninstallerAsync(string bundle, OfflineBundleResult snapshot, OfflineUninstallPlan plan, CancellationToken token)
    {
        var stage = new WindowsStagingRootFactory().CreateExclusive(Path.Combine(Path.GetTempPath(), "localai-offline-uninstall-" + Guid.NewGuid().ToString("N")));
        IRetainedStagingFile? retained = null;
        try
        {
            var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var target = Path.Combine(stage.CanonicalPath, BundledInstaller);
            await CopyVerifiedAsync(bundle, plan.Source, target, files[plan.Source], token);
            retained = stage.RetainFile(BundledInstaller);
            if (retained.Metadata.Length != plan.Bytes || !retained.Metadata.Sha256.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            var manifestBytes = await ReadVerifiedAsync(bundle, files["release-manifest.json"], token);
            var signatureBytes = await ReadVerifiedAsync(bundle, files["release-manifest.sig"], token);
            var manifest = verifier.Verify(manifestBytes, signatureBytes);
            if (manifest.RequiresAuthenticode)
            {
                var policy = new AuthenticodePublisherPolicy("CN=LocalAi", new string('0', 64));
                if (policy.IsPlaceholder || !new WindowsAuthenticodeVerifier().IsTrusted(target, policy)) throw Invalid();
            }
            var prepared = new PreparedUninstaller(stage, retained);
            prepared.Revalidate(); return prepared;
        }
        catch
        {
            retained?.Dispose();
            try { stage.Cleanup(); } finally { stage.Dispose(); }
            throw;
        }
    }

    private OfflineExecutedStep ApplyUninstall(OfflineUninstallPlan plan, InstallationLayout layout, string version, PreparedUninstaller prepared, CancellationToken token)
    {
        OfflineExecutedStep Result(bool success, string detail) => new("configuration", "uninstall",
            success ? "completed" : "failed", null, false, null, Detail: detail);
        try
        {
            prepared.Revalidate();
            OfflineEnvironmentBundle.EnsureNoLinks(plan.Destination);
            if (uninstaller.Read(layout) != plan.Before) return Result(false, "uninstall-registration-changed");
            token.ThrowIfCancellationRequested();
            var registered = uninstaller.Register(layout, version, prepared.Path);
            prepared.Revalidate();
            var observed = uninstaller.Read(layout);
            if (registered != observed || registered.DisplayName != UninstallRegistration.DisplayName ||
                registered.DisplayVersion != version || registered.Publisher != UninstallRegistration.Publisher ||
                !SameInstallation(registered.InstallLocation, layout.Root) || registered.UninstallString != "\"" + plan.Destination + "\" --uninstall")
                return Result(false, "uninstall-registration-readback-failed");
            OfflineEnvironmentBundle.EnsureNoLinks(plan.Destination);
            using var copy = new FileStream(plan.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            return copy.Length == plan.Bytes && Convert.ToHexStringLower(SHA256.HashData(copy)).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)
                ? Result(true, "uninstall-registered") : Result(false, "uninstall-copy-readback-failed");
        }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "uninstall-registration-failed"); }
    }

    private static bool SameInstallation(string path, string root) => Path.IsPathFullyQualified(path) &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    private sealed class PreparedUninstaller(IStagingRootLease stage, IRetainedStagingFile file) : IDisposable
    {
        public string Path => System.IO.Path.Combine(stage.CanonicalPath, BundledInstaller);
        public void Revalidate() { stage.ValidateExactLayout([BundledInstaller]); file.Revalidate(); }
        public void Dispose()
        {
            file.Dispose();
            try { stage.Cleanup(); }
            catch (Exception error) when (IsExecutionFailure(error)) { /* A protected staging directory may remain for manual cleanup. */ }
            finally { stage.Dispose(); }
        }
    }
}
