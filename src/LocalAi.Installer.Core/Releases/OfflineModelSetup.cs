using System.Security.Cryptography;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Releases;

public sealed partial class OfflineBundleApplication
{
    private static OfflineModelSetupPlan PlanModelSetup(OfflineModelsPlan models, InstallationLayout layout)
    {
        var record = Path.Combine(layout.Root, OllamaLaunchRecord.FileName);
        return new(models.Destination, record, LaunchRecordHash(record));
    }

    private async Task<OfflineExecutedStep> ApplyModelSetupAsync(OfflineModelSetupPlan plan, InstallationLayout layout, CancellationToken token)
    {
        OfflineExecutedStep Result(bool success, string detail) => new("configuration", "ollama-model-root",
            success ? "completed" : "failed", null, false, null, Detail: detail);
        try
        {
            OllamaLaunchRecordStore.ValidateModelsDirectory(plan.Directory);
            // Reuse the installer's registry, approved-directory and executable identity
            // checks. A PATH lookup never authorizes unattended backend startup.
            var installed = await applications.FindOllamaAsync(token);
            if (installed?.ExecutablePath is not { Length: > 0 } executable || !Path.IsPathFullyQualified(executable) ||
                executable.StartsWith(@"\\", StringComparison.Ordinal) || executable.StartsWith("//", StringComparison.Ordinal))
                return Result(false, "ollama-installation-unverified");
            OfflineEnvironmentBundle.EnsureNoLinks(executable);
            if (!File.Exists(executable)) return Result(false, "ollama-installation-unavailable");
            token.ThrowIfCancellationRequested();
            if (!string.Equals(plan.BeforeSha256, LaunchRecordHash(plan.RecordPath), StringComparison.Ordinal))
                return Result(false, "ollama-launch-record-changed");
            var store = new OllamaLaunchRecordStore(layout.Root);
            store.Save(executable, installed.DetectedVersion, plan.Directory);
            var record = store.Read();
            return record?.ModelsDirectory == plan.Directory && record.ExecutablePath == Path.GetFullPath(executable) &&
                record.Version == installed.DetectedVersion
                ? Result(true, "ollama-model-root-configured") : Result(false, "ollama-launch-record-readback-failed");
        }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "ollama-model-configuration-failed"); }
    }

    private static string? LaunchRecordHash(string path)
    {
        OfflineEnvironmentBundle.EnsureNoLinks(path);
        if (Directory.Exists(path)) throw Invalid();
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 65536) throw Invalid();
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
