using System.Globalization;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Why the package step stopped before activation, when the finish page can name the cause. The
/// row is worded from <see cref="Kind"/> and <see cref="Detail"/>; the run log and the journal take
/// <see cref="LogLine"/>, in English like the rest of the log.
/// </summary>
internal sealed record PackageFailure(PackageFailureKind Kind, string? Detail, string LogLine)
{
    private const string NotInstalled = " The package was not installed.";

    /// <summary>
    /// Null for a failure it cannot name. Read from the exception's type and fields, never from its
    /// message: a message is worded for a person and changes with them.
    /// </summary>
    internal static PackageFailure? Of(Exception exception, bool fromFolder) => exception switch
    {
        PackageVerificationException { Problem: PackageVerificationProblem.SizeMismatch } refused =>
            Mismatch(fromFolder, string.Format(
                CultureInfo.InvariantCulture,
                "Local AI package: localai-package.zip is {0} bytes; the signed manifest says {1}.",
                refused.ActualSize,
                refused.ExpectedSize)),
        PackageVerificationException { Problem: PackageVerificationProblem.LargerThanManifest } refused =>
            Mismatch(fromFolder, string.Format(
                CultureInfo.InvariantCulture,
                "Local AI package: localai-package.zip is larger than the {0} bytes the signed manifest says.",
                refused.ExpectedSize)),
        PackageVerificationException { Problem: PackageVerificationProblem.HashMismatch } refused =>
            Mismatch(fromFolder, string.Format(
                CultureInfo.InvariantCulture,
                "Local AI package: localai-package.zip does not match the SHA-256 in the signed manifest " +
                "(manifest {0}, file {1}).",
                refused.ExpectedSha256,
                refused.ActualSha256)),
        PackageVerificationException { Problem: PackageVerificationProblem.ContentsRejected } => new(
            PackageFailureKind.ContentsRejected,
            null,
            "Local AI package: the package matches the signed manifest, but its contents failed the " +
            "installer's checks." + NotInstalled),
        PackageVerificationException { Problem: PackageVerificationProblem.NotWritten } refused =>
            NotWritten(refused.Path, refused.Reason),
        WorkingCopyWriteException copy => NotWritten(copy.Path, copy.Message),
        // The kinds the folder gives a file it cannot open, the same ones the package page words.
        ReleaseResolutionException
        {
            Refusal.Kind: ReleaseRefusalKind.MissingFiles or ReleaseRefusalKind.FileAccessDenied
                or ReleaseRefusalKind.FileUnreadable or ReleaseRefusalKind.FileTooLarge,
        } file => new(
            file.Refusal.Kind switch
            {
                ReleaseRefusalKind.MissingFiles => PackageFailureKind.NotInFolder,
                ReleaseRefusalKind.FileAccessDenied => PackageFailureKind.NoReadAccess,
                ReleaseRefusalKind.FileTooLarge => PackageFailureKind.TooLarge,
                _ => PackageFailureKind.Unreadable,
            },
            file.InnerException?.Message ?? file.Message,
            $"Local AI package: {file.Message}" + NotInstalled),
        // Only a download raises this from the package step: the folder's failures are typed above.
        ReleaseResolutionException download when !fromFolder => new(
            PackageFailureKind.NotDownloaded,
            download.Message,
            $"Local AI package: {download.Message}" + NotInstalled),
        _ => null,
    };

    private static PackageFailure Mismatch(bool fromFolder, string line) => new(
        fromFolder ? PackageFailureKind.FolderCopyDoesNotMatch : PackageFailureKind.DownloadDoesNotMatch,
        null,
        line + NotInstalled);

    /// <summary>The reason loses its closing period to the one the line puts after it.</summary>
    private static PackageFailure NotWritten(string? path, string? reason) => new(
        PackageFailureKind.NotWritten,
        reason,
        $"Local AI package: could not write the package to {path}: {reason?.TrimEnd('.')}." + NotInstalled);
}
