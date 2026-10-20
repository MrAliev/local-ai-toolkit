namespace LocalAi.Launcher;

public sealed class LauncherApplication
{
    private readonly string _binRoot;
    private readonly VersionResolver _resolver;
    private readonly ToolRunner _runner;

    public LauncherApplication(string binRoot, string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        _binRoot = Path.GetFullPath(binRoot);
        _resolver = new VersionResolver(_binRoot);
        _runner = new ToolRunner(launcherPath);
    }

    public async Task<int> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (string.Equals(tool, "localai", StringComparison.Ordinal) &&
            arguments.Count > 0 &&
            string.Equals(arguments[0], "update", StringComparison.Ordinal))
        {
            ResolvedTool updater;
            UpdateHandoff handoff;
            using (VersionLease.AcquireShared(Path.Combine(_binRoot, "current.lock")))
            {
                updater = _resolver.Resolve(tool);
                handoff = await UpdateHandoff.CreateAsync(
                    updater.ExecutablePath, cancellationToken);
            }

            // The updater may stop the old version and take the exclusive activation lease.
            // Neither it nor this waiting launcher may keep that version alive.
            using (handoff)
            {
                return await _runner.RunAsync(
                    handoff.ExecutablePath,
                    arguments,
                    updater.Version,
                    cancellationToken);
            }
        }

        using var lease = VersionLease.AcquireShared(
            Path.Combine(_binRoot, "current.lock"));
        var resolved = _resolver.Resolve(tool);
        return await _runner.RunAsync(
            resolved.ExecutablePath,
            arguments,
            resolved.Version,
            cancellationToken);
    }
}
