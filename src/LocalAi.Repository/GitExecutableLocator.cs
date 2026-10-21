namespace LocalAi.Repository;

/// <summary>
/// Resolves Git from explicit installation directories, without the operating system's
/// implicit executable search in the process working directory.
/// PATH and installations outside the inspected repositories remain owner-controlled.
/// </summary>
public static class GitExecutableLocator
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Resolve(string workingDirectory) =>
        Find(Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.CurrentDirectory, workingDirectory)
        ?? throw new FileNotFoundException(
            "Git was not found in an absolute PATH directory outside the repository.");

    public static string? Find(string searchPath, string currentDirectory, string workingDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(currentDirectory)) return null;
            var cwd = Path.GetFullPath(currentDirectory);
            var working = Path.GetFullPath(
                string.IsNullOrEmpty(workingDirectory) ? cwd : workingDirectory, cwd);
            var roots = new List<string>();
            foreach (var directory in new[] { cwd, working })
            {
                var physicalDirectory = PhysicalPath(directory, directory: true);
                if (physicalDirectory is null) return null;
                foreach (var spelling in new[] { directory, physicalDirectory })
                {
                    foreach (var root in AncestorRepositories(spelling))
                    {
                        var physicalRoot = PhysicalPath(root, directory: true);
                        if (physicalRoot is null) return null;
                        roots.Add(physicalRoot);
                        roots.Add(root);
                    }
                }
            }

            var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
            foreach (var raw in searchPath.Split(Path.PathSeparator))
            {
                var entry = raw.Trim();
                if (entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"')
                    entry = entry[1..^1];
                if (!Path.IsPathFullyQualified(entry) || entry.Contains('"')) continue;
                try
                {
                    var directory = Path.GetFullPath(entry);
                    if (!IsLocal(directory) || roots.Any(root => Within(directory, root))) continue;
                    var candidate = PhysicalPath(Path.Combine(directory, executable), directory: false);
                    if (candidate is null || !IsLocal(candidate)
                        || roots.Any(root => Within(candidate, root))) continue;
                    return candidate;
                }
                catch (Exception exception) when (PathFailure(exception)) { }
            }
        }
        catch (Exception exception) when (PathFailure(exception)) { }
        return null;
    }

    private static IEnumerable<string> AncestorRepositories(string directory)
    {
        for (var examined = directory; examined is not null; examined = Path.GetDirectoryName(examined))
        {
            var marker = Path.Combine(examined, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) yield return examined;
        }
    }

    private static bool Within(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(root, Comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root)
                ? root : root + Path.DirectorySeparatorChar, Comparison);
    }

    private static bool IsLocal(string path)
    {
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        var type = new DriveInfo(Path.GetPathRoot(path)!).DriveType;
        return type is not (DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory);
    }

    // Resolve every component, not just the leaf: a trusted-looking PATH entry can itself
    // be a junction into a repository. Return the physical executable, not its mutable alias.
    private static string? PhysicalPath(string path, bool directory, int links = 0)
    {
        if (links > 40 || !Path.IsPathFullyQualified(path) || !IsLocal(path)) return null;
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var resolved = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var isDirectory = index < parts.Length - 1 || directory;
            var next = Path.Combine(resolved, parts[index]);
            FileSystemInfo item = isDirectory ? new DirectoryInfo(next) : new FileInfo(next);
            var target = item.LinkTarget;
            if (target is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var link = item.ResolveLinkTarget(returnFinalTarget: false);
                if (link is null) return null;
                var physical = PhysicalPath(link.FullName, isDirectory, links + 1);
                if (physical is null) return null;
                resolved = physical;
            }
            else
            {
                if (!item.Exists || (!isDirectory && (item.Attributes & FileAttributes.Directory) != 0))
                    return null;
                resolved = next;
            }
        }
        return resolved;
    }

    private static bool PathFailure(Exception exception) =>
        exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException;
}
