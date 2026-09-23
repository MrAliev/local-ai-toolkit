using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineNpmSelection(string Component, string Node, string NpmCli, string Output);
public sealed record OfflineExecutionRequest(int SchemaVersion, string[] Dependencies, bool InstallRuntime, string? ModelsOutput, OfflineNpmSelection[]? Npm = null, bool RegisterPaths = false);
public sealed record OfflineUserPathPlan(string? Before, string After, string[] Directories);
public sealed record OfflineExecutionPlan(string ManifestSha256, string ReleaseVersion, OfflineDependencyPlan[] Dependencies,
    string? RuntimeRoot, OfflineModelsPlan? Models, string OutputDirectory, string[] RemainingRequirements, OfflineNpmPlan[]? Npm = null, OfflineUserPathPlan? UserPath = null)
{
    public string[] DependencyOrder => DependencyCatalog.Supported.Where(item => Dependencies.Any(plan => plan.Component == item.ActionId) ||
        Npm?.Any(plan => plan.Component == item.ActionId) == true).Select(item => item.ActionId).ToArray();
}
public sealed record OfflineExecutedStep(string Kind, string Id, string State, int? ExitCode, bool RebootRequired,
    string? RetainedDirectory, LocalAiPackageInstallResult? Runtime = null, string? Detail = null);
public sealed record OfflineExecutionResult(OfflineExecutionPlan Plan, string State, string JournalPath,
    OfflineExecutedStep[] Steps, string[] PendingSteps, bool RecordingComplete, bool ReadyForUse);

public sealed partial class OfflineBundleApplication
{
    public async Task<OfflineExecutionPlan> PlanExecutionAsync(string bundle, string pin, OfflineExecutionRequest request, string output,
        ModelRoutingCatalogDocument catalog, InstallationLayout layout, CancellationToken token)
        => (await ExecutionMaterialAsync(bundle, pin, request, output, catalog, layout, token)).Plan;

    private async Task<(OfflineBundleResult Snapshot, OfflineExecutionPlan Plan)> ExecutionMaterialAsync(string bundle, string pin,
        OfflineExecutionRequest request, string output, ModelRoutingCatalogDocument catalog, InstallationLayout layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var npmSelections = request.Npm ?? [];
        if (request.SchemaVersion is not (1 or 2 or 3) || request.SchemaVersion == 1 && npmSelections.Length != 0 ||
            request.RegisterPaths && (request.SchemaVersion < 3 || !request.InstallRuntime && npmSelections.Length == 0) ||
            request.Dependencies is null || request.Dependencies.Length > DependencyCatalog.Supported.Count ||
            request.Dependencies.Any(id => id is null || !DependencyCatalog.Supported.Any(item => item.ActionId == id)) ||
            request.Dependencies.Distinct(StringComparer.Ordinal).Count() != request.Dependencies.Length ||
            npmSelections.Length > DependencyCatalog.Supported.Count || npmSelections.Any(selection => selection is null ||
                !DependencyCatalog.Supported.Any(item => item.ActionId == selection.Component && item.InstallerKind == DependencyInstallerKind.Npm) ||
                string.IsNullOrWhiteSpace(selection.Node) || string.IsNullOrWhiteSpace(selection.NpmCli) || string.IsNullOrWhiteSpace(selection.Output)) ||
            npmSelections.Select(selection => selection.Component).Distinct(StringComparer.Ordinal).Count() != npmSelections.Length ||
            npmSelections.Any(selection => request.Dependencies.Contains(selection.Component, StringComparer.Ordinal)) ||
            request.Dependencies.Length == 0 && npmSelections.Length == 0 && !request.InstallRuntime && request.ModelsOutput is null) throw Invalid();
        output = NewExecutionDirectory(output, bundle, layout.Root);
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        ValidateComponents(snapshot.Manifest);
        var dependencies = new List<OfflineDependencyPlan>();
        foreach (var item in DependencyCatalog.Supported.Where(item => request.Dependencies.Contains(item.ActionId, StringComparer.Ordinal)))
            dependencies.Add(await DependencyPlanAsync(bundle, snapshot, item.ActionId, token));
        var npm = new List<OfflineNpmPlan>();
        var outputs = new List<string> { output };
        foreach (var item in DependencyCatalog.Supported)
        {
            var selection = npmSelections.SingleOrDefault(selection => selection.Component == item.ActionId);
            if (selection is null) continue;
            var destination = NewExecutionDirectory(selection.Output, bundle, layout.Root);
            if (outputs.Any(existing => PathsOverlap(existing, destination))) throw Invalid();
            npm.Add(await NpmPlanAsync(bundle, snapshot, selection.Component, selection.Node, selection.NpmCli, destination, token));
            outputs.Add(destination);
        }
        OfflineModelsPlan? models = null;
        if (request.ModelsOutput is not null)
        {
            var destination = NewExecutionDirectory(request.ModelsOutput, bundle, layout.Root);
            if (outputs.Any(existing => PathsOverlap(existing, destination))) throw Invalid();
            var artifacts = await ReadModelArtifactsAsync(bundle, snapshot, catalog, token);
            models = new(artifacts.Models, artifacts.Files.Select(path => path[7..]).ToArray(), artifacts.Bytes, destination, "preflight-required");
        }
        var path = request.RegisterPaths ? PlanUserPath(request.InstallRuntime ? layout : null, npm) : null;
        return (snapshot, new(snapshot.ManifestSha256, snapshot.Manifest.ReleaseVersion, dependencies.ToArray(),
            request.InstallRuntime ? layout.Root : null, models, output,
            ["dependency-prerequisites-and-installed-versions-unverified", "disk-requirements-incomplete",
             "offline-usability-unverified", .. models is null ? Array.Empty<string>() : ["ollama-model-root-configuration", "target-broker-preflight"],
             .. request.InstallRuntime && path is null ? new[] { "launcher-path-configuration" } : Array.Empty<string>(),
             .. npm.Count == 0 ? Array.Empty<string>() : path is null ? ["npm-path-configuration", "npm-toolchain-compatibility-unverified"] : ["npm-toolchain-compatibility-unverified"],
             .. path is null ? Array.Empty<string>() : ["new-process-environment-required"]], npm.ToArray(), path));
    }

