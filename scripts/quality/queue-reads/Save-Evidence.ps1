param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$beforePath = Join-Path $InputDirectory 'before/results.json'
$afterPath = Join-Path $InputDirectory 'after/results.json'
$before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
$contentionPath = Join-Path $InputDirectory 'contention.json'
$contention = Get-Content -LiteralPath $contentionPath -Raw | ConvertFrom-Json
if ($before.ModelInvocations -ne 0 -or $after.ModelInvocations -ne 0 -or
    $before.ImageBytes -ne $after.ImageBytes -or $contention.ActiveLeases -ne 1 -or
    $contention.UniqueJobs -ne 50) { throw 'Probe invariants failed' }
$rows = foreach ($kind in @('text','image')) {
    foreach ($count in @(1,10,100)) {
        foreach ($operation in @('list','enqueue-join')) {
            $left = @($before.Samples | Where-Object { $_.Kind -eq $kind -and $_.Jobs -eq $count -and $_.Operation -eq $operation })
            $right = @($after.Samples | Where-Object { $_.Kind -eq $kind -and $_.Jobs -eq $count -and $_.Operation -eq $operation })
            if ($left.Count -ne 3 -or $right.Count -ne 3 -or
                @($right | Where-Object { $_.RequestReads -ne $count -or $_.RequestBytes -le 0 }).Count -ne 0) {
                throw 'Missing samples or unexpected request-read accounting'
            }
            [ordered]@{
                Kind=$kind; Jobs=$count; Operation=$operation
                BeforeMutexMedianMs=@($left.MutexHeldMs | Sort-Object)[1]
                AfterMutexMedianMs=@($right.MutexHeldMs | Sort-Object)[1]
                BeforeAllocatedMedianBytes=@($left.AllocatedBytes | Sort-Object)[1]
                AfterAllocatedMedianBytes=@($right.AllocatedBytes | Sort-Object)[1]
                BeforeRequestReads=$left[0].RequestReads; AfterRequestReads=$right[0].RequestReads
                BeforeRequestBytes=$left[0].RequestBytes; AfterRequestBytes=$right[0].RequestBytes
            }
        }
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $beforePath -Destination (Join-Path $OutputDirectory 'before.json')
Copy-Item -LiteralPath $afterPath -Destination (Join-Path $OutputDirectory 'after.json')
Copy-Item -LiteralPath $contentionPath -Destination (Join-Path $OutputDirectory 'contention.json')
[ordered]@{
    BaseRevision='ddc16b583a1bb4daaf81868e1e5a62c46578d7d6'
    BeforeSourceSha256=$before.SourceSha256; AfterSourceSha256=$after.SourceSha256
    Note='Warm filesystem; three samples per operation; temporary-source instrumentation; no inference; managed allocations are not peak memory'
    CloudTokenSavings=$null; TokenReason='No cloud input/output comparison in this queue experiment'
    Rows=@($rows)
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
