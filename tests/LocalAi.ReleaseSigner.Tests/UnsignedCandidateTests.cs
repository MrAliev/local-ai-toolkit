using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAi.ReleaseSigner.Tests;

public sealed class UnsignedCandidateTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] OwnFiles =
    [
        "codesearch.exe", "codesearch-mcp.exe", "locallm-mcp.exe", "localai.exe",
        "localai-launcher.exe", "LocalAi.Broker.exe", "LocalAi.Installer.exe",
    ];

    [Fact]
    public void The_candidate_workflow_only_attests_unsigned_builds_and_pins_its_actions()
    {
        var workflow = Path.Combine(RepositoryRoot(), ".github", "workflows", "build-unsigned-candidate.yml");
        Assert.True(File.Exists(workflow), "An unsigned candidate workflow is required.");
        var text = File.ReadAllText(workflow);
        Assert.Contains("workflow_dispatch:", text);
        Assert.Contains("github.repository == 'MrAliev/local-ai-toolkit'", text);
        Assert.Contains("github.ref == 'refs/heads/main'", text);
        Assert.Contains("github.event_name == 'workflow_dispatch'", text);
        Assert.Contains("runs-on: windows-2025", text);
        Assert.Contains("ref: ${{ github.sha }}", text);
        Assert.Contains("persist-credentials: false", text);
        Assert.Contains("contents: read", text);
        Assert.Contains("if-no-files-found: error", text);
        Assert.Contains("github.run_id", text);
        Assert.Contains("github.run_attempt", text);
        Assert.Contains("path: publish/unsigned-candidate/artifact/", text);
        Assert.DoesNotMatch("(?m)^\\s*(?:push|pull_request|pull_request_target|workflow_run):", text);
        Assert.DoesNotMatch("secrets\\.|runs-on:\\s*\\[?\\s*self-hosted|contents:\\s*write|gh release|ReleaseSigner|submit-signing-request", text);
        Assert.Contains("id-token: write", text);
        Assert.Contains("attestations: write", text);
        Assert.Contains("--source-digest", text);
        Assert.Contains("--signer-workflow", text);
        Assert.Contains("verify-localai-unsigned-candidate.ps1", text);
        Assert.Contains("artifact-ids:", text);
        Assert.Contains("gh api", text);
        Assert.Contains("'build-unsigned-candidate.yml', 'attest-release.yml'", text);
        Assert.Contains("$state -cne 'active'", text);
        var actions = Regex.Matches(text, @"uses:\s*([^\s]+)");
        Assert.Equal(7, actions.Count);
        foreach (Match action in actions) Assert.Matches(@"^actions/[a-z-]+@[0-9a-f]{40}$", action.Groups[1].Value);
    }

    [Fact]
    public async Task Only_seven_own_binaries_are_exposed_and_checksums_describe_the_actual_bytes()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.Run();
        Assert.True(result.ExitCode == 0, result.Output);
        var artifact = Path.Combine(fixture.Root, "publish", "unsigned-candidate", "artifact");
        Assert.Equal(OwnFiles.Order(), Directory.GetFiles(Path.Combine(artifact, "own-pe")).Select(Path.GetFileName).Order());
        Assert.Equal(8, Directory.GetFiles(artifact, "*", SearchOption.AllDirectories).Length);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifact, "unsigned-candidate.json")));
        Assert.Equal("localai.unsigned-candidate.v2", metadata.RootElement.GetProperty("schema").GetString());
        Assert.False(metadata.RootElement.GetProperty("signed").GetBoolean());
        Assert.Equal(Sha, metadata.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Equal(new string('c', 40), metadata.RootElement.GetProperty("sourceTree").GetString());
        Assert.True(metadata.RootElement.GetProperty("sourceClean").GetBoolean());
        Assert.Equal("refs/heads/main", metadata.RootElement.GetProperty("sourceRef").GetString());
        Assert.Equal("MrAliev/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml@refs/heads/main", metadata.RootElement.GetProperty("workflow").GetString());
        Assert.Equal(123, metadata.RootElement.GetProperty("runId").GetInt64());
        Assert.Equal("win-x64", metadata.RootElement.GetProperty("runtimeIdentifier").GetString());
        Assert.Equal("9.8.7-rc.1", metadata.RootElement.GetProperty("version").GetString());
        foreach (var file in metadata.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(artifact, file.GetProperty("path").GetString()!));
            Assert.Equal(bytes.Length, file.GetProperty("size").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
        }
        var commands = File.ReadAllLines(Path.Combine(fixture.Root, "commands.jsonl"));
        Assert.Equal(7, commands.Length);
        foreach (var command in commands)
        {
            var args = JsonSerializer.Deserialize<string[]>(command)!;
            Assert.Equal("publish", args[0]);
            Assert.Contains("-p:Version=9.8.7-rc.1", args);
            Assert.Contains("-p:FileVersion=9.8.7.0", args);
            Assert.Contains("-p:ContinuousIntegrationBuild=true", args);
        }
    }

    [Theory]
    [InlineData("both-reject", 0, 2)]
    [InlineData("accept-source", 1, 1)]
    [InlineData("accept-signer", 1, 2)]
    public async Task The_workflow_treats_only_two_expected_rejections_as_success(string scenario, int expectedExit, int expectedCalls)
    {
        using var fixture = new CandidateFixture();
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".github", "workflows", "build-unsigned-candidate.yml"));
        var start = Array.FindIndex(lines, line => line == "      - name: Reject attestations from another source or signer");
        Assert.True(start >= 0);
        var run = Array.FindIndex(lines, start, line => line == "        run: |");
        Assert.True(run > start);
        var end = Array.FindIndex(lines, run + 1, line => line.StartsWith("      - name:", StringComparison.Ordinal));
        Assert.True(end > run);
        var step = string.Join(Environment.NewLine, lines.Skip(run + 1).Take(end - run - 1)
            .Select(line => string.IsNullOrWhiteSpace(line) ? "" : line[10..]));
        var result = await fixture.RunNegativeControlStep(step, scenario);
        Assert.True(result.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {result.ExitCode}. {result.Output}");
        Assert.Equal(expectedCalls, result.Calls);
    }

    [Theory]
    [InlineData("GITHUB_REPOSITORY", "fork/local-ai-toolkit")]
    [InlineData("GITHUB_REF", "refs/heads/unreviewed")]
    [InlineData("GITHUB_EVENT_NAME", "pull_request_target")]
    [InlineData("GITHUB_SHA", "not-a-commit")]
    [InlineData("GITHUB_RUN_ID", "1;Write-Output unsafe")]
    [InlineData("GITHUB_RUN_ID", "999999999999999999999999999")]
    [InlineData("GITHUB_RUN_ATTEMPT", "999999999999999999999999999")]
    [InlineData("FIXTURE_HEAD", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("GITHUB_WORKFLOW_REF", "fork/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml@refs/heads/main")]
    [InlineData("FIXTURE_DIRTY", " M src/unreviewed.cs")]
    public async Task An_untrusted_source_context_is_rejected_before_building(string variable, string value)
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.Run(environment: new() { [variable] = value });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("trusted main workflow", result.Output);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "commands.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "publish")));
    }

    [Theory]
    [InlineData("1.2.3;Write-Output unsafe")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-rc.01")]
    [InlineData("65535.2.3")]
    public async Task Invalid_versions_are_rejected_before_building(string version)
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.Run(version);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ReleaseVersion", result.Output);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "commands.jsonl")));
    }

    [Theory]
    [InlineData("FIXTURE_FAIL_PROJECT", "LocalAi.Broker")]
    [InlineData("FIXTURE_MISSING_PROJECT", "LocalAi.Installer")]
    [InlineData("FIXTURE_SIGNATURE_STATUS", "Valid")]
    public async Task Failed_or_incomplete_builds_never_expose_an_uploadable_artifact(string variable, string value)
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.Run(environment: new() { [variable] = value });
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "publish", "unsigned-candidate", "artifact")));
    }

    [Fact]
    public async Task An_existing_output_is_not_deleted_or_reused()
    {
        using var fixture = new CandidateFixture();
        var path = Path.Combine(fixture.Root, "publish", "unsigned-candidate");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "sentinel"), "keep");
        var result = await fixture.Run();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "sentinel")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "commands.jsonl")));
    }

    [Fact]
    public async Task A_source_change_during_build_never_exposes_an_uploadable_artifact()
    {
        using var fixture = new CandidateFixture();
        var result = await fixture.Run(environment: new() { ["FIXTURE_DIRTY_AFTER_BUILD"] = "1" });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("clean source checkout", result.Output);
        Assert.Equal(7, File.ReadAllLines(Path.Combine(fixture.Root, "commands.jsonl")).Length);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "publish", "unsigned-candidate", "artifact")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class CandidateFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("localai-unsigned-candidate-").FullName;

        public CandidateFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            var script = Path.Combine(RepositoryRoot(), "scripts", "build-localai-unsigned-candidate.ps1");
            Assert.True(File.Exists(script), "The unsigned candidate build script is required.");
            File.Copy(script, Path.Combine(Root, "scripts", "candidate.ps1"));
            File.WriteAllText(Path.Combine(Root, "harness.ps1"), """
                param([string] $ReleaseVersion)
                function global:git {
                    $global:LASTEXITCODE = 0
                    if ($args -contains 'status') {
                        if ($env:FIXTURE_DIRTY) { $env:FIXTURE_DIRTY }
                    } elseif ($args -contains 'HEAD^{tree}') { 'cccccccccccccccccccccccccccccccccccccccc' }
                    else { $env:FIXTURE_HEAD }
                }
                function global:Get-AuthenticodeSignature {
                    param([string] $LiteralPath)
                    if ($env:FIXTURE_SIGNATURE_STATUS) { @{ Status = $env:FIXTURE_SIGNATURE_STATUS } }
                    else { @{ Status = 'NotSigned' } }
                }
                function global:dotnet {
                    $global:LASTEXITCODE = 0
                    ConvertTo-Json -InputObject @($args) -Compress | Add-Content -LiteralPath (Join-Path $PSScriptRoot 'commands.jsonl')
                    $name = [IO.Path]::GetFileNameWithoutExtension($args[1])
                    if ($env:FIXTURE_FAIL_PROJECT -eq $name) { $global:LASTEXITCODE = 42; return }
                    $output = $args[[Array]::IndexOf($args, '-o') + 1]
                    New-Item -ItemType Directory -Path $output -Force | Out-Null
                    $names = @{
                        'CodeSearch.Cli'='codesearch.exe'; 'CodeSearch.Mcp'='codesearch-mcp.exe';
                        'LocalLm.Mcp'='locallm-mcp.exe'; 'LocalAi.Cli'='localai.exe';
                        'LocalAi.Launcher'='localai-launcher.exe'; 'LocalAi.Broker'='LocalAi.Broker.exe';
                        'LocalAi.Installer'='LocalAi.Installer.exe'
                    }
                    if ($env:FIXTURE_MISSING_PROJECT -ne $name) {
                        [IO.File]::WriteAllText((Join-Path $output $names[$name]), $name)
                    }
                    [IO.File]::WriteAllText((Join-Path $output 'upstream.dll'), 'must not be submitted')
                    [IO.File]::WriteAllText((Join-Path $output 'unexpected.exe'), 'must not be submitted')
                    if ($env:FIXTURE_DIRTY_AFTER_BUILD) { $env:FIXTURE_DIRTY = ' M src/unreviewed.cs' }
                }
                & (Join-Path $PSScriptRoot 'scripts/candidate.ps1') -ReleaseVersion $ReleaseVersion
                """);
        }

        public async Task<(int ExitCode, string Output)> Run(string version = "9.8.7-rc.1", Dictionary<string, string>? environment = null)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(Root, "harness.ps1"), "-ReleaseVersion", version }) start.ArgumentList.Add(argument);
            start.Environment["GITHUB_REPOSITORY"] = "MrAliev/local-ai-toolkit";
            start.Environment["GITHUB_REF"] = "refs/heads/main";
            start.Environment["GITHUB_EVENT_NAME"] = "workflow_dispatch";
            start.Environment["GITHUB_SHA"] = Sha;
            start.Environment["GITHUB_RUN_ID"] = "123";
            start.Environment["GITHUB_RUN_ATTEMPT"] = "1";
            start.Environment["GITHUB_WORKFLOW_REF"] = "MrAliev/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml@refs/heads/main";
            start.Environment["FIXTURE_HEAD"] = Sha;
            if (environment is not null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, await stdout + await stderr);
        }

        public async Task<(int ExitCode, string Output, int Calls)> RunNegativeControlStep(string step, string scenario)
        {
            var harness = Path.Combine(Root, "control-step.ps1");
            var calls = Path.Combine(Root, "control-calls.txt");
            // Execute the actual workflow body with the documented GitHub pwsh suffix.
            // The fake supplies exit codes only; it does not establish attestation validity.
            File.WriteAllText(harness, """
                $ErrorActionPreference = 'Stop'
                $global:controlInvocations = 0
                function global:gh {
                    $global:controlInvocations++
                    $global:LASTEXITCODE = if (
                        ($env:CONTROL_SCENARIO -eq 'accept-source' -and $global:controlInvocations -eq 1) -or
                        ($env:CONTROL_SCENARIO -eq 'accept-signer' -and $global:controlInvocations -eq 2)) { 0 } else { 1 }
                    Set-Content -LiteralPath $env:CONTROL_CALLS_PATH -Value $global:controlInvocations
                }

                """ + step + Environment.NewLine + """
                if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }
                """);
            var start = new ProcessStartInfo("pwsh")
            {
                WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-File", harness }) start.ArgumentList.Add(argument);
            start.Environment["CONTROL_SCENARIO"] = scenario;
            start.Environment["CONTROL_CALLS_PATH"] = calls;
            start.Environment["EXPECTED_SOURCE"] = Sha;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, await stdout + await stderr, int.Parse(File.ReadAllText(calls).Trim()));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
