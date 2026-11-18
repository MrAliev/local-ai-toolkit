using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LocalAi.ReleaseSigner.Tests;

public sealed class ReleaseAssetReceiptTests
{
    private static readonly string[] ExpectedNames =
    [
        "LocalAi.Installer.exe", "localai-package.zip", "release-manifest.json", "release-manifest.sig"
    ];

    [Fact]
    public async Task RecordsOnlyObservedPublishedBytesAndExplicitlyUnverifiedClaims()
    {
        using var fixture = new ReceiptFixture();
        Directory.CreateDirectory(fixture.OutputRoot);
        var result = await fixture.RunAsync("valid");

        Assert.True(result.ExitCode == 0, result.Stderr);
        using var summary = JsonDocument.Parse(result.Stdout);
        Assert.Equal(fixture.OutputRoot, summary.RootElement.GetProperty("outputRoot").GetString());
        Assert.Equal(Path.Combine(fixture.OutputRoot, "assets"), summary.RootElement.GetProperty("assetsPath").GetString());
        var predicatePath = Path.Combine(fixture.OutputRoot, "receipt-predicate.json");
        Assert.Equal(predicatePath, summary.RootElement.GetProperty("predicatePath").GetString());
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(predicatePath, TestContext.Current.CancellationToken));
        var predicate = document.RootElement;
        Assert.Equal("localai.release-asset-receipt.v1", predicate.GetProperty("schema").GetString());
        Assert.Equal("MrAliev/local-ai-toolkit", predicate.GetProperty("repository").GetString());
        Assert.Equal(73, predicate.GetProperty("releaseId").GetInt64());
        Assert.Equal("receipt-'; throw 'must not execute'; #", predicate.GetProperty("tag").GetString());
        Assert.Equal("publisher-claimed-target", predicate.GetProperty("declaredTarget").GetString());
        Assert.False(predicate.GetProperty("sourceBuildVerified").GetBoolean());
        Assert.False(predicate.GetProperty("authenticodeVerified").GetBoolean());
        Assert.Equal(901, predicate.GetProperty("witness").GetProperty("workflowRunId").GetInt64());
        Assert.Equal(2, predicate.GetProperty("witness").GetProperty("workflowRunAttempt").GetInt64());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", predicate.GetProperty("observedAt").GetString()!);
        Assert.Equal(4, predicate.GetProperty("assets").GetArrayLength());
        Assert.Equal(ExpectedNames.OrderBy(x => x, StringComparer.Ordinal),
            Directory.GetFiles(Path.Combine(fixture.OutputRoot, "assets")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var asset in predicate.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString()!;
            var bytes = Encoding.UTF8.GetBytes("receipt-fixture:" + name);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.OutputRoot, "assets", name), TestContext.Current.CancellationToken));
            Assert.Equal(Array.IndexOf(ExpectedNames, name) + 101, asset.GetProperty("id").GetInt64());
            Assert.Equal(bytes.Length, asset.GetProperty("size").GetInt64());
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(sha256, asset.GetProperty("sha256").GetString());
            Assert.Equal("sha256:" + sha256, asset.GetProperty("apiDigest").GetString());
        }
        Assert.Equal(4, File.ReadAllLines(fixture.DownloadLog).Length);
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".localai-*-*"));
    }

    [Theory]
    [InlineData("missing", "exactly the four expected assets")]
    [InlineData("extra", "exactly the four expected assets")]
    [InlineData("traversal", "unsafe, or duplicate asset name")]
    [InlineData("backslash", "unsafe, or duplicate asset name")]
    [InlineData("duplicate", "unsafe, or duplicate asset name")]
    [InlineData("caseMismatch", "unsafe, or duplicate asset name")]
    [InlineData("invalidId", "invalid asset ID")]
    [InlineData("invalidSize", "invalid asset size")]
    [InlineData("invalidDigest", "invalid asset digest")]
    [InlineData("digestWithNewline", "invalid asset digest")]
    [InlineData("foreignAssetUrl", "asset API URL outside")]
    [InlineData("foreignReleaseUrl", "release API URL outside")]
    [InlineData("draft", "must be published")]
    public async Task RejectsInvalidMetadataBeforeDownloadingOrWritingOutput(string scenario, string message)
    {
        using var fixture = new ReceiptFixture();
        Directory.CreateDirectory(fixture.OutputRoot);
        var result = await fixture.RunAsync(scenario);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.DownloadLog));
        Assert.True(Directory.Exists(fixture.OutputRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".localai-*-*"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.bin")));
    }

    [Theory]
    [InlineData("hashMismatch", "API SHA256 digest")]
    [InlineData("sizeMismatch", "API size")]
    [InlineData("replacement", "changed during observation")]
    [InlineData("retagged", "changed during observation")]
    public async Task RejectsChangedBytesOrSnapshotWithoutChangingExistingEmptyOutput(string scenario, string message)
    {
        using var fixture = new ReceiptFixture();
        Directory.CreateDirectory(fixture.OutputRoot);
        var result = await fixture.RunAsync(scenario);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.OutputRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".localai-*-*"));
    }

    [Fact]
    public async Task HashesAssetsEvenWhenTheApiDoesNotSupplyDigests()
    {
        using var fixture = new ReceiptFixture();
        var result = await fixture.RunAsync("noDigest");
        Assert.True(result.ExitCode == 0, result.Stderr);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixture.OutputRoot, "receipt-predicate.json"), TestContext.Current.CancellationToken));
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, asset.GetProperty("apiDigest").ValueKind);
            Assert.Matches("^[0-9a-f]{64}$", asset.GetProperty("sha256").GetString()!);
        }
    }

    [Theory]
    [InlineData("deserializedDates")]
    [InlineData("deserializedOffsetDates")]
    public async Task AcceptsTimestampsProducedByPowerShellJsonDeserialization(string scenario)
    {
        using var fixture = new ReceiptFixture();
        var result = await fixture.RunAsync(scenario);
        Assert.True(result.ExitCode == 0, result.Stderr);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixture.OutputRoot, "receipt-predicate.json"), TestContext.Current.CancellationToken));
        Assert.Equal("2026-11-17T12:01:00.0000000Z", document.RootElement.GetProperty("publishedAt").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public async Task PreservesExistingNonemptyOutput()
    {
        using var fixture = new ReceiptFixture();
        Directory.CreateDirectory(fixture.OutputRoot);
        var sentinel = Path.Combine(fixture.OutputRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "preserve me", TestContext.Current.CancellationToken);

        var result = await fixture.RunAsync("valid");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("new or empty", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
        Assert.False(File.Exists(fixture.DownloadLog));
    }

    [Fact]
    public async Task DoesNotExposeCredentialsOrSignedRedirectUrlsInDownloadErrors()
    {
        using var fixture = new ReceiptFixture();
        var result = await fixture.RunAsync("downloadFailure");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("asset download failed", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-secret-token", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-secret", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    private sealed class ReceiptFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalAi.ReleaseAssetReceiptTests", Guid.NewGuid().ToString("N"));
        public string OutputRoot => Path.Combine(Root, "observed");
        public string DownloadLog => Path.Combine(Root, "downloads.txt");
        private string HarnessPath => Path.Combine(Root, "harness.ps1");

        public ReceiptFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(HarnessPath, Harness, new UTF8Encoding(false));
        }

        public async Task<ProcessResult> RunAsync(string scenario)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Root
            };
            foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-File", HarnessPath,
                         "-ScriptPath", FindReceiptScript(), "-OutputRoot", OutputRoot,
                         "-Scenario", scenario, "-DownloadLog", DownloadLog })
                start.ArgumentList.Add(value);
            start.Environment["GH_TOKEN"] = "fixture-secret-token";
            start.Environment["GITHUB_RUN_ID"] = "901";
            start.Environment["GITHUB_RUN_ATTEMPT"] = "2";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new ProcessResult(process.ExitCode, await stdout, await stderr);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }

        private static string FindReceiptScript([CallerFilePath] string sourceFile = "")
        {
            foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    var candidate = Path.Combine(directory.FullName, "scripts", "record-localai-release-receipt.ps1");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            throw new FileNotFoundException("Could not locate the release receipt script from the test checkout.");
        }

        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalAi.ReleaseAssetReceiptTests"));
            var actualParent = Path.GetDirectoryName(Path.GetFullPath(Root));
            if (!string.Equals(expectedParent, actualParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the test fixture directory.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private const string Harness = """
            param([string]$ScriptPath, [string]$OutputRoot, [string]$Scenario, [string]$DownloadLog)
            $utf8 = [Text.UTF8Encoding]::new($false)
            [Console]::InputEncoding = $utf8
            [Console]::OutputEncoding = $utf8
            $OutputEncoding = $utf8
            $ErrorActionPreference = 'Stop'
            # The receipt runs in its own script scope. State is explicit per test process,
            # so mock functions never accidentally bind to the receipt's script scope.
            $global:receiptReleaseReads = 0
            $global:receiptAssetReads = 0
            $global:receiptPayloads = @{}
            $global:receiptNames = @('LocalAi.Installer.exe', 'localai-package.zip', 'release-manifest.json', 'release-manifest.sig')
            $global:receiptApi = 'https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/73'
            $global:receiptScenario = $Scenario
            $global:receiptDownloadLog = $DownloadLog
            foreach ($name in $global:receiptNames) { $global:receiptPayloads[$name] = [Text.Encoding]::UTF8.GetBytes('receipt-fixture:' + $name) }

            function Convert-MockMetadata {
                param([object]$Value)
                if ($global:receiptScenario -eq 'deserializedDates') {
                    $parsed = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 10)
                    $sample = if ($parsed -is [array]) { $parsed[0].created_at } else { $parsed.published_at }
                    if ($sample -isnot [DateTime]) { throw 'Expected real JSON deserialization to produce DateTime.' }
                    return ,$parsed
                }
                if ($global:receiptScenario -eq 'deserializedOffsetDates') {
                    $parsed = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 10) -DateKind Offset
                    $sample = if ($parsed -is [array]) { $parsed[0].created_at } else { $parsed.published_at }
                    if ($sample -isnot [DateTimeOffset]) { throw 'Expected real JSON deserialization to produce DateTimeOffset.' }
                    return ,$parsed
                }
                return ,$Value
            }

            function Invoke-RestMethod {
                [CmdletBinding()]
                param([string]$Uri, [hashtable]$Headers, [int]$MaximumRedirection)
                if ($MaximumRedirection -ne 0) { throw 'API metadata redirects must be disabled.' }
                if ($Headers.Authorization -cne 'Bearer fixture-secret-token') { throw 'Expected test authorization header.' }
                if ($Uri -ceq $global:receiptApi) {
                    $global:receiptReleaseReads++
                    $metadata = [pscustomobject]@{
                        id = [long]73; url = $(if ($global:receiptScenario -eq 'foreignReleaseUrl') { 'https://example.invalid/73' } else { $global:receiptApi })
                        assets_url = "$global:receiptApi/assets"; draft = ($global:receiptScenario -eq 'draft'); prerelease = $false
                        tag_name = $(if ($global:receiptScenario -eq 'retagged' -and $global:receiptReleaseReads -gt 1) { 'changed-tag' } else { "receipt-'; throw 'must not execute'; #" })
                        target_commitish = 'publisher-claimed-target'
                        created_at = '2026-11-17T12:00:00Z'; published_at = '2026-11-17T12:01:00Z'; updated_at = '2026-11-17T12:01:00Z'
                    }
                    return Convert-MockMetadata $metadata
                }
                if ($Uri -cne "$global:receiptApi/assets?per_page=100&page=1") { throw 'Unexpected API URL; no network is allowed.' }
                $global:receiptAssetReads++
                $assets = @(
                    for ($index = 0; $index -lt $global:receiptNames.Count; $index++) {
                        $name = $global:receiptNames[$index]
                        $id = [long](101 + $index)
                        if ($global:receiptScenario -eq 'replacement' -and $global:receiptAssetReads -gt 1 -and $index -eq 0) { $id = [long]201 }
                        [pscustomobject]@{
                            id = $id; name = $name; size = [long]$global:receiptPayloads[$name].Length
                            url = "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/assets/$id"
                            browser_download_url = 'https://example.invalid/do-not-use'; state = 'uploaded'
                            digest = $(if ($global:receiptScenario -eq 'noDigest') { $null } else { 'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($global:receiptPayloads[$name])).ToLowerInvariant() })
                            created_at = '2026-11-17T12:00:00Z'; updated_at = '2026-11-17T12:01:00Z'
                        }
                    }
                )
                switch ($global:receiptScenario) {
                    'missing' { $assets = @($assets[0..2]) }
                    'extra' { $assets += [pscustomobject]@{ name = 'unexpected.bin' } }
                    'traversal' { $assets[3].name = '../escaped.bin' }
                    'backslash' { $assets[3].name = '..\escaped.bin' }
                    'duplicate' { $assets[3] = $assets[0] }
                    'caseMismatch' { $assets[0].name = 'localai.installer.exe' }
                    'invalidId' { $assets[0].id = '101' }
                    'invalidSize' { $assets[0].size = [long]-1 }
                    'invalidDigest' { $assets[0].digest = 'sha256:xyz' }
                    'digestWithNewline' { $assets[0].digest += "`n" }
                    'foreignAssetUrl' { $assets[0].url = 'https://example.invalid/asset' }
                }
                return Convert-MockMetadata $assets
            }

            function Invoke-WebRequest {
                [CmdletBinding()]
                param([string]$Uri, [hashtable]$Headers, [string]$OutFile, [int]$MaximumRedirection, [switch]$PreserveAuthorizationOnRedirect)
                if ($Headers.Accept -cne 'application/octet-stream') { throw 'Expected a binary API asset request.' }
                if ($PreserveAuthorizationOnRedirect) { throw 'Authorization must never be preserved on redirects.' }
                if ($global:receiptScenario -eq 'downloadFailure') { throw 'fixture-secret-token https://release-assets.githubusercontent.com/signed-secret' }
                $index = -1
                for ($candidate = 0; $candidate -lt $global:receiptNames.Count; $candidate++) {
                    if ($Uri -ceq "https://api.github.com/repos/MrAliev/local-ai-toolkit/releases/assets/$(101 + $candidate)") { $index = $candidate }
                }
                if ($index -lt 0) { throw 'Unexpected asset URL; no network is allowed.' }
                $name = $global:receiptNames[$index]
                [byte[]]$bytes = $global:receiptPayloads[$name].Clone()
                if ($global:receiptScenario -eq 'hashMismatch' -and $index -eq 0) { $bytes[0] = $bytes[0] -bxor 1 }
                if ($global:receiptScenario -eq 'sizeMismatch' -and $index -eq 0) { $bytes = [byte[]]($bytes + [byte]0) }
                [IO.File]::AppendAllText($global:receiptDownloadLog, $name + [Environment]::NewLine)
                [IO.File]::WriteAllBytes($OutFile, $bytes)
            }

            & $ScriptPath -ReleaseId 73 -OutputRoot $OutputRoot
            """;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
