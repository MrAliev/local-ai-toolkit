using System.Globalization;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Releases;

public sealed partial class OfflineBundleApplication
{
    private static string PreflightId(ModelContextRef selection) =>
        selection.Model + ":" + selection.ContextTokens.ToString(CultureInfo.InvariantCulture);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<OfflineExecutedStep> ApplyModelPreflightAsync(ModelContextRef selection, OfflineExecutionPlan plan,
        VerifiedPackage package, InstallationLayout layout, CancellationToken token)
    {
        OfflineExecutedStep Result(string state, string detail, int? exit = null, ModelPreflightCommandSuccess? proof = null,
            ModelResidencyPolicy? policy = null) => new("preflight", PreflightId(selection), state, exit, false, null,
                Detail: detail, Proof: proof, ResidencyPolicy: policy);
        try
        {
            package.Revalidate();
            using var layoutLease = InstallationLayoutLease.Acquire(layout);
            layoutLease.CommitScaffold();
            using var launcher = layoutLease.LockLauncher(package.Files.Single(file => file.RelativePath == LocalAiPackageLayout.StableLauncherFile));
            using var active = ActiveVersionBatchLease.Acquire(layout.BinRoot, package.Manifest.VersionDirectory, TimeSpan.FromMinutes(5), token);
            var recordPath = Path.Combine(layout.Root, OllamaLaunchRecord.FileName);
            OfflineEnvironmentBundle.EnsureNoLinks(recordPath);
            using var recordLease = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = new OllamaLaunchRecordStore(layout.Root).Read();
            if (record?.ModelsDirectory != plan.Models!.Destination) return Result("failed", "ollama-model-root-changed");
            var policyStore = new ModelResidencyPolicyStore(layout.Root);
            var policy = policyStore.Read().ModelResidency;

            var process = await Run(["status"]);
            if (Interrupted(process) is { } stopped) return stopped;
            if (process.ExitCode != 0) return Result("failed", "broker-status-failed", process.ExitCode);
            var status = Parse<ModelStatusCommandSuccess>(process);
            if (status.SchemaVersion != 1 || !status.Accepted || status.Operation != "status" || status.CatalogVersion != plan.CatalogVersion ||
                status.InstalledModels is null || status.PendingPullModels is null || status.InstalledModels.Count > 256 || status.PendingPullModels.Count > 256)
                return Result("failed", "preflight-response-invalid");
            // Never turn an offline readiness check into a download or join a pull whose
            // completion would make imported material indistinguishable from fetched data.
            if (status.PendingPullModels.Contains(selection.Model, StringComparer.Ordinal)) return Result("failed", "model-pull-pending");
            if (!status.InstalledModels.Contains(selection.Model, StringComparer.Ordinal)) return Result("failed", "model-not-installed");
            process = await Run(["preflight", "--model", selection.Model, "--context", selection.ContextTokens.ToString(CultureInfo.InvariantCulture),
                "--catalog-version", plan.CatalogVersion!]);
            if (Interrupted(process) is { } interrupted) return interrupted;
            if (process.ExitCode == 3)
            {
                var rejected = Parse<ModelPreflightCommandRejected>(process);
                return rejected.SchemaVersion == 1 && !rejected.Accepted && rejected.Operation == "preflight" &&
                    rejected.Model == selection.Model && rejected.ContextTokens == selection.ContextTokens && rejected.CatalogVersion == plan.CatalogVersion &&
                    rejected.Code == "residency_rejected" ? Result("failed", "residency-rejected", 3) : Result("failed", "preflight-response-invalid", 3);
            }
            if (process.ExitCode != 0) return Result("failed", "broker-preflight-failed", process.ExitCode);
            var proof = Parse<ModelPreflightCommandSuccess>(process);
            if (proof.SchemaVersion != 1 || !proof.Accepted || proof.Operation != "preflight" || proof.Model != selection.Model ||
                proof.ContextTokens != selection.ContextTokens || proof.CatalogVersion != plan.CatalogVersion || proof.SizeBytes <= 0 || proof.SizeVramBytes < 0 ||
                proof.VerifiedAtUtc == default || proof.VerifiedAtUtc.Offset != TimeSpan.Zero || proof.VerifiedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                return Result("failed", "preflight-response-invalid");
            var acceptable = policy switch
            {
                ModelResidencyPolicy.RequireFullVram => proof.FullyResident && proof.SizeBytes == proof.SizeVramBytes,
                ModelResidencyPolicy.AllowPartialOffload => proof.SizeVramBytes > 0,
                ModelResidencyPolicy.AllowCpu => true,
                _ => false,
            };
            if (!acceptable) return Result("failed", "residency-rejected", 0);
            if (policyStore.Read().ModelResidency != policy) return Result("failed", "residency-policy-changed");
            active.Revalidate(); launcher.Revalidate(); package.Revalidate();
            return Result("completed", "model-residency-verified;source-and-directory-unverified", 0, proof, policy);

            async Task<ProcessResult> Run(string[] arguments)
            {
                token.ThrowIfCancellationRequested(); active.Revalidate(); launcher.Revalidate(); package.Revalidate();
                var result = await runner.RunAsync(launcher.CanonicalPath, ["run", "localai", "model", .. arguments, "--runtime-root", layout.Root],
                    TimeSpan.FromMinutes(30), token);
                active.Revalidate(); launcher.Revalidate(); package.Revalidate();
                return result;
            }

            OfflineExecutedStep? Interrupted(ProcessResult result) => result.Cancelled || result.ExitCode == 4
                ? Result("cancelled", "preflight-interrupted", result.ExitCode) : result.TimedOut ? Result("timed-out", "preflight-interrupted", result.ExitCode) : null;
        }
        catch (ProcessTerminationException) { return Result("termination-unconfirmed", "preflight-termination-unconfirmed"); }
        catch (OperationCanceledException) { return Result("cancelled", "preflight-interrupted"); }
        catch (JsonException) { return Result("failed", "preflight-response-invalid"); }
        catch (Exception error) when (IsExecutionFailure(error) || error is LocalAi.Contracts.Activation.ActivationCoordinationException)
        { return Result("failed", "preflight-trust-or-process-failed"); }
    }

    private static T Parse<T>(ProcessResult result)
    {
        if (result.StandardOutputTruncated || result.StandardOutput.Length is 0 or > 1048576) throw new JsonException();
        return JsonSerializer.Deserialize<T>(result.StandardOutput, LocalAiJson.Strict) ?? throw new JsonException();
    }
}
