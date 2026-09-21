param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$samples = foreach ($name in @('before-1','after-1','after-2','before-2','before-3','after-3')) {
    $path = Join-Path $InputDirectory "$name.json"
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    [pscustomobject]@{Name=$name; Path=$path; Result=$result}
}
$reference = $samples[0].Result
foreach ($sample in $samples) {
    $result = $sample.Result
    foreach ($field in @('State','Previous','Next','BaseChunks','Model','ActiveHits')) {
        if (($result.$field | ConvertTo-Json -Depth 10 -Compress) -cne
            ($reference.$field | ConvertTo-Json -Depth 10 -Compress)) {
            throw "Different $field in $($sample.Name)"
        }
    }
    if ((@($result.OtherHits | Select-Object -First 3) | ConvertTo-Json -Compress) -cne
        (@($reference.OtherHits | Select-Object -First 3) | ConvertTo-Json -Compress)) {
        throw 'Other worktree target hits differ'
    }
    if ($result.ModelBefore.digest -ne $reference.ModelBefore.digest -or
        $result.ModelAfter.digest -ne $reference.ModelBefore.digest) { throw 'Model identity changed' }
    $expected = if ($sample.Name.StartsWith('before')) { $result.Next } else { $result.Previous }
    if ($result.GlobalGenerationAtReadiness -ne $expected) { throw 'Unexpected pointer at readiness' }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($sample in $samples) { Copy-Item -LiteralPath $sample.Path -Destination $OutputDirectory }
$summary = [ordered]@{
    Baseline = $reference.Implementation
    Scenario = 'Warm prebuilt base; active linked worktree; one 96-chunk overlay missing; same shared broker'
    Order = @($samples.Name)
    ModelDigest = $reference.ModelBefore.digest
    CloudTokenSavings = $null
    TokenReason = 'Timing experiment; no cloud input/output comparison'
    Retrieval = 'Active top 5 identical; other top 3 identical. Other fifth hit varies in both groups after re-embedding; all raw results retained.'
    Groups = @{}
}
foreach ($group in @('before','after')) {
    $values = @($samples | Where-Object { $_.Name.StartsWith($group) } | ForEach-Object { $_.Result })
    $summary.Groups[$group] = [ordered]@{
        ActiveReadyMedianMs = @($values.ActiveReadyMs | Sort-Object)[1]
        FullMaintenanceMedianMs = @($values.FullMaintenanceMs | Sort-Object)[1]
        CliSha256 = @($values.CliSha256 | Sort-Object -Unique)
        CoreSha256 = @($values.CoreSha256 | Sort-Object -Unique)
    }
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 10
