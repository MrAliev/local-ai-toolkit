using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class RuntimeEvidenceSurfaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Identity_switch_survives_the_shared_json_argument_normalizer(int position)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "evidence.zip");
        string[] arguments = position switch
        {
            0 => ["diagnostics", "--identity", "--json", "--out", output],
            1 => ["diagnostics", "--json", "--identity", "--out", output],
            _ => ["diagnostics", "--out", output, "--identity", "--json"],
        };
        Assert.True(MachineOutput.Requested(arguments));
        var normalized = MachineOutput.Without(arguments);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await DiagnosticsCommand.ExecuteAsync(normalized[1..], new(stdout, stderr), true, Token, fixture.Root);
        Assert.True(exit == 0, stderr.ToString());
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("contents").TryGetProperty("runtime-evidence.json", out _));
    }

    [Fact]
    public async Task Identity_as_an_invalid_output_operand_does_not_hide_the_explicit_json_error_mode()
    {
        using var fixture = new Fixture();
        string[] arguments = ["diagnostics", "--out", "--identity", "--json"];
        Assert.True(MachineOutput.Requested(arguments));
        Assert.Equal(["diagnostics", "--out", "--identity"], MachineOutput.Without(arguments));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await DiagnosticsCommand.ExecuteAsync(MachineOutput.Without(arguments)[1..], new(stdout, stderr), true, Token, fixture.Root);
        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Precise_identity_is_opt_in_and_uses_the_observed_cli_component_and_validated_index_record()
    {
        using var fixture = new Fixture();
        const string secret = "PRIVATE_repository_model_and_path";
        var repository = Path.Combine(fixture.Root, "repositories", secret);
        Directory.CreateDirectory(repository);
        new RepositoryManifestStore(FsPath.From(repository)).Save(new(secret, secret, secret,
            new string('a', 64), new string('b', 40), secret, 1024, 1, 2,
            RepositoryIndexState.Current, [], DateTimeOffset.UtcNow));
        var normal = await DiagnosticBundle.CaptureAsync(fixture.Root, Token);
        Assert.Equal(5, normal.Contents.Count);
        Assert.False(normal.Contents.ContainsKey("runtime-evidence.json"));

        var precise = await DiagnosticBundle.CaptureAsync(fixture.Root, Token, includeIdentity: true);
        Assert.Equal(6, precise.Contents.Count);
        Assert.True(precise.Manifest.GetProperty("runtimeIdentityIncluded").GetBoolean());
        Assert.False(precise.Manifest.GetProperty("liveHealthChecked").GetBoolean());
        var identity = precise.Contents["runtime-evidence.json"];
        Assert.Equal("localai.runtime-evidence.v1", identity.GetProperty("schema").GetString());
        Assert.Equal("cli", identity.GetProperty("component").GetString());
        var hash = identity.GetProperty("componentFileSha256");
        Assert.Equal("observed", hash.GetProperty("basis").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DiagnosticsCommand).Assembly.Location))),
            hash.GetProperty("value").GetString());
        var index = Assert.Single(identity.GetProperty("indexes").EnumerateArray());
        Assert.Equal(new string('a', 64), index.GetProperty("generationId").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, "manifest.json")))),
            index.GetProperty("manifestSha256").GetString());
        Assert.True(!index.TryGetProperty("sourceCommit", out var commit) || commit.ValueKind == JsonValueKind.Null);
        Assert.Equal("recorded", index.GetProperty("basis").GetString());
        var serialized = JsonSerializer.Serialize(precise, DiagnosticBundle.Json);
        Assert.DoesNotContain(secret, serialized);
        Assert.DoesNotContain(fixture.Root, serialized);
        Assert.Equal("not_evaluated", identity.GetProperty("modelSelection").GetProperty("basis").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_index_identity_does_not_erase_a_healthy_sibling(bool corruptManifest)
    {
        using var fixture = new Fixture();
        var healthy = Path.Combine(fixture.Root, "repositories", "healthy");
        Directory.CreateDirectory(healthy);
        new RepositoryManifestStore(FsPath.From(healthy)).Save(new("healthy", "private", "private",
            new string('a', 64), new string('b', 40), "private-model", 1024, 1, 2,
            RepositoryIndexState.Current, [], DateTimeOffset.UtcNow));
        var unavailable = Path.Combine(fixture.Root, "repositories", "unavailable");
        Directory.CreateDirectory(unavailable);
        if (corruptManifest)
            await File.WriteAllTextAsync(Path.Combine(unavailable, "manifest.json"), "{invalid-json", Token);

        var bundle = await DiagnosticBundle.CaptureAsync(fixture.Root, Token, includeIdentity: true);
        var identity = bundle.Contents["runtime-evidence.json"];
        var indexes = identity.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Equal(2, indexes.Length);
        var recorded = Assert.Single(indexes, item => item.GetProperty("basis").GetString() == "recorded");
        Assert.Equal(new string('a', 64), recorded.GetProperty("generationId").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(healthy, "manifest.json")))),
            recorded.GetProperty("manifestSha256").GetString());
        var unknown = Assert.Single(indexes, item => item.GetProperty("basis").GetString() == "unknown");
        Assert.True(!unknown.TryGetProperty("generationId", out var generation) || generation.ValueKind == JsonValueKind.Null);
        Assert.Equal("unknown", identity.GetProperty("indexState").GetProperty("basis").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_identity_is_written_in_the_zip_in_either_option_order(bool identityFirst)
    {
        using var fixture = new Fixture();
        var output = Path.Combine(fixture.Root, "evidence.zip");
        var args = identityFirst ? new[] { "--identity", "--out", output } : new[] { "--out", output, "--identity" };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await DiagnosticsCommand.ExecuteAsync(args, new(stdout, stderr), true, Token, fixture.Root);
        Assert.True(exit == 0, stderr.ToString());
        using var archive = ZipFile.OpenRead(output);
        Assert.Equal(7, archive.Entries.Count); // Manifest, five default projections, explicit identity.
        Assert.NotNull(archive.GetEntry("runtime-evidence.json"));
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("saved").GetBoolean());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("contents").TryGetProperty("runtime-evidence.json", out _));
    }

    [Theory]
    [InlineData("missing-output")]
    [InlineData("flag-as-output")]
    [InlineData("duplicate-identity")]
    [InlineData("unknown-option")]
    public async Task Invalid_identity_arguments_do_not_write_or_collect(string scenario)
    {
        using var fixture = new Fixture();
        var args = scenario switch
        {
            "missing-output" => new[] { "--out" },
            "flag-as-output" => new[] { "--out", "--identity" },
            "duplicate-identity" => new[] { "--identity", "--identity" },
            _ => new[] { "--unknown" },
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await DiagnosticsCommand.ExecuteAsync(args, new(stdout, stderr), true, Token, fixture.Root);
        Assert.Equal(2, exit);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("localai-identity-surface-");
        public string Root => directory.FullName;
        public void Dispose()
        {
            if (!directory.Name.StartsWith("localai-identity-surface-", StringComparison.Ordinal)
                || directory.FullName != Path.GetFullPath(directory.FullName)
                || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
            directory.Delete(recursive: true);
        }
    }
}
