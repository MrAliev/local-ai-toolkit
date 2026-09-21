param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
$ErrorActionPreference = 'Stop'
$baseline = Get-Content -Raw -LiteralPath $Before | ConvertFrom-Json
$candidate = Get-Content -Raw -LiteralPath $After | ConvertFrom-Json
if ($baseline.Rows.Count -ne 9 -or $candidate.Rows.Count -ne 18) { throw 'Incomplete matrix' }
$summary = foreach ($row in $candidate.Rows) {
    $previous = @($baseline.Rows | Where-Object { $_.Types -eq $row.Types -and $_.Revision -eq $row.Revision })
    if ($previous.Count -ne 1 -or $previous[0].ContentDigest -ne $row.ContentDigest) { throw 'Document content differs' }
    $expectedCalls = if ($row.Revision -eq 1) { $row.Types } else { 0 }
    if ($row.GeneratorCalls -ne $expectedCalls) { throw 'Text cache did not reuse' }
    $expectedResources = if ($row.Mode -eq 'default') { 1 } else { 0 }
    if ($row.ResourceFiles -ne $expectedResources -or $row.AppliedResources -ne $expectedResources) { throw 'Wrong number of shared resources' }
    [ordered]@{ types = $row.Types; revision = $row.Revision; mode = $row.Mode
        beforeBytes = $previous[0].StorageBytes; afterBytes = $row.StorageBytes
        beforeAppliedBytes = $previous[0].AppliedBytes; afterAppliedBytes = $row.AppliedBytes
        verifiedContentDigest = $row.ContentDigest }
}
$destination = Join-Path $PSScriptRoot 'evidence'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath $Before -Destination (Join-Path $destination 'before.json')
Copy-Item -LiteralPath $After -Destination (Join-Path $destination 'after.json')
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'comparison.json') -Encoding utf8
$summary | Where-Object { $_.revision -eq 1 -or ($_.types -eq 12 -and $_.revision -eq 3) } | ConvertTo-Json
