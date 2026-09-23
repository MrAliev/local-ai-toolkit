using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Releases;

public sealed partial class OfflineBundleApplication
{
    private OfflineUserPathPlan PlanUserPath(InstallationLayout? layout, IReadOnlyList<OfflineNpmPlan> npm)
    {
        var directories = new List<string>();
        if (layout is not null) directories.Add(layout.LauncherDirectory);
        foreach (var item in npm)
        {
            // npm's Windows command shims resolve node.exe, not an arbitrary renamed host.
            if (!Path.GetFileName(item.NodeExecutable).Equals("node.exe", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            directories.Add(Path.GetDirectoryName(item.NodeExecutable)!);
            directories.Add(Path.Combine(item.Destination, "node_modules", ".bin"));
        }
        var selected = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // These characters change PATH parsing/expansion instead of naming the literal directory.
        if (selected.Any(directory => directory.IndexOfAny([';', '"', '%', '\r', '\n']) >= 0)) throw Invalid();
        var before = pathStore.Read();
        var after = selected.Aggregate(before ?? string.Empty, LauncherPathRegistration.Add);
        if (after.Length >= 32767) throw Invalid();
        return new(before, after, selected);
    }

    private OfflineExecutedStep ApplyUserPath(OfflineExecutionPlan plan, InstallationLayout layout, CancellationToken token)
    {
        var change = plan.UserPath!;
        OfflineExecutedStep Result(bool succeeded, string detail) => new("configuration", "user-path",
            succeeded ? "completed" : "failed", null, false, null, Detail: detail);
        try
        {
            foreach (var directory in change.Directories)
            {
                OfflineEnvironmentBundle.EnsureNoLinks(directory);
                if (!Directory.Exists(directory)) return Result(false, "user-path-target-missing");
            }
            if (plan.RuntimeRoot is not null && !IsRegularFile(layout.LauncherPath))
                return Result(false, "user-path-target-missing");
            foreach (var npm in plan.Npm ?? [])
            {
                var command = DependencyCatalog.Supported.Single(item => item.ActionId == npm.Component).ExecutableName + ".cmd";
                if (!IsRegularFile(npm.NodeExecutable) || !IsRegularFile(Path.Combine(npm.Destination, "node_modules", ".bin", command)))
                    return Result(false, "user-path-target-missing");
            }
        }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "user-path-target-invalid"); }
        token.ThrowIfCancellationRequested();
        string? current;
        try { current = pathStore.Read(); }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "user-path-read-failed"); }
        // The registry has no compare-and-set API here. Detect intervening changes rather
        // than deliberately overwriting one, and verify afterwards without retry/rollback.
        if (!string.Equals(current, change.Before, StringComparison.Ordinal)) return Result(false, "user-path-changed");
        if (string.Equals(current, change.After, StringComparison.Ordinal)) return Result(true, "user-path-already-registered");
        token.ThrowIfCancellationRequested();
        try { pathStore.Write(change.After); }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "user-path-write-failed"); }
        try
        {
            return string.Equals(pathStore.Read(), change.After, StringComparison.Ordinal)
                ? Result(true, "user-path-updated") : Result(false, "user-path-readback-mismatch");
        }
        catch (Exception error) when (IsExecutionFailure(error)) { return Result(false, "user-path-readback-failed"); }
    }

    private static bool IsRegularFile(string path)
    {
        OfflineEnvironmentBundle.EnsureNoLinks(path);
        return File.Exists(path);
    }
}
