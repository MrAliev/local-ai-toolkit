namespace LocalAi.Broker;

/// <summary>Resolve gh without implicit CWD search or probes of remote executable paths.</summary>
internal static class GitHubCliExecutableLocator
{
    internal static string? Find(string searchPath, string currentDirectory,
        CancellationToken cancellationToken = default, Func<string, bool>? fileExists = null,
        Func<string, DriveType>? driveType = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(currentDirectory)) return null;
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var executable = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        driveType ??= root => new DriveInfo(root).DriveType;
        var project = IsLocal(cwd) ? FindProject(cwd) : cwd;
        foreach (var raw in searchPath.Split(Path.PathSeparator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = raw.Trim();
            if (entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"') entry = entry[1..^1];
            if (!Path.IsPathFullyQualified(entry) || entry.Contains('"')) continue;
            try
            {
                // Normalize first: //host/share is another spelling of a Windows UNC path.
                var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
                if (!IsLocal(directory) || directory.Equals(cwd, comparison)
                    || (project is not null && Within(directory, project)))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(directory, executable);
                var exists = (fileExists ?? File.Exists)(candidate);
                cancellationToken.ThrowIfCancellationRequested();
                if (exists) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;

        bool IsLocal(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows() && directory.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            try
            {
                var type = driveType(Path.GetPathRoot(directory)!);
                cancellationToken.ThrowIfCancellationRequested();
                return type is not (DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
        }

        string? FindProject(string directory)
        {
            for (var examined = directory; examined is not null; examined = Path.GetDirectoryName(examined))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = Path.Combine(examined, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return examined;
            }
            return null;
        }

        bool Within(string directory, string parent) => directory.Equals(parent, comparison)
            || directory.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }
}