    public async Task<OfflineExecutionResult> ExecuteAsync(string bundle, string pin, OfflineExecutionRequest request, string output,
        ModelRoutingCatalogDocument catalog, InstallationLayout layout, CancellationToken token)
    {
        var (snapshot, plan) = await ExecutionMaterialAsync(bundle, pin, request, output, catalog, layout, token);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        VerifiedPackage? package = null;
        try
        {
            // Full runtime trust is established before any dependency can change the machine.
            if (plan.RuntimeRoot is not null) package = await PrepareOfflineRuntimeAsync(bundle, snapshot, token);
            token.ThrowIfCancellationRequested();
            using var directory = new WindowsStagingRootFactory().CreateExclusive(plan.OutputDirectory);
            WriteExecutionRecord(Path.Combine(directory.CanonicalPath, "plan.json"), plan);
            using var journal = InstallerRunJournal.Start(directory.CanonicalPath);
            var steps = new List<OfflineExecutedStep>();
            var pending = plan.DependencyOrder.Select(component => "dependency:" + component)
                .Concat(plan.RuntimeRoot is null ? [] : new[] { "runtime:localai" })
                .Concat(plan.Models is null ? [] : new[] { "models:bundled" })
                .Concat(plan.UserPath is null ? [] : new[] { "configuration:user-path" }).ToList();
            var state = "applied";
            var recording = false;
            var recordingComplete = true;
            string? activeStep = null;
            try
            {
                foreach (var component in plan.DependencyOrder)
                {
                    token.ThrowIfCancellationRequested();
                    recording = true; activeStep = journal.BeginStep(InstallerRunEffectKind.DependencyInstall, component); recording = false;
                    OfflineExecutedStep step;
                    var npm = plan.Npm?.SingleOrDefault(item => item.Component == component);
                    if (npm is not null)
                    {
                        var result = await ApplyNpmAsync(bundle, pin, component, npm.NodeExecutable, npm.NpmCli, npm.Destination, token);
                        step = new("dependency", component, result.State == "applied" ? "completed" : result.State, result.ExitCode, false, result.RetainedDirectory);
                    }
                    else
                    {
                        var result = await ApplyDependencyAsync(bundle, pin, component, token);
                        step = new("dependency", component, result.State, result.ExitCode, result.RebootRequired, result.RetainedDirectory);
                    }
                    Record(step);
                    if (step.State != "completed") { state = step.State; break; }
                    if (step.RebootRequired) { state = "reboot-required"; break; }
                }
                if (state == "applied" && package is not null)
                {
                    token.ThrowIfCancellationRequested();
                    recording = true; activeStep = journal.BeginStep(InstallerRunEffectKind.PackageActivation, "runtime:localai"); recording = false;
                    var result = await new LocalAiPackageInstaller(runner, new ExistingLocalAiInspector(new SystemFileSystemProbe()), TimeSpan.FromMinutes(5))
                        .InstallAsync(package, layout, token);
                    var succeeded = result.Status is LocalAiPackageInstallStatus.Installed or LocalAiPackageInstallStatus.AlreadyInstalled;
                    var uncertain = result.Status is LocalAiPackageInstallStatus.Indeterminate or LocalAiPackageInstallStatus.ManualRecoveryRequired or LocalAiPackageInstallStatus.RollbackFailed;
                    Record(new("runtime", "localai", succeeded ? "completed" : uncertain ? "recovery-required" : "failed", null, false, null, result));
                    if (succeeded) ReleaseInstallService.RecordInstalledRelease(result.VersionPath, result.Version, plan.ReleaseVersion);
                    else state = uncertain ? "recovery-required" : "failed";
                }
                if (state == "applied" && plan.Models is not null)
                {
                    token.ThrowIfCancellationRequested();
                    recording = true; activeStep = journal.BeginStep(InstallerRunEffectKind.ModelInstall, "models:bundled"); recording = false;
                    await ImportModelsAsync(bundle, pin, plan.Models.Destination, catalog, true, token);
                    Record(new("models", "bundled", "completed", null, false, null));
                }
                if (state == "applied" && plan.UserPath is not null)
                {
                    token.ThrowIfCancellationRequested();
                    recording = true; activeStep = journal.BeginStep(InstallerRunEffectKind.UserPath, "configuration:user-path"); recording = false;
                    var step = ApplyUserPath(plan, layout, token);
                    Record(step);
                    if (step.State != "completed") state = step.State;
                }
                Finish();
            }
            catch (Exception error) when (error is OperationCanceledException || IsExecutionFailure(error))
            {
                state = recording ? "recording-failed" : error is OperationCanceledException ? "cancelled" : "failed";
                if (recording) recordingComplete = false;
                else
                {
                    try
                    {
                        // The effect may have partially completed. The durable intent and this
                        // failure never authorize an automatic replay or whole-run rollback.
                        if (activeStep is not null) journal.FailStep(activeStep, state + ";partial-state-unverified");
                        Finish();
                    }
                    catch (Exception writeError) when (IsExecutionFailure(writeError)) { state = "recording-failed"; recordingComplete = false; }
                }
            }
            var report = new OfflineExecutionResult(plan, state, journal.JournalPath, steps.ToArray(), pending.ToArray(), recordingComplete, false);
            try { WriteExecutionRecord(Path.Combine(directory.CanonicalPath, "result.json"), report); }
            catch (Exception error) when (IsExecutionFailure(error)) { report = report with { State = "recording-failed", RecordingComplete = false }; }
            return report;

            void Record(OfflineExecutedStep step)
            {
                steps.Add(step);
                recording = true;
                if (step.State == "completed")
                {
                    journal.CompleteStep(activeStep!, JsonSerializer.Serialize(step, OfflineEnvironmentBundle.Json), false);
                    pending.RemoveAt(0);
                }
                else if (step.State is not ("termination-unconfirmed" or "recovery-required"))
                    journal.FailStep(activeStep!, JsonSerializer.Serialize(step, OfflineEnvironmentBundle.Json));
                // Uncertain effects remain Running in the durable journal.
                recording = false; activeStep = null;
            }

            void Finish()
            {
                // Reboot and uncertain outcomes intentionally keep an incomplete journal.
                if (state is "reboot-required" or "termination-unconfirmed" or "recovery-required") return;
                recording = true;
                journal.Finish(state == "applied" ? InstallerRunOutcome.Completed : state == "cancelled" ? InstallerRunOutcome.Cancelled : InstallerRunOutcome.Failed);
                recording = false;
            }
        }
        finally { package?.TryCleanupAndDispose(); }
    }

