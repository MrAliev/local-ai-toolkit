[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RequestPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$document = [Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $RequestPath -Raw))
try {
    $request = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        throw 'The release request must be a JSON object.'
    }
    foreach ($property in $document.RootElement.EnumerateObject()) {
        if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            -not $request.TryAdd($property.Name, $property.Value.GetString())) {
            throw 'Release request fields must be unique strings.'
        }
    }
    if ($request.Count -ne 2 -or -not $request.ContainsKey('version') -or -not $request.ContainsKey('sourceCommit')) {
        throw 'The release request must contain version and sourceCommit strings.'
    }
}
finally {
    $document.Dispose()
}

$version = $request['version']
$sourceCommit = $request['sourceCommit']
$identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$versionPattern = '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})(?:-' +
    $identifier + '(?:\.' + $identifier + ')*)?\z'
if ($version.Length -gt 100 -or $version -cnotmatch $versionPattern) {
    throw 'The release version must use a version such as 0.3.0 or 0.3.0-rc.1.'
}
if (@(($version -split '-', 2)[0].Split('.') | Where-Object { [int]$_ -gt 65534 }).Count -gt 0) {
    throw 'Release version numeric parts must not exceed 65534.'
}
if ($sourceCommit -cnotmatch '\A[0-9a-f]{40}\z') {
    throw 'The release request must name a full lowercase source commit SHA.'
}

$mainCommit = & git -C $repository rev-parse --verify 'refs/remotes/origin/main^{commit}'
if ($LASTEXITCODE -ne 0) {
    throw 'The main branch is unavailable. Fetch the repository history before checking the request.'
}
& git -C $repository merge-base --is-ancestor $sourceCommit $mainCommit
if ($LASTEXITCODE -ne 0) {
    throw 'The requested commit must already be present in main.'
}

[ordered]@{
    version = $version
    sourceCommit = $sourceCommit
} | ConvertTo-Json -Compress
