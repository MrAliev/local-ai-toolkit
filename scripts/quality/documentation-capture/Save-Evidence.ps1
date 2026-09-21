param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$beforePath = Join-Path $InputDirectory 'before-run/results.json'
$afterPath = Join-Path $InputDirectory 'after-run/results.json'
$before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
if ($before.ModelInvocations -ne 0 -or $after.ModelInvocations -ne 0) { throw 'Unexpected model work' }
$rows = foreach ($limit in @(1,5,[int]::MaxValue)) {
    $left = @($before.Samples | Where-Object Limit -eq $limit)
    $right = @($after.Samples | Where-Object Limit -eq $limit)
    if ($left.Count -ne 3 -or $right.Count -ne 3 -or
        @(@($left.UnitsSha256) + @($right.UnitsSha256) | Select-Object -Unique).Count -ne 1) {
        throw "Missing samples or different documentation inputs at limit $limit"
    }
    [ordered]@{
        Limit=$limit; Units=$left[0].Units
        BeforeCaptureMedianMs=@($left.CaptureMs | Sort-Object)[1]
        AfterCaptureMedianMs=@($right.CaptureMs | Sort-Object)[1]
        BeforeAllocatedMedianBytes=@($left.AllocatedBytes | Sort-Object)[1]
        AfterAllocatedMedianBytes=@($right.AllocatedBytes | Sort-Object)[1]
        BeforeSourceReads=$left[0].SourceReads; AfterSourceReads=$right[0].SourceReads
        BeforeSyntaxParses=$left[0].SyntaxParses; AfterSyntaxParses=$right[0].SyntaxParses
        UnitsSha256=$left[0].UnitsSha256
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $beforePath -Destination (Join-Path $OutputDirectory 'before.json')
Copy-Item -LiteralPath $afterPath -Destination (Join-Path $OutputDirectory 'after.json')
[ordered]@{
    BaseRevision='f636319a5ba55bc2f6ff8e9bc62c4864c3d79ff4'
    BeforeSourceHashes=$before.SourceHashes; AfterSourceHashes=$after.SourceHashes
    SemanticDocuments=865; CorpusChunks=10877; CorpusBytes=190908768
    Note='Frozen corpus and real index; warm filesystem; warmup then three samples per limit; before group then after group; temporary-source counters'
    CloudTokenSavings=$null; TokenReason='No cloud input/output comparison in this Capture experiment'
    Rows=@($rows)
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
