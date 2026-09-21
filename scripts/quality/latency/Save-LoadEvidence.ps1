#requires -Version 7.4
param(
    [Parameter(Mandatory)][string] $InputDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$baseline = $null
$verified = 0
foreach ($name in @('sequential-1', 'concurrent-1', 'concurrent-2', 'sequential-2', 'cold')) {
    $run = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json") -Raw | ConvertFrom-Json
    $end = Get-Content -LiteralPath (Join-Path $InputDirectory "$name.json.tags-after.json") -Raw | ConvertFrom-Json
    $digest = @($run.tagsBefore.models | Where-Object name -eq $run.status.model)[0].digest
    $endDigest = @($end.models | Where-Object name -eq $run.status.model)[0].digest
    if (-not $digest -or $digest -cne $endDigest -or $run.dirtyHash) { throw "Unstable identity: $name" }
    $expectedMode = if ($name.StartsWith('concurrent')) { 'concurrent' } elseif ($name -eq 'cold') { 'cold' } else { 'sequential' }
    if ($run.mode -cne $expectedMode) { throw "Wrong mode: $name" }
    $expectedCount = if ($name -eq 'cold') { 12 } else { 68 }
    if ($run.measurements.Count -ne $expectedCount) { throw "Incomplete run: $name" }
    foreach ($pass in @(0, 1)) {
        if (@($run.measurements | Where-Object pass -eq $pass).Count -ne $expectedCount / 2) { throw "Incomplete pass: $name, $pass" }
    }
    $firstIds = @($run.measurements | Where-Object pass -eq 0 | ForEach-Object id | Sort-Object)
    $repeatIds = @($run.measurements | Where-Object pass -eq 1 | ForEach-Object id | Sort-Object)
    if (($firstIds | ConvertTo-Json -Compress) -cne ($repeatIds | ConvertTo-Json -Compress)) { throw "Mismatched passes: $name" }
    $samples = @($run.measurements | ForEach-Object {
        if (-not $_.embeddingsUsed -or $_.backendTiming.schemaVersion -ne 1 -or $_.backendTiming.jobId -cne $_.jobId) {
            throw "Degraded search or missing/misattributed timing: $name, $($_.id)"
        }
        if ($_.pass -notin @(0, 1)) { throw 'Unexpected pass' }
        $timing = $_.backendTiming.backend
        if ($timing.cacheHit -ne ($_.pass -eq 1)) { throw 'Unexpected cache observation' }
        if ($_.pass -eq 0 -and ($null -eq $timing.totalNanoseconds -or $null -eq $timing.loadNanoseconds)) { throw 'Backend timing missing' }
        if ($_.pass -eq 1 -and ($null -ne $timing.totalNanoseconds -or $null -ne $timing.loadNanoseconds -or
            $null -ne $timing.promptEvalNanoseconds -or $null -ne $timing.promptTokens)) { throw 'Cache hit replayed backend work' }
        if ($name -eq 'cold') {
            if ($null -eq $_.residencyBefore -or $_.residencyBefore.modelPresent -ne ($_.pass -eq 1)) { throw 'Unproven cold/warm condition' }
            if ($_.pass -eq 1 -and ($_.residencyBefore.digest -cne $digest -or $_.residencyBefore.sizeVramBytes -le 0)) { throw 'Warm residency identity differs' }
        }
        foreach ($field in @('startedAtMs', 'totalMs', 'beforeEmbeddingMs', 'embeddingMs', 'afterEmbeddingMs', 'queueMs', 'executionMs')) {
            if ($null -eq $_.$field -or -not [double]::IsFinite($_.$field) -or $_.$field -lt 0) { throw "Invalid measurement: $field" }
        }
        $hitJson = ConvertTo-Json -InputObject @($_.hits) -Depth 30 -Compress
        [ordered]@{
            id = $_.id; pass = $_.pass; startedAtMs = $_.startedAtMs; totalMs = $_.totalMs
            beforeEmbeddingMs = $_.beforeEmbeddingMs; embeddingMs = $_.embeddingMs
            afterEmbeddingMs = $_.afterEmbeddingMs; queueMs = $_.queueMs; executionMs = $_.executionMs
            wasCold = $_.wasCold; residencyBefore = $_.residencyBefore
            embeddingsUsed = $_.embeddingsUsed; backendTiming = $_.backendTiming
            hitsSha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hitJson)))
        }
    })
    $maximumOverlap = 0
    foreach ($sample in $samples) {
        $overlap = @($samples | Where-Object { $_.startedAtMs -le $sample.startedAtMs -and ($_.startedAtMs + $_.totalMs) -gt $sample.startedAtMs }).Count
        $maximumOverlap = [Math]::Max($maximumOverlap, $overlap)
    }
    if ($maximumOverlap -gt $run.concurrency -or ($run.mode -eq 'concurrent' -and $maximumOverlap -lt 2)) { throw 'Requested concurrency was not observed' }
    $report = [ordered]@{
        implementation = $run.implementation; mode = $run.mode; concurrency = $run.concurrency; maximumObservedOverlap = $maximumOverlap
        policy = $run.policy; probeSha256 = $run.probeSha256
        coreSha256 = $run.coreSha256; brokerSha256 = $run.brokerSha256
        brokerAssemblySha256 = $run.brokerAssemblySha256; brokerCoreSha256 = $run.brokerCoreSha256
        sourceCommit = $run.sourceCommit; sourceTree = $run.sourceTree; corpusSha256 = $run.corpusSha256
        generationId = Split-Path (Split-Path $run.status.indexPath -Parent) -Leaf
        indexedFiles = $run.status.fileCount; indexedChunks = $run.status.chunkCount
        model = $run.status.model; digestBefore = $digest; digestAfter = $endDigest
        phases = $run.phases; backendTimingSource = $run.backendTimingSource; measurements = $samples
    }
    if ($null -eq $baseline) { $baseline = $report }
    if (($baseline.policy | ConvertTo-Json -Compress) -cne ($report.policy | ConvertTo-Json -Compress)) { throw 'Policy differs' }
    foreach ($field in @('sourceCommit', 'sourceTree', 'corpusSha256', 'generationId', 'indexedFiles', 'indexedChunks', 'model', 'digestBefore', 'probeSha256', 'coreSha256', 'brokerSha256', 'brokerAssemblySha256', 'brokerCoreSha256')) {
        if ($baseline[$field] -cne $report[$field]) { throw "Comparison identity differs: $name, $field" }
    }
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($sample in $samples) {
        if (-not $seen.Add("$($sample.pass):$($sample.id)")) { throw 'Duplicate observation' }
        $reference = @($baseline.measurements | Where-Object { $_.id -ceq $sample.id -and $_.pass -eq 0 })
        if ($reference.Count -ne 1 -or $reference[0].hitsSha256 -cne $sample.hitsSha256) { throw "Retrieval mismatch: $name, $($sample.id)" }
        $verified++
    }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $OutputDirectory "$name.json")), ($report | ConvertTo-Json -Depth 30) + "`n")
    foreach ($pass in @(0, 1)) {
        Copy-Item -LiteralPath (Join-Path $InputDirectory "$name.json.pass$pass.quality.json") -Destination (Join-Path $OutputDirectory "$name.pass$pass.quality.json")
    }
}
Write-Output "Verified $verified identical retrievals, stable identities, cold/warm residency and observed concurrency."
