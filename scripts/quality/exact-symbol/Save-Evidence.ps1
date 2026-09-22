#requires -Version 7.4
param(
    [Parameter(Mandatory)][string] $InputFile,
    [Parameter(Mandatory)][string] $OutputFile
)
$ErrorActionPreference = 'Stop'
$run = Get-Content -LiteralPath $InputFile -Raw | ConvertFrom-Json
if ($run.schemaVersion -ne 1 -or -not $run.complete -or $run.caseCount -ne 73 -or $run.samples.Count -ne 146) {
    throw 'Incomplete pilot; this exporter targets the documented 73-case corpus'
}
if (-not $run.digestBefore -or $run.digestBefore -cne $run.digestAfter) { throw 'Model identity changed' }
$groups = @($run.samples | Group-Object id)
if ($groups.Count -ne $run.caseCount) { throw 'Case count differs' }
foreach ($group in $groups) {
    $pair = @($group.Group | Sort-Object pass)
    if ($pair.Count -ne 2 -or $pair[0].pass -ne 0 -or $pair[1].pass -ne 1 -or
        $pair[0].query -cne $pair[1].query -or $pair[0].category -cne $pair[1].category -or
        $pair[0].hybridHitsSha256 -cne $pair[1].hybridHitsSha256 -or $pair[0].decision -cne $pair[1].decision) {
        throw "Repeated result changed: $($group.Name)"
    }
}
$samples = @($run.samples | ForEach-Object {
    $sample = $_
    foreach ($field in @('prototypeMs', 'hybridMs')) {
        if (-not [double]::IsFinite($sample.$field) -or $sample.$field -lt 0) { throw 'Invalid duration' }
    }
    if (($sample.decision -eq 'unique-chunk') -ne ($sample.exact.Count -eq 1)) { throw 'Unexpected decision' }
    $exact = ConvertTo-Json -InputObject @($sample.exact) -Depth 10 -Compress
    $hybrid = ConvertTo-Json -InputObject @($sample.hybrid) -Depth 10 -Compress
    $sameFirst = $sample.exact.Count -eq 1 -and $sample.hybrid.Count -gt 0 -and
        (($sample.exact[0] | ConvertTo-Json -Compress) -ceq ($sample.hybrid[0] | ConvertTo-Json -Compress))
    if ($sample.sameFirstHit -ne $sameFirst -or $sample.sameOrderedLocations -ne ($exact -ceq $hybrid)) {
        throw 'Recorded location comparison differs from actual results'
    }
    [ordered]@{
        id = $sample.id; query = $sample.query; category = $sample.category; pass = $sample.pass
        decision = $sample.decision; matchingChunks = $sample.matchingChunks
        prototypeMs = $sample.prototypeMs; hybridMs = $sample.hybridMs
        exact = @($sample.exact); hybridFirst = $sample.hybrid[0]; hybridHitCount = $sample.hybrid.Count
        hybridHitsSha256 = $sample.hybridHitsSha256
        sameFirstHit = $sample.sameFirstHit; sameOrderedLocations = $sample.sameOrderedLocations
    }
})
$run.samples = $samples
$run | Add-Member -NotePropertyName distinctQueries -NotePropertyValue @($samples.query | Sort-Object -Unique).Count
$parent = Split-Path ([IO.Path]::GetFullPath($OutputFile)) -Parent
$null = New-Item -ItemType Directory -Force -Path $parent
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile), ($run | ConvertTo-Json -Depth 20) + "`n")
Write-Output "Verified 146 observations, stable repeated retrievals and exact/hybrid location comparisons; $($run.distinctQueries) distinct query strings."
