using System.Numerics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Installing from a folder, with no network to GitHub.
///
/// The installer is a downloader: the executable is a wizard and the product is the package it
/// fetches while it runs, so handing someone the executable hands them nothing. What this feed
/// changes is where the bytes come from — and nothing about whether they are believed. The
/// manifest still has to be signed by the trusted key and the package still has to match the
/// hash inside that manifest, so a folder someone else prepared is no more trusted than a host
/// someone else runs.
/// </summary>
public sealed class DirectoryReleaseFeedTests : IDisposable
{
    private static readonly BigInteger P256Order = BigInteger.Parse(
        "115792089210356248762697446949407573529996955224135760342422259061068512044369");

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Directory.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task Latest_is_whichever_release_the_folder_holds()
    {
        var source = Release("0.1.45");

        var tag = await Feed(source).ResolveTagAsync(
            "latest",
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    [Theory]
    [InlineData("0.1.45")]
    [InlineData("v0.1.45")]
    public async Task The_release_that_is_there_is_answered_by_either_spelling(string requested)
    {
        var source = Release("0.1.45");

        var tag = await Feed(source).ResolveTagAsync(
            requested,
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    /// <summary>
    /// Quietly installing whatever the folder happened to hold is the surprise an offline
    /// install can least afford: there is no release page to notice the wrong version on.
    /// </summary>
    [Fact]
    public async Task A_release_the_folder_does_not_hold_is_refused_rather_than_substituted()
    {
        var source = Release("0.1.44");

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("0.1.45", TestContext.Current.CancellationToken));

        Assert.Contains("0.1.44", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.45", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ReleaseRefusalKind.OtherReleaseInFolder, exception.Refusal.Kind);
        Assert.Equal("0.1.44", exception.Refusal.Release);
        Assert.Equal("0.1.45", exception.Refusal.Requested);
    }

    [Fact]
    public async Task The_manifest_and_signature_land_in_the_working_directory()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");

        var resolved = await Feed(source).ResolveAsync(
            "0.1.45",
            working,
            TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", resolved.Manifest.ReleaseVersion);
        Assert.True(File.Exists(Path.Combine(working, "release-manifest.json")));
        Assert.True(File.Exists(Path.Combine(working, "release-manifest.sig")));
    }

    [Fact]
    public async Task The_package_is_copied_and_its_progress_reported()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");
        var reported = new List<long>();

        var path = await Feed(source).DownloadPackageAsync(
            "0.1.45",
            working,
            new Progress<long>(reported.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(working, "localai-package.zip"), path);
        Assert.Equal(
            await File.ReadAllBytesAsync(
                Path.Combine(source, "localai-package.zip"),
                TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Copied rather than read where it lies: verification and installation have to see the same
    /// bytes, and the source folder is not somewhere the installer controls for the minutes an
    /// installation takes.
    /// </summary>
    [Fact]
    public async Task The_copy_is_what_survives_a_source_that_changes_afterwards()
    {
        var source = Release("0.1.45");
        var working = Path.Combine(root, "working");
        var path = await Feed(source).DownloadPackageAsync(
            "0.1.45",
            working,
            null,
            TestContext.Current.CancellationToken);

        await File.WriteAllBytesAsync(
            Path.Combine(source, "localai-package.zip"),
            [9, 9, 9],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(
            new byte[] { 9, 9, 9 },
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_folder_missing_a_file_says_which_three_are_needed()
    {
        var source = Release("0.1.45");
        File.Delete(Path.Combine(source, "release-manifest.sig"));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Contains("release-manifest.sig", exception.Message, StringComparison.Ordinal);
        Assert.Contains("localai-package.zip", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point: a folder is not trusted for being a folder. A manifest signed by anything
    /// other than the embedded key is refused exactly as it would be over HTTPS.
    /// </summary>
    [Fact]
    public async Task A_manifest_signed_by_another_key_is_refused()
    {
        var source = Release("0.1.45");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var feed = new DirectoryReleaseFeed(source, stranger.ExportSubjectPublicKeyInfo());

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            feed.ResolveAsync("0.1.45", Path.Combine(root, "working"), TestContext.Current.CancellationToken));

        Assert.Contains("failed verification", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ReleaseVerificationException>(exception.InnerException);
        Assert.Equal(ReleaseRefusalKind.ManifestRejectedInFolder, exception.Refusal.Kind);
    }

    /// <summary>
    /// A correctly signed release for another installer is not a damaged one. Its version comes
    /// from a manifest whose signature was checked first, so it can be named.
    /// </summary>
    [Fact]
    public async Task A_release_for_another_installer_is_named_incompatible_with_its_version()
    {
        var source = Release("0.9.0", compatibility: "another-installer");

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.Incompatible, exception.Refusal.Kind);
        Assert.Equal("0.9.0", exception.Refusal.Release);
        // Still a verification failure, which no other transport is asked to retry.
        Assert.IsType<ReleaseVerificationException>(exception.InnerException);
    }

    /// <summary>
    /// The folder the installer starts from is offered when any one of the three is in it: a
    /// folder missing a file is still where somebody meant to install from, and the check then
    /// names what is missing instead of the folder being silently ignored.
    /// </summary>
    [Theory]
    [InlineData("release-manifest.json")]
    [InlineData("release-manifest.sig")]
    [InlineData("localai-package.zip")]
    public void A_folder_holding_any_one_of_the_three_files_is_offered(string kept)
    {
        var source = Release("0.1.45");
        foreach (var name in DirectoryReleaseFeed.RequiredFiles.Where(name => name != kept))
        {
            File.Delete(Path.Combine(source, name));
        }

        Assert.True(DirectoryReleaseFeed.HoldsAnyReleaseFile(source));
    }

    /// <summary>Anything can sit beside a lone installer, so the rest of a release set counts for nothing.</summary>
    [Fact]
    public void A_folder_holding_none_of_the_three_files_is_not_offered()
    {
        var beside = Path.Combine(root, "beside-the-installer");
        Directory.CreateDirectory(beside);
        foreach (var name in new[] { "LocalAi.Installer.exe", "INSTALL.txt", "release-models.json" })
        {
            File.WriteAllText(Path.Combine(beside, name), "x");
        }

        Assert.False(DirectoryReleaseFeed.HoldsAnyReleaseFile(beside));
        Assert.False(DirectoryReleaseFeed.HoldsAnyReleaseFile(Path.Combine(root, "absent")));
        Assert.False(DirectoryReleaseFeed.HoldsAnyReleaseFile(null));
    }

    /// <summary>All three are looked for before the manifest is read, so every missing one is named.</summary>
    [Fact]
    public async Task Every_missing_file_is_named_not_only_the_first()
    {
        var source = Release("0.1.45");
        File.Delete(Path.Combine(source, "release-manifest.sig"));
        File.Delete(Path.Combine(source, "localai-package.zip"));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.MissingFiles, exception.Refusal.Kind);
        Assert.Equal(["release-manifest.sig", "localai-package.zip"], exception.Refusal.Files);
        Assert.StartsWith(
            $"'{source}' does not hold release-manifest.sig, localai-package.zip.",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The page takes the manifest and the signature; without this the package's absence surfaced
    /// only in the run, after the page had called the release verified.
    /// </summary>
    [Fact]
    public async Task A_folder_without_the_package_is_refused_when_its_release_is_taken()
    {
        var source = Release("0.1.45");
        File.Delete(Path.Combine(source, "localai-package.zip"));

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveAsync(
                "0.1.45",
                Path.Combine(root, "working"),
                TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.MissingFiles, exception.Refusal.Kind);
        Assert.Equal(["localai-package.zip"], exception.Refusal.Files);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_is_called_missing()
    {
        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(Path.Combine(root, "absent")).ResolveTagAsync(
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.NoSuchFolder, exception.Refusal.Kind);
    }

    /// <summary>
    /// A folder this account cannot open hid its files from File.Exists, so it was reported as
    /// not holding release-manifest.json (run 20260927-173013, variant 14). Whether a file is
    /// absent or only hidden cannot be told from outside such a folder, so the folder is what is
    /// named. Here the files are absent and listing is denied, which is what the check sees there.
    /// </summary>
    [Fact]
    public async Task A_folder_this_account_cannot_open_is_not_called_missing()
    {
        var source = Path.Combine(root, "closed");
        Directory.CreateDirectory(source);
        using var denied = Deny(source, FileSystemRights.ListDirectory);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.FolderAccessDenied, exception.Refusal.Kind);
    }

    /// <summary>The folder is looked into only to explain a missing file, never as a condition of its own.</summary>
    [Fact]
    public async Task A_folder_that_cannot_be_listed_but_whose_files_can_be_read_still_resolves()
    {
        var source = Release("0.1.45");
        using var denied = Deny(source, FileSystemRights.ListDirectory);

        var tag = await Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken);

        Assert.Equal("0.1.45", tag);
    }

    [Fact]
    public async Task A_file_this_account_cannot_read_is_named()
    {
        var source = Release("0.1.45");
        using var denied = Deny(
            Path.Combine(source, "release-manifest.json"),
            FileSystemRights.ReadData);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.FileAccessDenied, exception.Refusal.Kind);
        Assert.Equal("release-manifest.json", exception.Refusal.File);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_for_another_reason_carries_the_reason()
    {
        var source = Release("0.1.45");
        using var held = new FileStream(
            Path.Combine(source, "release-manifest.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.FileUnreadable, exception.Refusal.Kind);
        Assert.Equal("release-manifest.json", exception.Refusal.File);
        var cause = Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal(cause.Message, exception.Refusal.Detail);
    }

    [Fact]
    public async Task A_file_larger_than_a_release_publishes_is_named()
    {
        var source = Release("0.1.45");
        File.WriteAllBytes(Path.Combine(source, "release-manifest.json"), new byte[(64 * 1024) + 1]);

        var exception = await Assert.ThrowsAsync<ReleaseResolutionException>(() =>
            Feed(source).ResolveTagAsync("latest", TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseRefusalKind.FileTooLarge, exception.Refusal.Kind);
        Assert.Equal("release-manifest.json", exception.Refusal.File);
    }

    private DirectoryReleaseFeed Feed(string source) =>
        new(source, key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// A deny rule for this account on one file or folder of the test's own, not inherited by
    /// anything inside, and taken back on dispose.
    /// </summary>
    private static Denial Deny(string path, FileSystemRights rights) => new(path, rights);

    private sealed class Denial : IDisposable
    {
        private readonly FileSystemInfo target;
        private readonly FileSystemAccessRule rule;

        public Denial(string path, FileSystemRights rights)
        {
            target = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                rights,
                AccessControlType.Deny);
            Change(security => security.AddAccessRule(rule));
        }

        public void Dispose() => Change(security => security.RemoveAccessRule(rule));

        private void Change(Action<FileSystemSecurity> change)
        {
            if (target is DirectoryInfo directory)
            {
                var security = directory.GetAccessControl();
                change(security);
                directory.SetAccessControl(security);
            }
            else
            {
                var file = (FileInfo)target;
                var security = file.GetAccessControl();
                change(security);
                file.SetAccessControl(security);
            }
        }
    }

    /// <summary>Writes the three files a release publishes, signed by this test's own key.</summary>
    private string Release(
        string version,
        string compatibility = BrokerCompatibilityContract.BuildCompatibilityId)
    {
        var source = Path.Combine(root, "release-" + version);
        Directory.CreateDirectory(source);
        var package = new byte[64 * 1024];
        Random.Shared.NextBytes(package);
        File.WriteAllBytes(Path.Combine(source, "localai-package.zip"), package);

        var manifest = new ReleaseManifest(
            1,
            version,
            version,
            "signed-7",
            BrokerCompatibilityContract.ProtocolVersion,
            compatibility,
            new Uri("https://releases.example.invalid/localai-" + version + ".zip"),
            package.LongLength,
            Convert.ToHexString(SHA256.HashData(package)),
            false,
            []);
        var json = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
        File.WriteAllBytes(Path.Combine(source, "release-manifest.json"), json);
        File.WriteAllBytes(Path.Combine(source, "release-manifest.sig"), Sign(json));
        return source;
    }

    /// <summary>
    /// Low-S normalised, because the verifier rejects the other half of every signature pair —
    /// two encodings of one signature is a malleability the release format does not allow.
    /// </summary>
    private byte[] Sign(byte[] payload)
    {
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        if (s > P256Order / 2)
        {
            var normalised = (P256Order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
            var destination = signature.AsSpan(32);
            destination.Clear();
            normalised.CopyTo(destination[^normalised.Length..]);
        }

        return signature;
    }

    public void Dispose()
    {
        key.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
