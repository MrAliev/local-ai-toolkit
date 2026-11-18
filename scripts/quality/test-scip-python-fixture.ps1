param([Parameter(Mandatory)][string]$FixturePrefix)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This boundary check requires Windows junctions.' }
$helper = Join-Path $PSScriptRoot 'prepare-scip-python-fixture.cs'
$fixture = [IO.Path]::GetFullPath($FixturePrefix)
$bundle = Join-Path $fixture 'node_modules/@sourcegraph/scip-python/dist/scip-python.js'
$before = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
$scratch = [IO.Directory]::CreateTempSubdirectory('localai-scip-boundary-')
$linkedModules = Join-Path $scratch.FullName 'direct/scip-python-fixture/node_modules'
$linkedAncestor = Join-Path $scratch.FullName 'ancestor'

function Assert-LinkedFixtureRefused([string]$Prefix) {
    # The native failure is the expected result, even when the caller opts in to
    # converting native exit codes into terminating PowerShell errors.
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & dotnet run --file $helper --configuration Release -- $Prefix 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or $output -notmatch 'Refusing a linked path') {
        throw 'The helper did not explicitly reject the linked fixture.'
    }
    if ((Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash -ne $before) {
        throw 'A rejected fixture changed the referenced bundle.'
    }
}

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $linkedModules) -Force | Out-Null
    New-Item -ItemType Junction -Path $linkedModules -Target (Join-Path $fixture 'node_modules') | Out-Null
    Assert-LinkedFixtureRefused (Split-Path -Parent $linkedModules)
    New-Item -ItemType Junction -Path $linkedAncestor -Target (Split-Path -Parent $fixture) | Out-Null
    Assert-LinkedFixtureRefused (Join-Path $linkedAncestor 'scip-python-fixture')
    Write-Output 'Passed: linked node_modules and linked ancestor rejected; bundle unchanged.'
}
finally {
    $owned = [IO.Path]::GetFullPath($scratch.FullName)
    if ($owned -ne $scratch.FullName -or -not $scratch.Name.StartsWith('localai-scip-boundary-') -or
        ($scratch.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing cleanup outside the owned boundary-test directory.'
    }
    foreach ($link in @($linkedModules, $linkedAncestor)) {
        if (-not [IO.Path]::GetFullPath($link).StartsWith($owned + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unexpected boundary-test link path.'
        }
        $entry = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        if ($null -ne $entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            [IO.Directory]::Delete($link) # Remove the junction itself without traversing its target.
        }
    }
    [IO.Directory]::Delete($owned, $true)
}
exit 0
