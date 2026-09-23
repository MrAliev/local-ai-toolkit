using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class HardwarePlanBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("signature")]
    [InlineData("tampered")]
    public async Task Untrusted_release_data_is_refused_without_observation_or_exception_details(string damage)
    {
        using var fixture = new Fixture();
        var manifest = Path.Combine(fixture.Release, "release-manifest.json");
        if (damage == "empty") File.WriteAllBytes(manifest, []);
        if (damage == "oversized") File.WriteAllBytes(manifest, new byte[1024 * 1024 + 1]);
        if (damage == "signature") File.WriteAllBytes(Path.Combine(fixture.Release, "release-manifest.sig"), new byte[64]);
        if (damage == "tampered") File.AppendAllText(manifest, "PRIVATE_TEXT");
        var (exit, json) = await fixture.Run();
        Assert.Equal(65, exit);
        Assert.Contains("release_manifest_rejected", json);
        Assert.DoesNotContain("PRIVATE_TEXT", json);
        Assert.DoesNotContain(fixture.Release, json);
        Assert.Equal(0, fixture.Observations);
        Assert.False(Directory.Exists(fixture.Runtime));
    }

    [Fact]
    public async Task Signed_plan_uses_fresh_observations_without_creating_runtime_or_reusing_proofs()
    {
        using var fixture = new Fixture();
        var (firstExit, firstText) = await fixture.Run();
        Assert.Equal(0, firstExit);
        using var first = JsonDocument.Parse(firstText);
        var data = first.RootElement.GetProperty("data");
        Assert.Equal("embedded", data.GetProperty("catalogSource").GetString());
        var plan = data.GetProperty("plan");
        Assert.Equal("RequireFullVram", plan.GetProperty("residencyPolicy").GetString());
        Assert.False(plan.GetProperty("changesPolicy").GetBoolean());
        Assert.Equal(8UL << 30, plan.GetProperty("selectedAdapter").GetProperty("dedicatedLocalBytes").GetUInt64());
        Assert.NotEmpty(plan.GetProperty("models").EnumerateArray());
        Assert.All(plan.GetProperty("models").EnumerateArray(), model => Assert.Equal("preflight-required", model.GetProperty("residencyProof").GetString()));
        fixture.Vram = 4UL << 30;
        var (secondExit, secondText) = await fixture.Run();
        Assert.Equal(0, secondExit);
        using var second = JsonDocument.Parse(secondText);
        Assert.Equal(4UL << 30, second.RootElement.GetProperty("data").GetProperty("plan").GetProperty("selectedAdapter").GetProperty("dedicatedLocalBytes").GetUInt64());
        Assert.Equal(2, fixture.Observations);
        Assert.False(Directory.Exists(fixture.Runtime));
    }

    [Fact]
    public async Task Invalid_catalog_is_not_silently_replaced_by_embedded_defaults()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Runtime, "settings"));
        var path = new ModelRoutingCatalogStore(fixture.Runtime).FilePath;
        File.WriteAllText(path, "invalid PRIVATE_CATALOG");
        var (exit, json) = await fixture.Run();
        Assert.Equal(65, exit);
        Assert.Contains("catalog_rejected", json);
        Assert.DoesNotContain("PRIVATE_CATALOG", json);
        Assert.Equal(0, fixture.Observations);
        Assert.Equal("invalid PRIVATE_CATALOG", File.ReadAllText(path));
    }

    [Fact]
    public async Task Corrupt_policy_fallback_is_visible_and_does_not_weaken_or_rewrite_policy()
    {
        using var fixture = new Fixture();
        var settings = Path.Combine(fixture.Runtime, "settings");
        Directory.CreateDirectory(settings);
        var path = Path.Combine(settings, BrokerPolicy.FileName);
        File.WriteAllText(path, "invalid");
        var (exit, text) = await fixture.Run();
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(text);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("policySource").GetProperty("fileFound").GetBoolean());
        Assert.False(data.GetProperty("policySource").GetProperty("fileUsed").GetBoolean());
        Assert.Equal("RequireFullVram", data.GetProperty("plan").GetProperty("residencyPolicy").GetString());
        Assert.Equal("invalid", File.ReadAllText(path));
    }

    [Fact]
    public async Task Missing_disk_directory_is_not_created_or_observed()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Release, "missing");
        var (exit, text) = await fixture.Run(["--disk", missing]);
        Assert.Equal(2, exit);
        Assert.Contains("invalid_disk_path", text);
        Assert.False(Directory.Exists(missing));
        Assert.Equal(0, fixture.Observations);
    }

    [Fact]
    public void Dependency_inventory_recognizes_shims_but_never_claims_version_or_usability()
    {
        using var directory = new TemporaryDirectory("hardware-dependencies");
        File.WriteAllText(directory.Combine("scip-typescript.cmd"), "@exit /b 99");
        File.WriteAllText(directory.Combine("ollama.exe"), "not an executable");
        var items = HardwarePlanCommand.FindDependencies('"' + directory.Path + '"');
        Assert.Equal("present", items.Single(item => item.Executable == "scip-typescript").Presence);
        Assert.Equal("present", items.Single(item => item.Executable == "ollama.exe").Presence);
        Assert.Equal("not-found", items.Single(item => item.Executable == "git.exe").Presence);
        Assert.All(items, item => Assert.Equal("unverified", item.Verification));
    }

    [Fact]
    public void Json_as_an_option_value_is_not_removed_or_interpreted_as_output_mode()
    {
        string[] args = ["hardware", "plan", "--release", "--json"];
        Assert.False(MachineOutput.Requested(args));
        Assert.Equal(args, MachineOutput.Without(args));
        Assert.True(MachineOutput.Requested([.. args, "--json"]));
        Assert.Equal(args, MachineOutput.Without([.. args, "--json"]));
    }

    [Theory]
    [InlineData(@"\\server\share\release")]
    [InlineData(@"\\?\UNC\server\share\release")]
    [InlineData(@"\\.\pipe\release")]
    public void Remote_and_device_paths_are_rejected_without_probing_the_target(string path)
    {
        Assert.False(HardwarePlanCommand.IsLocalPath(path));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new("hardware-boundary");
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string Release => directory.Path;
        internal string Runtime => directory.Combine("runtime");
        internal ulong Vram { get; set; } = 8UL << 30;
        internal int Observations { get; private set; }

        internal Fixture()
        {
            var catalog = ModelRoutingCatalogStore.LoadEmbedded();
            var manifest = new ReleaseManifest(1, "0.0.0-test", "abcdef123456", catalog.CatalogVersion,
                BrokerCompatibilityContract.ProtocolVersion, BrokerCompatibilityContract.BuildCompatibilityId,
                new Uri("https://example.invalid/package.zip"), 1024, new string('A', 64), false,
                catalog.Models.SelectMany(model => model.ContextTokens.Select(context => new ManifestModel(model.Tag, context, 4L << 30, 4L << 30))).ToArray());
            var payload = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var order = BigInteger.Parse("115792089210356248762697446949407573529996955224135760342422259061068512044369");
            var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
            if (s > order / 2)
            {
                var normalized = (order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
                signature.AsSpan(32).Clear(); normalized.CopyTo(signature.AsSpan(64 - normalized.Length));
            }
            File.WriteAllBytes(Path.Combine(Release, "release-manifest.json"), payload);
            File.WriteAllBytes(Path.Combine(Release, "release-manifest.sig"), signature);
        }

        internal async Task<(int Exit, string Text)> Run(string[]? extra = null)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            using var verifier = new ReleaseManifestVerifier(key.ExportSubjectPublicKeyInfo());
            var exit = await HardwarePlanCommand.ExecuteAsync(["--release", Release, .. extra ?? []], new(output, error), true,
                Token, Runtime, verifier, (_, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested(); Observations++;
                    return Task.FromResult(new HardwareObservation(new(ObservationState.Available, [new("fixture", "GPU", Vram, false)], null),
                        new(ObservationState.Available, 32UL << 30, 16UL << 30), new(ObservationState.Available, 100L << 30, null), []));
                });
            Assert.Empty(error.ToString());
            return (exit, output.ToString());
        }

        public void Dispose() { key.Dispose(); directory.Dispose(); }
    }
}
