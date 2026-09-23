using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Releases;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class OfflineEnvironmentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bundle_copies_exact_materials_lists_missing_components_and_verifies_after_transfer()
    {
        using var fixture = new Fixture();
        var built = await fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token);
        Assert.Matches("^[a-f0-9]{64}$", built.ManifestSha256);
        Assert.NotEmpty(built.Manifest.Files);
        Assert.Equal(4, built.Manifest.Files.Length);
        Assert.Equal("included", built.Manifest.Components.Single(component => component.Id == "dependency.ollama").Availability);
        Assert.Equal("missing", built.Manifest.Components.Single(component => component.Id == "dependency.git").Availability);
        Assert.Contains(built.Manifest.Components, component => component.Kind == "model" && component.Availability == "missing");
        Assert.False(built.Manifest.CompleteForInstallation);
        Assert.Equal("installation-unverified", built.Manifest.InstallationState);
        Assert.Equal(built.Manifest.Files.Sum(file => file.Bytes), built.Manifest.TransferredBytes);
        foreach (var file in built.Manifest.Files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, file.Path)), File.ReadAllBytes(Path.Combine(fixture.Output, file.Path)));
        Directory.Move(fixture.Output, fixture.Moved);
        var verified = await fixture.Bundle.VerifyAsync(fixture.Moved, built.ManifestSha256, Token);
        Assert.Equal(built.ManifestSha256, verified.ManifestSha256);
        Assert.Equal(built.Manifest.TransferredBytes, verified.Manifest.TransferredBytes);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("manifest")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("wrong-pin")]
    public async Task Transfer_verification_refuses_changed_missing_extra_files_and_untrusted_inventory(string mutation)
    {
        using var fixture = new Fixture();
        var built = await fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token);
        Directory.CreateDirectory(fixture.Output);
        var file = Path.Combine(fixture.Output, "ollama.exe");
        if (mutation == "file") File.WriteAllText(file, "changed");
        if (mutation == "manifest") File.WriteAllText(Path.Combine(fixture.Output, OfflineEnvironmentBundle.ManifestName), "{}");
        if (mutation == "missing") File.Delete(file);
        if (mutation == "extra") File.WriteAllText(Path.Combine(fixture.Output, "extra.exe"), "unlisted");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Bundle.VerifyAsync(fixture.Output,
            mutation == "wrong-pin" ? new string('0', 64) : built.ManifestSha256, Token));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("folder/../outside.exe")]
    [InlineData("folder/file.exe:stream")]
    [InlineData("CON")]
    [InlineData("folder/name.")]
    public async Task Invalid_source_paths_are_rejected_before_publishing_output(string path)
    {
        using var fixture = new Fixture();
        fixture.WriteSource(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public async Task Existing_output_and_corrupt_signed_package_are_not_replaced_or_published()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Output);
        var sentinel = Path.Combine(fixture.Output, "owner.txt"); File.WriteAllText(sentinel, "keep");
        await Assert.ThrowsAsync<IOException>(() => fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        File.WriteAllText(Path.Combine(fixture.Source, "localai-package.zip"), "different package");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Bundle.BuildAsync(fixture.Source, fixture.Moved, fixture.Catalog, Token));
        Assert.False(Directory.Exists(fixture.Moved));
    }

    [Fact]
    public async Task Cancelled_preparation_publishes_nothing()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, cancel.Token));
        Assert.False(Directory.Exists(fixture.Output));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new("offline-environment");
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ReleaseManifestVerifier verifier;
        internal string Source => directory.Combine("source");
        internal string Output => directory.Combine("bundle");
        internal string Moved => directory.Combine("transferred");
        internal ModelRoutingCatalogDocument Catalog { get; } = ModelRoutingCatalogStore.LoadEmbedded();
        internal OfflineEnvironmentBundle Bundle { get; }
        internal ReleaseManifestVerifier Verifier => verifier;

        internal Fixture()
        {
            Directory.CreateDirectory(Source);
            byte[] package = [1, 2, 3, 4];
            var release = new ReleaseManifest(1, "0.0.0-test", "abcdef123456", Catalog.CatalogVersion,
                BrokerCompatibilityContract.ProtocolVersion, BrokerCompatibilityContract.BuildCompatibilityId,
                new Uri("https://example.invalid/package.zip"), package.Length, Convert.ToHexString(SHA256.HashData(package)), false, []);
            var payload = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(release);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var order = BigInteger.Parse("115792089210356248762697446949407573529996955224135760342422259061068512044369");
            var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
            if (s > order / 2)
            {
                var normalized = (order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
                signature.AsSpan(32).Clear(); normalized.CopyTo(signature.AsSpan(64 - normalized.Length));
            }
            File.WriteAllBytes(Path.Combine(Source, "release-manifest.json"), payload);
            File.WriteAllBytes(Path.Combine(Source, "release-manifest.sig"), signature);
            File.WriteAllBytes(Path.Combine(Source, "localai-package.zip"), package);
            File.WriteAllText(Path.Combine(Source, "ollama.exe"), "fixture, never execute");
            WriteSource("ollama.exe");
            verifier = new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo());
            Bundle = new(verifier);
        }

        internal void WriteSource(string path) => File.WriteAllText(Path.Combine(Source, OfflineEnvironmentBundle.SourceName),
            JsonSerializer.Serialize(new { schemaVersion = 1, components = new[] { new { kind = "dependency", id = "dependency.ollama", version = "fixture-version", files = new[] { path } } } }));

        public void Dispose() { verifier.Dispose(); key.Dispose(); directory.Dispose(); }
    }
}
