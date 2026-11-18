#Requires -Version 7.0
<#
.SYNOPSIS
Verifies an unsigned candidate's complete file inventory and GitHub provenance.
.DESCRIPTION
No candidate executable is run. Success verifies the manifest attestation and the
seven manifest-bound files; it does not verify Authenticode or an installed runtime.
The gh application on PATH is part of the verifier's trusted local environment.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ArtifactRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSource,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$ExpectedRunId,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$ExpectedRunAttempt,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$EvidencePath,
    [ValidatePattern('^MrAliev/local-ai-toolkit/\.github/workflows/[A-Za-z0-9_.-]+\.ya?ml$')]
    [string]$ExpectedSignerWorkflow = 'MrAliev/local-ai-toolkit/.github/workflows/build-unsigned-candidate.yml'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$utf8 = [Text.UTF8Encoding]::new($false, $true)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            Assert-Condition (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse points are forbidden: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-UniqueJsonProperties([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            Assert-Condition ($seen.Add($property.Name)) "Duplicate or case-aliased JSON property: $($property.Name)"
            Assert-UniqueJsonProperties $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonProperties $item }
    }
}

function Assert-ObjectShape([Text.Json.JsonElement]$Element, [string[]]$Names) {
    Assert-Condition ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'Expected a JSON object.'
    $actual = @($Element.EnumerateObject() | ForEach-Object Name)
    Assert-Condition ($actual.Count -eq $Names.Count) 'Unexpected JSON object properties.'
    foreach ($name in $Names) {
        Assert-Condition ($actual -ccontains $name) "Missing canonical JSON property: $name"
    }
}

function Get-JsonString([Text.Json.JsonElement]$Element, [string]$Name) {
    $value = $Element.GetProperty($Name)
    Assert-Condition ($value.ValueKind -eq [Text.Json.JsonValueKind]::String) "Expected string property: $Name"
    return $value.GetString()
}

function Get-JsonInteger([Text.Json.JsonElement]$Element, [string]$Name) {
    $value = $Element.GetProperty($Name)
    [long]$number = 0
    Assert-Condition ($value.ValueKind -eq [Text.Json.JsonValueKind]::Number -and $value.TryGetInt64([ref]$number)) "Expected integer property: $Name"
    return $number
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $Stream.Position = 0 }
}

$requiredNames = @('codesearch.exe', 'codesearch-mcp.exe', 'locallm-mcp.exe', 'localai.exe', 'localai-launcher.exe', 'LocalAi.Broker.exe', 'LocalAi.Installer.exe')
$repository = 'MrAliev/local-ai-toolkit'
$predicateType = 'https://slsa.dev/provenance/v1'
$streams = [Collections.Generic.List[IO.FileStream]]::new()
$manifestDocument = $null
$verificationDocument = $null
$stderrPath = $null
$pendingEvidencePath = $null