    private async Task<VerifiedPackage> PrepareOfflineRuntimeAsync(string bundle, OfflineBundleResult snapshot, CancellationToken token)
    {
        var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var manifest = await ReadVerifiedAsync(bundle, files["release-manifest.json"], token);
        var signature = await ReadVerifiedAsync(bundle, files["release-manifest.sig"], token);
        var packageVerifier = new ReleasePackageVerifier(verifier, new OfflinePackageClient(bundle), new WindowsAuthenticodeVerifier(),
            new AuthenticodePublisherPolicy("CN=LocalAi", new string('0', 64)));
        return await packageVerifier.VerifyAsync(manifest, signature,
            Path.Combine(Path.GetTempPath(), "localai-offline-runtime-" + Guid.NewGuid().ToString("N")), token);
    }

    private sealed class OfflinePackageClient(string bundle) : IReleaseClient
    {
        public Task<Stream> OpenPackageAsync(Uri approvedPackageUri, long maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = OfflineEnvironmentBundle.Resolve(bundle, "localai-package.zip"); OfflineEnvironmentBundle.EnsureNoLinks(path);
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            return Task.FromResult(stream);
        }
    }

    private static string NewExecutionDirectory(string path, string bundle, string runtimeRoot)
    {
        path = Path.GetFullPath(path);
        if (Path.Exists(path)) throw new IOException("The output already exists.");
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent) || PathsOverlap(path, bundle) || PathsOverlap(path, runtimeRoot)) throw Invalid();
        OfflineEnvironmentBundle.EnsureNoLinks(parent);
        return path;
    }

    private static bool PathsOverlap(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)); second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteExecutionRecord(string path, object record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, OfflineEnvironmentBundle.Json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }

    private static bool IsExecutionFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or OverflowException or ReleaseVerificationException or LocalAiPackageInstallationException or
        JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException;
}
