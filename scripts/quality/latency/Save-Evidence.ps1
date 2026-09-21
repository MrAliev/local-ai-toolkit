#requires -Version 7.4
param(
    [Parameter(Mandatory)][string] $InputDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
foreach ($name in @('baseline', 'after', 'pilot-cache')) {
    $run = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json") -Raw | ConvertFrom-Json
    $samples = @($run.measurements | ForEach-Object {
        # -InputObject preserves an empty array. Piping it would emit nothing and hash null.
        $hitsJson = ConvertTo-Json -InputObject @($_.hits) -Depth 30 -Compress
        $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($hitsJson)))
        [ordered]@{
            id = $_.id; pass = $_.pass; totalMs = $_.totalMs
            beforeEmbeddingMs = $_.beforeEmbeddingMs; embeddingMs = $_.embeddingMs
            afterEmbeddingMs = $_.afterEmbeddingMs; identityMs = $_.identityMs
            cacheHit = $_.cacheHit; queueMs = $_.queueMs; executionMs = $_.executionMs
            wasCold = $_.wasCold; embeddingsUsed = $_.embeddingsUsed; hitsSha256 = $hash
        }
    })
    $digest = @($run.tagsBefore.models | Where-Object name -eq $run.status.model)[0].digest
    $endTags = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json.tags-after.json") -Raw | ConvertFrom-Json
    $endDigest = @($endTags.models | Where-Object name -eq $run.status.model)[0].digest
    if (-not $digest -or $digest -cne $endDigest) { throw 'Model digest missing or changed' }
    $report = [ordered]@{
        implementation = $run.implementation; coreSha256 = $run.coreSha256
        brokerSha256 = $run.brokerSha256; sourceCommit = $run.sourceCommit
        sourceTree = $run.sourceTree; dirtyHash = $run.dirtyHash
        corpusSha256 = $run.corpusSha256; model = $run.status.model
        digestBefore = $digest; digestAfter = $endDigest; phases = $run.phases
        backendLoadMs = $null; backendLoadReason = $run.backendLoadReason; measurements = $samples
    }
    $target = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$name.json"))
    [IO.File]::WriteAllText($target, ($report | ConvertTo-Json -Depth 30) + "`n")
    foreach ($pass in @(0, 1)) {
        Copy-Item -LiteralPath (Join-Path $InputDirectory "$name.json.pass$pass.quality.json") `
            -Destination (Join-Path $OutputDirectory "$name.pass$pass.quality.json")
    }
}
$before = Get-Content -LiteralPath (Join-Path $OutputDirectory 'baseline.json') -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath (Join-Path $OutputDirectory 'after.json') -Raw | ConvertFrom-Json
foreach ($field in @('sourceCommit', 'sourceTree', 'dirtyHash', 'corpusSha256', 'model', 'digestBefore')) {
    if ($before.$field -cne $after.$field) { throw "Comparison identity differs: $field" }
}
if ($before.measurements.Count -ne $after.measurements.Count) { throw 'Sample count mismatch' }
for ($i = 0; $i -lt $before.measurements.Count; $i++) {
    foreach ($field in @('id', 'pass', 'embeddingsUsed', 'hitsSha256')) {
        if ($before.measurements[$i].$field -cne $after.measurements[$i].$field) {
            throw "Retrieval mismatch: $i, $field"
        }
    }
}
Write-Output "Verified $($before.measurements.Count) identical retrievals."
