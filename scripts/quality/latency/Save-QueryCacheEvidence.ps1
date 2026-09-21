#requires -Version 7.4
param(
    [Parameter(Mandatory)][string] $InputDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$baseline = $null
foreach ($name in @('baseline-1', 'candidate-1', 'candidate-2', 'baseline-2')) {
    $run = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json") -Raw | ConvertFrom-Json
    $end = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json.tags-after.json") -Raw | ConvertFrom-Json
    $digest = @($run.tagsBefore.models | Where-Object name -eq $run.status.model)[0].digest
    $endDigest = @($end.models | Where-Object name -eq $run.status.model)[0].digest
    if (-not $digest -or $digest -cne $endDigest -or $run.dirtyHash) { throw "Unstable identity: $name" }
    $samples = @($run.measurements | ForEach-Object {
        $hitJson = ConvertTo-Json -InputObject @($_.hits) -Depth 30 -Compress
        [ordered]@{
            id = $_.id; pass = $_.pass; totalMs = $_.totalMs
            beforeEmbeddingMs = $_.beforeEmbeddingMs; embeddingMs = $_.embeddingMs
            afterEmbeddingMs = $_.afterEmbeddingMs; queueMs = $_.queueMs; executionMs = $_.executionMs
            embeddingsUsed = $_.embeddingsUsed
            hitsSha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hitJson)))
        }
    })
    if ($samples.Count -ne 68) { throw "Incomplete run: $name" }
    $report = [ordered]@{
        implementation = $run.implementation; coreSha256 = $run.coreSha256
        brokerSha256 = $run.brokerSha256; brokerAssemblySha256 = $run.brokerAssemblySha256
        brokerCoreSha256 = $run.brokerCoreSha256; sourceCommit = $run.sourceCommit
        sourceTree = $run.sourceTree; corpusSha256 = $run.corpusSha256; model = $run.status.model
        digestBefore = $digest; digestAfter = $endDigest; phases = $run.phases
        backendLoadMs = $null; backendLoadReason = $run.backendLoadReason; measurements = $samples
    }
    if ($null -eq $baseline) { $baseline = $report }
    foreach ($field in @('sourceCommit', 'sourceTree', 'corpusSha256', 'model', 'digestBefore', 'coreSha256')) {
        if ($baseline[$field] -cne $report[$field]) { throw "Comparison identity differs: $name, $field" }
    }
    for ($i = 0; $i -lt 68; $i++) {
        foreach ($field in @('id', 'pass', 'embeddingsUsed', 'hitsSha256')) {
            if ($baseline.measurements[$i][$field] -cne $samples[$i][$field]) { throw "Retrieval mismatch: $name, $i, $field" }
        }
    }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $OutputDirectory "$name.json")), ($report | ConvertTo-Json -Depth 30) + "`n")
    foreach ($pass in @(0, 1)) {
        Copy-Item -LiteralPath (Join-Path $InputDirectory "$name.json.pass$pass.quality.json") -Destination (Join-Path $OutputDirectory "$name.pass$pass.quality.json")
    }
}
Write-Output 'Verified all 272 retrievals against the baseline, with unchanged source, corpus, model and client identities.'
