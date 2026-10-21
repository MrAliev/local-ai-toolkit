namespace LocalAi.Broker;

/// <summary>
/// Resolve the opted-in CLI before invoking CreateProcess: never ask the OS to search the
/// broker's current directory. Only explicit absolute local PATH directories are candidates.
/// </summary>
internal static class GitHubCliExecutableLocator
{
    internal static string? Find(string searchPath, string currentDirectory)
    {
        if (!Path.IsPathFullyQualified(currentDirectory)) return null;
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // A drive/filesystem root is not a project tree; don't reject every installed binary
        // merely because a service was launched from C:\ or /.
        var excludeDescendants = !cwd.Equals(Path.GetPathRoot(cwd), comparison);
        var executable = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        foreach (var raw in searchPath.Split(Path.PathSeparator))
        {
            var entry = raw.Trim();
            if (entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"') entry = entry[1..^1];
            if (!Path.IsPathFullyQualified(entry) || entry.Contains('"')) continue;
            // Network/device PATH entries are not a trusted local executable location, and a
            // synchronous existence probe on an unavailable share could outlive cancellation.
            if (OperatingSystem.IsWindows() && entry.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            try
            {
                var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
                if (directory.Equals(cwd, comparison)
                    || (excludeDescendants && directory.StartsWith(cwd + Path.DirectorySeparatorChar, comparison)))
                    continue;
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
