using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Releases;

public enum ReleaseRefusalKind
{
    /// <summary>Nothing more specific is known; <see cref="ReleaseRefusal.Detail"/> holds the message.</summary>
    Unclassified,
    MissingFiles,
    NoSuchFolder,
    FolderAccessDenied,
    FileAccessDenied,
    FileUnreadable,
    FileTooLarge,
    ManifestRejectedInFolder,
    ManifestRejectedOnGitHub,
    Incompatible,
    OtherReleaseInFolder,
    GitHubUnreachable,
    NoPublishedRelease,
    AssetNotFound,
    DownloadFailed,
}

/// <summary>
/// Why a release was refused, kept as facts so the page can word it in the reader's language.
/// The exception's own message stays the English line for the run log.
/// </summary>
public sealed record ReleaseRefusal(
    ReleaseRefusalKind Kind,
    string? Release = null,
    string? Requested = null,
    string? File = null,
    IReadOnlyList<string>? Files = null,
    string? Repository = null,
    string? Detail = null,
    string? GitHubCliDetail = null)
{
    /// <summary>A manifest that failed its signature, unless it was signed correctly for another installer.</summary>
    internal static ReleaseRefusal Rejected(
        ReleaseVerificationException exception,
        ReleaseRefusal signatureFailure) =>
        exception.IncompatibleRelease is { } release
            ? new(ReleaseRefusalKind.Incompatible, Release: release)
            : signatureFailure;
}
