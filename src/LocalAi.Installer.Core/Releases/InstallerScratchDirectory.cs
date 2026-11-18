using System.Text;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// One run's directory for the downloaded package and its unpacked staging copy — about 800 MB
/// of an update. A closed window ends the process before any cleanup runs, so the directory
/// carries a marker the next start looks for.
///
/// The marker is held open for as long as the run lives, the way the run journal holds its
/// lock: the operating system releases it the moment the process dies, so a marker that opens
/// unshared belongs to a run that has ended, and one that does not belongs to a window still
/// working. Nothing without the marker is touched.
///
/// The removal's temporary copy of the installer claims its directory the same way, under a
/// prefix of its own.
/// </summary>
public sealed class InstallerScratchDirectory : IDisposable
{
    public const string MarkerFileName = ".localai-installer-run";

    private const string Prefix = "run-";
    private const string PackageContainerName = "LocalAi-installer";

    private static readonly byte[] MarkerContent =
        Encoding.ASCII.GetBytes("LocalAi installer run directory\n");

    private FileStream? marker;

    private bool released;

    private InstallerScratchDirectory(string path, FileStream marker)
    {
        Path = path;
        this.marker = marker;
    }

    public string Path { get; }

    public static string DefaultPackageRoot =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), PackageContainerName);

    /// <summary>Reserves a fresh workspace and places it in the usual package staging folder.</summary>
    public static InstallerScratchDirectory ClaimPackageRun()
    {
        var reserved = Directory.CreateTempSubdirectory("LocalAi-installer-reserve-");
        var ownedPath = reserved.FullName;
        try
        {
            var tempRoot = reserved.Parent ?? throw new IOException("The temporary directory has no parent.");
            var packageRoot = tempRoot.CreateSubdirectory(PackageContainerName);
            if (packageRoot.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The installer package staging directory cannot be a link.");
            }

            // Move an exclusively acquired directory on the same volume. A pre-existing
            // destination is an error, and remains outside this call's cleanup scope.
            var destination = System.IO.Path.Combine(packageRoot.FullName, Prefix + Guid.NewGuid().ToString("N"));
            Directory.Move(ownedPath, destination);
            ownedPath = destination;
            return OpenMarker(ownedPath);
        }
        catch
        {
            TryRemove(ownedPath);
            throw;
        }
    }

    public static InstallerScratchDirectory Claim(string root) => Claim(root, Prefix);

    public static InstallerScratchDirectory Claim(string root, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var path = System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return OpenMarker(path);
    }

    /// <summary>Owns a newly created system temporary directory for one release operation.</summary>
    public static InstallerScratchDirectory ClaimTemporary(string prefix)
    {
        var path = Directory.CreateTempSubdirectory(prefix).FullName;
        try
        {
            return OpenMarker(path);
        }
        catch
        {
            TryRemove(path);
            throw;
        }
    }

    private static InstallerScratchDirectory OpenMarker(string path)
    {
        var marker = new FileStream(
            System.IO.Path.Combine(path, MarkerFileName),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);
        try
        {
            marker.Write(MarkerContent);
            marker.Flush(flushToDisk: true);
        }
        catch
        {
            marker.Dispose();
            throw;
        }

        return new(path, marker);
    }

    /// <summary>
    /// Removes the directories of runs that have ended. Never throws: a directory that will not
    /// go now is tried again at the next start.
    /// </summary>
    public static void RemoveLeftovers(string root) => RemoveLeftovers(root, Prefix, requirePlainRoot: true);

    public static void RemoveLeftovers(string root, string prefix) => RemoveLeftovers(root, prefix, requirePlainRoot: false);

    private static void RemoveLeftovers(string root, string prefix, bool requirePlainRoot)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var container = new DirectoryInfo(root);
            if (requirePlainRoot && container.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var directory in container.EnumerateDirectories(prefix + "*"))
            {
                if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                    IsLeftover(directory.FullName))
                {
                    TryRemove(directory.FullName);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
        }
    }

    /// <summary>
    /// Lets the marker go and leaves the directory to a process that outlives this one; until it
    /// ends, only the files it has in use keep the sweep off it.
    /// </summary>
    public void Release()
    {
        marker?.Dispose();
        marker = null;
        released = true;
    }

    public void Dispose()
    {
        marker?.Dispose();
        marker = null;
        if (!released)
        {
            TryRemove(Path);
        }
    }

    private static bool IsLeftover(string directory)
    {
        var path = System.IO.Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var buffer = new byte[MarkerContent.Length + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer.AsSpan(0, read).SequenceEqual(MarkerContent);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The marker goes last, so a removal that stops part way leaves a directory the next start
    /// still recognises.
    /// </summary>
    private static void TryRemove(string directory)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (string.Equals(entry.Name, MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry is DirectoryInfo child &&
                    !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    child.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
        }
    }
}
