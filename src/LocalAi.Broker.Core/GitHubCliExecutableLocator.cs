namespace LocalAi.Broker;

/// <summary>Resolve gh outside the lexical and physical project/CWD boundaries, without remote executable probes.</summary>
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
        string? project;
        string physicalCwd;
        string? physicalProject;
        string? physicalLexicalProject;
        try
        {
            if (!IsLocal(cwd) || PhysicalPath(cwd, directory: true) is not { } resolvedCwd) return null;
            physicalCwd = resolvedCwd;
            project = FindProject(cwd);
            physicalProject = FindProject(physicalCwd);
            physicalLexicalProject = project is null ? null : PhysicalPath(project, directory: true);
            if (project is not null && physicalLexicalProject is null) return null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
                var physicalDirectory = PhysicalPath(directory, directory: true);
                if (physicalDirectory is null || ExcludedPhysical(physicalDirectory)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = PhysicalPath(Path.Combine(directory, executable), directory: false);
                if (candidate is null || ExcludedPhysical(Path.GetDirectoryName(candidate)!)) continue;
                var exists = (fileExists ?? File.Exists)(candidate);
                cancellationToken.ThrowIfCancellationRequested();
                if (exists) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;

        bool ExcludedPhysical(string directory) => directory.Equals(physicalCwd, comparison)
            || (physicalProject is not null && Within(directory, physicalProject))
            || (physicalLexicalProject is not null && Within(directory, physicalLexicalProject));

        // Resolve every component, including the executable itself. Returning this path also
        // keeps process creation from traversing the PATH alias after it has been checked.
        string? PhysicalPath(string path, bool directory, int links = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (links > 40 || !Path.IsPathFullyQualified(path) || !IsLocal(path)) return null;
            path = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(path)!;
            var parts = path[pathRoot.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var resolved = pathRoot;
            for (var index = 0; index < parts.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = index < parts.Length - 1 || directory;
                var next = Path.Combine(resolved, parts[index]);
                FileSystemInfo item = isDirectory ? new DirectoryInfo(next) : new FileInfo(next);
                if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = item.ResolveLinkTarget(returnFinalTarget: false);
                    if (target is null || PhysicalPath(target.FullName, isDirectory, links + 1) is not { } physical)
                        return null;
                    resolved = physical;
                }
                else
                {
                    if (!item.Exists || (!isDirectory && (item.Attributes & FileAttributes.Directory) != 0)) return null;
                    resolved = next;
                }
            }
            return resolved;
        }

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