try {
    Assert-Condition ($ExpectedSource -cmatch '^[0-9a-f]{40}$') 'ExpectedSource must be a lowercase Git commit ID.'
    $root = [IO.Path]::GetFullPath($ArtifactRoot)
    if ($root.Length -gt [IO.Path]::GetPathRoot($root).Length) { $root = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) }
    Assert-Condition ([IO.Directory]::Exists($root)) 'ArtifactRoot must be an existing directory.'
    Assert-NoReparseAncestors $root
    $evidence = [IO.Path]::GetFullPath($EvidencePath)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-Condition (-not $evidence.StartsWith($rootPrefix, $comparison) -and -not $evidence.Equals($root, $comparison)) 'EvidencePath must be outside ArtifactRoot.'
    $evidenceParent = [IO.Path]::GetDirectoryName($evidence)
    Assert-Condition ([IO.Directory]::Exists($evidenceParent)) 'EvidencePath parent directory must already exist.'
    Assert-NoReparseAncestors $evidence
    Assert-Condition (-not [IO.File]::Exists($evidence) -and -not [IO.Directory]::Exists($evidence)) 'EvidencePath must not already exist.'

    $rootEntries = @([IO.DirectoryInfo]::new($root).GetFileSystemInfos())
    Assert-Condition ($rootEntries.Count -eq 2) 'ArtifactRoot must contain only unsigned-candidate.json and own-pe.'
    foreach ($entry in $rootEntries) {
        Assert-Condition (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Artifact entries must not be reparse points.'
        Assert-Condition ($entry.Name -ceq 'unsigned-candidate.json' -or $entry.Name -ceq 'own-pe') "Unexpected artifact entry: $($entry.Name)"
    }
    $manifestPath = [IO.Path]::Combine($root, 'unsigned-candidate.json')
    $ownPePath = [IO.Path]::Combine($root, 'own-pe')
    Assert-Condition ([IO.File]::Exists($manifestPath) -and [IO.Directory]::Exists($ownPePath)) 'Expected manifest file and own-pe directory.'
    $ownEntries = @([IO.DirectoryInfo]::new($ownPePath).GetFileSystemInfos())
    Assert-Condition ($ownEntries.Count -eq $requiredNames.Count) 'own-pe must contain exactly seven files.'
    foreach ($entry in $ownEntries) {
        Assert-Condition (($entry.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -eq 0) 'own-pe entries must be regular files without reparse points.'
        Assert-Condition ($requiredNames -ccontains $entry.Name) "Unexpected or case-aliased own-pe file: $($entry.Name)"
    }

    # Keep read-only handles open until evidence is committed. On Windows this also
    # denies replacement, deletion, and writing while gh verifies the manifest.
    $manifestStream = [IO.File]::Open($manifestPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $streams.Add($manifestStream)
    Assert-Condition ($manifestStream.Length -gt 0 -and $manifestStream.Length -le 131072) 'Manifest size is outside the allowed bounds.'
    $manifestDocument = [Text.Json.JsonDocument]::Parse($manifestStream)
    $manifest = $manifestDocument.RootElement
    Assert-UniqueJsonProperties $manifest
    Assert-ObjectShape $manifest @('schema', 'signed', 'version', 'repository', 'sourceCommit', 'sourceTree', 'sourceRef', 'sourceClean', 'workflow', 'workflowRun', 'runId', 'runAttempt', 'runtimeIdentifier', 'files')
    Assert-Condition ((Get-JsonString $manifest 'schema') -ceq 'localai.unsigned-candidate.v2') 'Unsupported candidate manifest schema.'
    Assert-Condition ($manifest.GetProperty('signed').ValueKind -eq [Text.Json.JsonValueKind]::False) 'Candidate must explicitly be unsigned.'
    Assert-Condition ((Get-JsonString $manifest 'version') -ceq $ExpectedVersion) 'Candidate version does not match the expected version.'
    Assert-Condition ((Get-JsonString $manifest 'repository') -ceq "https://github.com/$repository") 'Candidate repository does not match.'
    Assert-Condition ((Get-JsonString $manifest 'sourceCommit') -ceq $ExpectedSource) 'Candidate source does not match the expected commit.'
    Assert-Condition ((Get-JsonString $manifest 'sourceTree') -cmatch '^[0-9a-f]{40}$') 'Candidate source tree must be a lowercase Git object ID.'
    Assert-Condition ((Get-JsonString $manifest 'sourceRef') -ceq 'refs/heads/main') 'Candidate source ref must be main.'
    Assert-Condition ($manifest.GetProperty('sourceClean').ValueKind -eq [Text.Json.JsonValueKind]::True) 'Candidate source must be clean.'
    Assert-Condition ((Get-JsonString $manifest 'workflow') -ceq "$repository/.github/workflows/build-unsigned-candidate.yml@refs/heads/main") 'Candidate producer workflow does not match.'
    $runText = $ExpectedRunId.ToString([Globalization.CultureInfo]::InvariantCulture)
    Assert-Condition ((Get-JsonString $manifest 'workflowRun') -ceq "https://github.com/$repository/actions/runs/$runText") 'Candidate workflow run URL does not match.'
    Assert-Condition ((Get-JsonInteger $manifest 'runId') -eq $ExpectedRunId) 'Candidate run ID does not match.'
    Assert-Condition ((Get-JsonInteger $manifest 'runAttempt') -eq $ExpectedRunAttempt) 'Candidate run attempt does not match.'
    Assert-Condition ((Get-JsonString $manifest 'runtimeIdentifier') -ceq 'win-x64') 'Candidate runtime identifier does not match.'
    $fileElements = $manifest.GetProperty('files')
    Assert-Condition ($fileElements.ValueKind -eq [Text.Json.JsonValueKind]::Array -and $fileElements.GetArrayLength() -eq $requiredNames.Count) 'Manifest must list exactly seven files.'
    $seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $checkedFiles = [Collections.Generic.List[object]]::new()
    foreach ($file in $fileElements.EnumerateArray()) {
        Assert-ObjectShape $file @('path', 'size', 'sha256')
        $relative = Get-JsonString $file 'path'
        Assert-Condition (-not [IO.Path]::IsPathRooted($relative) -and $relative -cnotmatch '(^|[\\/])\.\.?([\\/]|$)' -and -not $relative.Contains('\') -and -not $relative.Contains(':')) 'Rooted, traversal, or alternate path syntax is forbidden.'
        Assert-Condition ($seenPaths.Add($relative)) 'Manifest contains duplicate or case-aliased file paths.'
        Assert-Condition (@($requiredNames | ForEach-Object { 'own-pe/' + $_ }) -ccontains $relative) "Unexpected manifest path: $relative"
        $size = Get-JsonInteger $file 'size'
        Assert-Condition ($size -gt 0) 'Candidate files must not be empty.'
        $expectedHash = Get-JsonString $file 'sha256'
        Assert-Condition ($expectedHash -cmatch '^[0-9a-f]{64}$') 'Candidate SHA256 must be lowercase hexadecimal.'
        $filePath = [IO.Path]::Combine($root, $relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        Assert-NoReparseAncestors $filePath
        $stream = [IO.File]::Open($filePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $streams.Add($stream)
        Assert-Condition ($stream.Length -eq $size) "Candidate file size does not match: $relative"
        $actualHash = Get-StreamSha256 $stream
        Assert-Condition ($actualHash -ceq $expectedHash) "Candidate file hash does not match: $relative"
        $checkedFiles.Add([ordered]@{ path = $relative; size = $size; sha256 = $actualHash })
    }
    $manifestHash = Get-StreamSha256 $manifestStream

    # Resolve only external applications, excluding a function or alias named gh.
    $gh = Get-Command gh -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $ghArguments = @('attestation', 'verify', $manifestPath, '--hostname', 'github.com', '--repo', $repository,
        '--signer-workflow', $ExpectedSignerWorkflow, '--source-digest', $ExpectedSource,
        '--signer-digest', $ExpectedSource, '--source-ref', 'refs/heads/main',
        '--predicate-type', $predicateType, '--deny-self-hosted-runners', '--format', 'json')
    $stderrPath = [IO.Path]::GetTempFileName()
    $verificationOutput = @(& $gh.Source @ghArguments 2> $stderrPath)
    $ghExitCode = $LASTEXITCODE
    Assert-Condition ($ghExitCode -eq 0) "GitHub attestation verification failed (exit $ghExitCode). No candidate executable was run."
    $verificationJson = $verificationOutput -join "`n"
    Assert-Condition ($verificationJson.Length -gt 0 -and $verificationJson.Length -le 8388608) 'GitHub attestation verification returned empty or oversized evidence.'
    $verificationDocument = [Text.Json.JsonDocument]::Parse($verificationJson)
    $results = $verificationDocument.RootElement
    Assert-UniqueJsonProperties $results
    Assert-Condition ($results.ValueKind -eq [Text.Json.JsonValueKind]::Array -and $results.GetArrayLength() -gt 0 -and $results.GetArrayLength() -le 30) 'GitHub attestation verification must return a nonempty bounded result array.'
    foreach ($result in $results.EnumerateArray()) {
        Assert-Condition ($result.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'Invalid attestation result.'
        $attestation = $result.GetProperty('attestation')
        Assert-Condition ($attestation.ValueKind -eq [Text.Json.JsonValueKind]::Object -and @($attestation.EnumerateObject()).Count -gt 0) 'Missing verified attestation bundle.'
        $verification = $result.GetProperty('verificationResult')
        Assert-Condition ($verification.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'Missing verification result.'
        $certificate = $verification.GetProperty('signature').GetProperty('certificate')
        Assert-Condition ($certificate.ValueKind -eq [Text.Json.JsonValueKind]::Object -and @($certificate.EnumerateObject()).Count -gt 0) 'Missing verified signing certificate.'
        $timestamps = $verification.GetProperty('verifiedTimestamps')
        Assert-Condition ($timestamps.ValueKind -eq [Text.Json.JsonValueKind]::Array -and $timestamps.GetArrayLength() -gt 0) 'Missing verified timestamps.'
        $statement = $verification.GetProperty('statement')
        Assert-Condition ((Get-JsonString $statement 'predicateType') -ceq $predicateType) 'Verified predicate type does not match.'
        $subjects = $statement.GetProperty('subject')
        Assert-Condition ($subjects.ValueKind -eq [Text.Json.JsonValueKind]::Array -and $subjects.GetArrayLength() -gt 0) 'Missing verified subjects.'
        $matched = $false
        foreach ($subject in $subjects.EnumerateArray()) {
            $digest = $subject.GetProperty('digest')
            [Text.Json.JsonElement]$sha256 = [Text.Json.JsonElement]::new()
            if ($digest.ValueKind -eq [Text.Json.JsonValueKind]::Object -and $digest.TryGetProperty('sha256', [ref]$sha256) -and $sha256.ValueKind -eq [Text.Json.JsonValueKind]::String -and $sha256.GetString() -ceq $manifestHash) { $matched = $true }
        }
        Assert-Condition $matched 'Verified attestation does not name this manifest SHA256.'
    }

    # Recheck bytes and inventory after the external verifier, including platforms
    # whose filesystem does not enforce Windows sharing semantics.
    Assert-Condition ((Get-StreamSha256 $manifestStream) -ceq $manifestHash) 'Manifest changed during verification.'
    for ($index = 0; $index -lt $checkedFiles.Count; $index++) {
        Assert-Condition ((Get-StreamSha256 $streams[$index + 1]) -ceq $checkedFiles[$index].sha256) 'Candidate bytes changed during verification.'
    }
    $finalRootEntries = @([IO.DirectoryInfo]::new($root).GetFileSystemInfos())
    $finalOwnEntries = @([IO.DirectoryInfo]::new($ownPePath).GetFileSystemInfos())
    Assert-Condition ($finalRootEntries.Count -eq 2 -and $finalOwnEntries.Count -eq 7) 'Artifact inventory changed during verification.'
    foreach ($entry in $finalRootEntries + $finalOwnEntries) {
        Assert-Condition (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Artifact became a reparse point during verification.'
    }
    foreach ($entry in $finalRootEntries) { Assert-Condition ($entry.Name -ceq 'unsigned-candidate.json' -or $entry.Name -ceq 'own-pe') 'Artifact root inventory changed during verification.' }
    foreach ($entry in $finalOwnEntries) { Assert-Condition ($requiredNames -ccontains $entry.Name -and ($entry.Attributes -band [IO.FileAttributes]::Directory) -eq 0) 'Artifact inventory changed during verification.' }
    Assert-NoReparseAncestors $root
    Assert-NoReparseAncestors $ownPePath
    Assert-NoReparseAncestors $evidenceParent

    $evidenceObject = [ordered]@{
        schema = 'localai.unsigned-candidate-verification.v1'
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        repository = "https://github.com/$repository"
        sourceCommit = $ExpectedSource
        version = $ExpectedVersion
        runId = $ExpectedRunId
        runAttempt = $ExpectedRunAttempt
        manifestSha256 = $manifestHash
        attestationVerified = $true
        verifiedAttestationCount = $results.GetArrayLength()
        policy = [ordered]@{ hostname = 'github.com'; signerWorkflow = $ExpectedSignerWorkflow; sourceDigest = $ExpectedSource; signerDigest = $ExpectedSource; sourceRef = 'refs/heads/main'; predicateType = $predicateType; denySelfHostedRunners = $true }
        files = @($checkedFiles.ToArray())
        authenticodeVerified = $false
        installedRuntimeVerified = $false
        scope = 'Manifest provenance and candidate bytes verified. Authenticode and installed runtime were not verified. No candidate executable was run.'
    }
    $pendingEvidencePath = [IO.Path]::Combine($evidenceParent, '.' + [IO.Path]::GetFileName($evidence) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $evidenceStream = [IO.File]::Open($pendingEvidencePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = $utf8.GetBytes(($evidenceObject | ConvertTo-Json -Depth 8) + "`n")
        $evidenceStream.Write($bytes, 0, $bytes.Length)
        $evidenceStream.Flush($true)
    }
    finally { $evidenceStream.Dispose() }
    [IO.File]::Move($pendingEvidencePath, $evidence, $false)
    $pendingEvidencePath = $null
    Write-Output "Verified unsigned candidate $ExpectedVersion at $ExpectedSource. Evidence: $evidence"
    Write-Output 'Authenticode and installed runtime were not verified. No candidate executable was run.'
}
finally {
    if ($verificationDocument) { $verificationDocument.Dispose() }
    if ($manifestDocument) { $manifestDocument.Dispose() }
    foreach ($stream in $streams) { $stream.Dispose() }
    if ($stderrPath -and [IO.File]::Exists($stderrPath)) { [IO.File]::Delete($stderrPath) }
    if ($pendingEvidencePath -and [IO.File]::Exists($pendingEvidencePath)) { [IO.File]::Delete($pendingEvidencePath) }
}
