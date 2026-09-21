param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$before = Get-Content -LiteralPath (Join-Path $InputDirectory 'before/results.json') -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath (Join-Path $InputDirectory 'after/results.json') -Raw | ConvertFrom-Json
if ($before.ModelInvocations -ne 0 -or $after.ModelInvocations -ne 0) { throw 'Replay unexpectedly invoked a model' }
$rows = foreach ($types in @(1,5,12)) {
    foreach ($phase in @('cold','unchanged','presentation','renderer','dependency')) {
        $left = @($before.Samples | Where-Object { $_.Types -eq $types -and $_.Phase -eq $phase })
        $right = @($after.Samples | Where-Object { $_.Types -eq $types -and $_.Phase -eq $phase })
        if ($left.Count -ne 3 -or $right.Count -ne 3) { throw 'Missing samples' }
        $beforeCalls = if ($phase -in @('cold','renderer','dependency')) { $types } else { 0 }
        $afterCalls = if ($phase -in @('cold','dependency')) { $types } else { 0 }
        if (@($left | Where-Object GeneratorCalls -ne $beforeCalls).Count -ne 0 -or
            @($right | Where-Object GeneratorCalls -ne $afterCalls).Count -ne 0 -or
            @($right | Where-Object StalePresentations -ne 0).Count -ne 0) { throw 'Unexpected cache behavior' }
        if ($phase -ne 'presentation' -and @(@($left.NormalizedArtifactDigest) + @($right.NormalizedArtifactDigest) | Select-Object -Unique).Count -ne 1) { throw 'Rendered bytes differ after the documented PDF metadata normalization' }
        if ($phase -eq 'presentation' -and @($left | Where-Object StalePresentations -ne $types).Count -ne 0) { throw 'Baseline did not reproduce stale presentation' }
        [ordered]@{
            Types=$types; Phase=$phase
            BeforeBuildMedianMs=@($left.BuildMs | Sort-Object)[1]; AfterBuildMedianMs=@($right.BuildMs | Sort-Object)[1]
            BeforeGenerationReplayMedianMs=@($left.GenerationReplayMs | Sort-Object)[1]; AfterGenerationReplayMedianMs=@($right.GenerationReplayMs | Sort-Object)[1]
            BeforeValidationMedianMs=@($left.ValidationMs | Sort-Object)[1]; AfterValidationMedianMs=@($right.ValidationMs | Sort-Object)[1]
            BeforeRenderMedianMs=@($left.RenderMs | Sort-Object)[1]; AfterRenderMedianMs=@($right.RenderMs | Sort-Object)[1]
            BeforeGeneratorCalls=$beforeCalls; AfterGeneratorCalls=$afterCalls
            BeforeStalePresentations=$left[0].StalePresentations; AfterStalePresentations=$right[0].StalePresentations
        }
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $InputDirectory 'before/results.json') -Destination (Join-Path $OutputDirectory 'before.json')
Copy-Item -LiteralPath (Join-Path $InputDirectory 'after/results.json') -Destination (Join-Path $OutputDirectory 'after.json')
[ordered]@{
    BaseRevision='cf42e6c26ef8820f19e6d9e8755b70bc9916b51e'; CorpusTypes=12
    BeforeSourceHashes=$before.SourceHashes; AfterSourceHashes=$after.SourceHashes
    Boundary='Synthetic 12-type input corpus; deterministic draft replay, not inference latency; validation and rendering measured separately in temporary source copies; three samples after warmup; PDF comparison normalizes only random font-subset prefixes and XMP document/instance UUIDs'
    CloudTokenSavings=$null; TokenReason='No cloud input/output comparison'
    Rows=@($rows)
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
