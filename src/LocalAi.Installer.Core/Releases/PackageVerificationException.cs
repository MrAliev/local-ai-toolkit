using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

/// <summary>Why <see cref="ReleasePackageVerifier"/> refused a package.</summary>
public enum PackageVerificationProblem
{
    /// <summary>The whole package was read and its length is not the manifest's.</summary>
    SizeMismatch,

    /// <summary>More bytes arrived than the manifest names, so reading stopped there.</summary>
    LargerThanManifest,

    /// <summary>The length matched and the SHA-256 did not.</summary>
    HashMismatch,

    /// <summary>The package matched its manifest and a check on what it holds refused it.</summary>
    ContentsRejected,

    /// <summary>The copy the verifier works on could not be written.</summary>
    NotWritten,
}

/// <summary>
/// A refused package with its cause as facts. The message is the one every package verification
/// failure carries, so nothing reading only the message learns more than before.
/// </summary>
public sealed class PackageVerificationException(PackageVerificationProblem problem)
    : ReleaseVerificationException(ReleasePackageVerifier.FailureMessage)
{
    public PackageVerificationProblem Problem { get; } = problem;

    public long? ExpectedSize { get; init; }

    public long? ActualSize { get; init; }

    /// <summary>As the signed manifest spells it.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Upper-case hex, as the manifest spells its own.</summary>
    public string? ActualSha256 { get; init; }

    /// <summary>The file that could not be written.</summary>
    public string? Path { get; init; }

    /// <summary>What the file system said when it could not be written.</summary>
    public string? Reason { get; init; }
}
