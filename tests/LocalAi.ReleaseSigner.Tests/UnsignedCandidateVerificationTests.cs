using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace LocalAi.ReleaseSigner.Tests;

public sealed class UnsignedCandidateVerificationTests
{
    [Fact]
    public async Task VerifiesExactAttestationPolicyAndWritesBoundedEvidence()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.RunAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(fixture.ArgumentsPath));
        Assert.Equal(new[]
        {
            "attestation", "verify", fixture.ManifestPath,
            "--hostname", "github.com", "--repo", "MrAliev/local-ai-toolkit",
            "--signer-workflow", CandidateFixture.SignerWorkflow,
            "--source-digest", CandidateFixture.Source,
            "--signer-digest", CandidateFixture.Source,
            "--source-ref", "refs/heads/main",
            "--predicate-type", "https://slsa.dev/provenance/v1",
            "--deny-self-hosted-runners", "--format", "json"
        }, arguments);
        using var evidence = JsonDocument.Parse(File.ReadAllText(fixture.EvidencePath));
        Assert.True(evidence.RootElement.GetProperty("attestationVerified").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("authenticodeVerified").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("installedRuntimeVerified").GetBoolean());
        Assert.Equal(7, evidence.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(Hash(File.ReadAllBytes(fixture.ManifestPath)), evidence.RootElement.GetProperty("manifestSha256").GetString());
        Assert.True(new FileInfo(fixture.EvidencePath).Length < 16_384);
        Assert.Contains("No candidate executable was run", result.Output);
    }

    [Fact]
    public async Task RejectsMutatedBytesBeforeCallingAttestationVerifier()
    {
        using var fixture = new CandidateFixture();
        var file = Path.Combine(fixture.ArtifactRoot, "own-pe", "localai.exe");
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 1; // Preserve size so that the digest check must reject it.
        File.WriteAllBytes(file, bytes);

        await AssertLocalRejection(fixture);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("version")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("repository")]
    [InlineData("source-ref")]
    [InlineData("source-tree")]
    [InlineData("dirty")]
    [InlineData("signed")]
    [InlineData("workflow")]
    [InlineData("workflow-run")]
    [InlineData("schema")]
    [InlineData("runtime")]
    [InlineData("string-run")]
    public async Task RejectsManifestMetadataDrift(string change)
    {
        using var fixture = new CandidateFixture();
        switch (change)
        {
            case "source": fixture.Manifest["sourceCommit"] = new string('b', 40); break;
            case "version": fixture.Manifest["version"] = "0.0.0-other"; break;
            case "run": fixture.Manifest["runId"] = 1235; break;
            case "attempt": fixture.Manifest["runAttempt"] = 2; break;
            case "repository": fixture.Manifest["repository"] = "https://github.com/other/repository"; break;
            case "source-ref": fixture.Manifest["sourceRef"] = "refs/heads/feature"; break;
            case "source-tree": fixture.Manifest["sourceTree"] = "not-a-tree"; break;
            case "dirty": fixture.Manifest["sourceClean"] = false; break;
            case "signed": fixture.Manifest["signed"] = true; break;
            case "workflow": fixture.Manifest["workflow"] = "other/workflow@refs/heads/main"; break;
            case "workflow-run": fixture.Manifest["workflowRun"] = "https://github.com/MrAliev/local-ai-toolkit/actions/runs/1235"; break;
            case "schema": fixture.Manifest["schema"] = "localai.unsigned-candidate.v1"; break;
            case "runtime": fixture.Manifest["runtimeIdentifier"] = "linux-x64"; break;
            case "string-run": fixture.Manifest["runId"] = "1234"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        fixture.WriteManifest();

        await AssertLocalRejection(fixture);
    }

    [Fact]
    public async Task RejectsWrongExpectedSourceEvenWhenManifestIsInternallyConsistent()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.RunAsync(expectedSource: new string('b', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.EvidencePath));
        Assert.False(File.Exists(fixture.ArgumentsPath));
    }

    [Theory]
    [InlineData("extra-root")]
    [InlineData("extra-own-pe")]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("rooted")]
    [InlineData("backslash")]
    [InlineData("duplicate")]
    [InlineData("case-alias")]
    [InlineData("directory")]
    [InlineData("size")]
    public async Task RejectsIncompleteOrNoncanonicalInventory(string change)
    {
        using var fixture = new CandidateFixture();
        var files = fixture.Manifest["files"]!.AsArray();
        switch (change)
        {
            case "extra-root": File.WriteAllText(Path.Combine(fixture.ArtifactRoot, "extra.txt"), "extra"); break;
            case "extra-own-pe": File.WriteAllText(Path.Combine(fixture.ArtifactRoot, "own-pe", "extra.exe"), "extra"); break;
            case "missing": File.Delete(Path.Combine(fixture.ArtifactRoot, "own-pe", "codesearch.exe")); break;
            case "traversal": files[0]!["path"] = "own-pe/../codesearch.exe"; break;
            case "rooted": files[0]!["path"] = Path.GetFullPath(Path.Combine(fixture.ArtifactRoot, "own-pe", "codesearch.exe")); break;
            case "backslash": files[0]!["path"] = "own-pe\\codesearch.exe"; break;
            case "duplicate": files[1] = files[0]!.DeepClone(); break;
            case "case-alias": files[1] = files[0]!.DeepClone(); files[1]!["path"] = "own-pe/CODESEARCH.EXE"; break;
            case "directory":
                var path = Path.Combine(fixture.ArtifactRoot, "own-pe", "codesearch.exe");
                File.Delete(path);
                Directory.CreateDirectory(path);
                break;
            case "size": files[0]!["size"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        fixture.WriteManifest();

        await AssertLocalRejection(fixture);
    }

    [Theory]
    [InlineData("\"schema\":\"localai.unsigned-candidate.v2\",")]
    [InlineData("\"SCHEMA\":\"localai.unsigned-candidate.v2\",")]
    public async Task RejectsDuplicateOrCaseAliasedJsonProperties(string duplicateProperty)
    {
        using var fixture = new CandidateFixture();
        var manifest = File.ReadAllText(fixture.ManifestPath);
        File.WriteAllText(fixture.ManifestPath, "{" + duplicateProperty + manifest[1..]);

        await AssertLocalRejection(fixture);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("invalid")]
    [InlineData("object")]
    [InlineData("missing-fields")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-predicate")]
    [InlineData("no-timestamps")]
    [InlineData("no-certificate")]
    public async Task FailsClosedWhenAttestationVerificationDoesNotEstablishTheManifest(string mode)
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.RunAsync(mode: mode);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(fixture.ArgumentsPath));
        Assert.False(File.Exists(fixture.EvidencePath));
    }

    [Fact]
    public async Task WrongSignerNegativeControlReachesGhAndCannotCreateEvidence()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.RunAsync(signerWorkflow: "MrAliev/local-ai-toolkit/.github/workflows/wrong-signer.yml");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(fixture.ArgumentsPath));
        Assert.False(File.Exists(fixture.EvidencePath));
    }

    [Fact]
    public async Task RejectsEvidenceInsideCandidateWithoutRunningGh()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.RunAsync(evidencePath: Path.Combine(fixture.ArtifactRoot, "evidence.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.ArgumentsPath));
        Assert.False(File.Exists(Path.Combine(fixture.ArtifactRoot, "evidence.json")));
    }

    private static async Task AssertLocalRejection(CandidateFixture fixture)
    {
        var result = await fixture.RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.EvidencePath));
        Assert.False(File.Exists(fixture.ArgumentsPath));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // These tests exercise the CLI contract and fail-closed behavior. The fake gh
    // never proves cryptographic provenance; controlled real-gh verification is a
    // separate workflow acceptance check.
    private sealed class CandidateFixture : IDisposable
    {
        public const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string SignerWorkflow = "MrAliev/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml";
        private static readonly string[] Names =
        [
            "codesearch.exe", "codesearch-mcp.exe", "locallm-mcp.exe", "localai.exe",
            "localai-launcher.exe", "LocalAi.Broker.exe", "LocalAi.Installer.exe"
        ];
        private readonly string directory;
        private readonly string mockBin;
        public string ArtifactRoot { get; }
        public string ManifestPath => Path.Combine(ArtifactRoot, "unsigned-candidate.json");
        public string EvidencePath => Path.Combine(directory, "verified-evidence.json");
        public string ArgumentsPath => Path.Combine(directory, "gh-arguments.json");
        public JsonObject Manifest { get; }

        public CandidateFixture()
        {
            directory = Directory.CreateTempSubdirectory("localai-verifier-tests-").FullName;
            ArtifactRoot = Path.Combine(directory, "candidate");
            mockBin = Path.Combine(directory, "bin");
            Directory.CreateDirectory(Path.Combine(ArtifactRoot, "own-pe"));
            Directory.CreateDirectory(mockBin);
            var files = new JsonArray();
            foreach (var name in Names)
            {
                // Deliberately non-executable fixture bytes must never be launched.
                var bytes = Encoding.UTF8.GetBytes("unsigned candidate fixture: " + name);
                File.WriteAllBytes(Path.Combine(ArtifactRoot, "own-pe", name), bytes);
                files.Add(new JsonObject { ["path"] = "own-pe/" + name, ["size"] = bytes.Length, ["sha256"] = Hash(bytes) });
            }
            Manifest = new JsonObject
            {
                ["schema"] = "localai.unsigned-candidate.v2", ["signed"] = false, ["version"] = "1.2.3",
                ["repository"] = "https://github.com/MrAliev/local-ai-toolkit", ["sourceCommit"] = Source,
                ["sourceTree"] = new string('c', 40), ["sourceRef"] = "refs/heads/main", ["sourceClean"] = true,
                ["workflow"] = SignerWorkflow + "@refs/heads/main",
                ["workflowRun"] = "https://github.com/MrAliev/local-ai-toolkit/actions/runs/1234",
                ["runId"] = 1234, ["runAttempt"] = 1, ["runtimeIdentifier"] = "win-x64", ["files"] = files
            };
            WriteManifest();
            var mockScript = Path.Combine(mockBin, "mock-gh.ps1");
            File.WriteAllText(mockScript, """
                $ErrorActionPreference = 'Stop'
                $utf8 = [Text.UTF8Encoding]::new($false)
                [Console]::InputEncoding = $utf8
                [Console]::OutputEncoding = $utf8
                $OutputEncoding = $utf8
                [IO.File]::WriteAllText($env:LOCALAI_GH_ARGUMENTS, (ConvertTo-Json -InputObject @($args) -Compress), $utf8)
                $signerIndex = [Array]::IndexOf($args, '--signer-workflow')
                if ($signerIndex -lt 0 -or $args[$signerIndex + 1] -cne 'MrAliev/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml') { exit 8 }
                switch ($env:LOCALAI_GH_MODE) {
                    'fail' { exit 9 }
                    'empty' { Write-Output '[]'; exit 0 }
                    'blank' { exit 0 }
                    'invalid' { Write-Output 'not-json'; exit 0 }
                    'object' { Write-Output '{}'; exit 0 }
                    'missing-fields' { Write-Output '[{}]'; exit 0 }
                }
                $hash = (Get-FileHash -LiteralPath $args[2] -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($env:LOCALAI_GH_MODE -eq 'wrong-subject') { $hash = '0' * 64 }
                $predicate = 'https://slsa.dev/provenance/v1'
                if ($env:LOCALAI_GH_MODE -eq 'wrong-predicate') { $predicate = 'https://example.invalid/predicate' }
                $timestamps = @(@{ type = 'tlog'; timestamp = '2026-11-18T00:00:00Z' })
                if ($env:LOCALAI_GH_MODE -eq 'no-timestamps') { $timestamps = @() }
                $certificate = @{ issuer = 'https://token.actions.githubusercontent.com' }
                if ($env:LOCALAI_GH_MODE -eq 'no-certificate') { $certificate = @{} }
                $result = @(@{
                    attestation = @{ bundle = @{ mediaType = 'application/vnd.dev.sigstore.bundle.v0.3+json' } }
                    verificationResult = @{
                        signature = @{ certificate = $certificate }
                        verifiedTimestamps = $timestamps
                        statement = @{
                            predicateType = $predicate
                            subject = @(@{ name = 'unsigned-candidate.json'; digest = @{ sha256 = $hash } })
                        }
                    }
                })
                Write-Output (ConvertTo-Json -InputObject $result -Depth 12 -Compress)
                exit 0
                """, new UTF8Encoding(false));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(mockBin, "gh.cmd"), $"@echo off\r\npwsh -NoProfile -File \"{mockScript}\" %*\r\nexit /b %errorlevel%\r\n", new UTF8Encoding(false));
            }
            else
            {
                var shim = Path.Combine(mockBin, "gh");
                File.WriteAllText(shim, "#!/bin/sh\nexec pwsh -NoProfile -File '" + mockScript.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n", new UTF8Encoding(false));
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public void WriteManifest() => File.WriteAllText(ManifestPath, Manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        public async Task<(int ExitCode, string Output)> RunAsync(string mode = "success", string expectedSource = Source, string signerWorkflow = SignerWorkflow, string? evidencePath = null)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-File", FindVerifier(), "-ArtifactRoot", ArtifactRoot,
                "-ExpectedSource", expectedSource, "-ExpectedVersion", "1.2.3",
                "-ExpectedRunId", "1234", "-ExpectedRunAttempt", "1",
                "-EvidencePath", evidencePath ?? EvidencePath, "-ExpectedSignerWorkflow", signerWorkflow
            }) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = mockBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["LOCALAI_GH_ARGUMENTS"] = ArgumentsPath;
            start.Environment["LOCALAI_GH_MODE"] = mode;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the verifier.");
            var cancellation = TestContext.Current.CancellationToken;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            try { await process.WaitForExitAsync(cancellation); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }

        private static string FindVerifier()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
                {
                    var candidate = Path.Combine(current.FullName, "scripts", "verify-localai-unsigned-candidate.ps1");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            throw new FileNotFoundException("Could not locate scripts/verify-localai-unsigned-candidate.ps1 from the test output directory.");
        }

        public void Dispose()
        {
            var actual = Path.GetFullPath(directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!actual.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Path.GetFileName(actual).StartsWith("localai-verifier-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a test directory outside the task-owned temporary root.");
            Directory.Delete(actual, recursive: true);
        }
    }
}
