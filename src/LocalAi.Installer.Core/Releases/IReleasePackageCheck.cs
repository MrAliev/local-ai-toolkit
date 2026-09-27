using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// A feed whose package can be compared with its signed manifest before the run: a folder. A
/// download cannot be hashed before it is downloaded.
/// </summary>
public interface IReleasePackageCheck
{
    /// <summary>
    /// True when the package has the size and the SHA-256 the manifest names. The run still
    /// copies the package and verifies the copy.
    /// </summary>
    Task<bool> PackageMatchesAsync(ReleaseManifest manifest, CancellationToken cancellationToken);
}
