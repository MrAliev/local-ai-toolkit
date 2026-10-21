param(
    [Parameter(Mandatory = $true)]
    [string] $ReleaseVersion
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# No source-ref, command, output-path or signing arguments are accepted. The workflow
# checks out its own main SHA, and this second check fails before any build starts.
if ($env:GITHUB_REPOSITORY -cne 'MrAliev/local-ai-toolkit' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_SHA -cnotmatch '^[0-9a-f]{40}$' -or
    $env:GITHUB_RUN_ID -cnotmatch '^[1-9][0-9]*$' -or
    $env:GITHUB_RUN_ATTEMPT -cnotmatch '^[1-9][0-9]*$') {
    throw 'An unsigned candidate must run in the trusted main workflow context.'
}
$head = [string](& git -C $root rev-parse --verify HEAD)
if ($LASTEXITCODE -ne 0 -or $head.Trim() -cne $env:GITHUB_SHA) {
    throw 'The checked out commit must match the trusted main workflow SHA.'
}

# SemVer without build metadata, with numeric components usable in Windows file
# versions. Input remains a value passed to dotnet, never executable shell text.
$identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$versionPattern = '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})(?:-' + $identifier + '(?:\.' + $identifier + ')*)?$'
if ($ReleaseVersion.Length -gt 100 -or $ReleaseVersion -cnotmatch $versionPattern) {
    throw 'ReleaseVersion must be a numeric three-part version with an optional SemVer prerelease.'
}
$numericVersion = ($ReleaseVersion -split '-', 2)[0]
if (@($numericVersion.Split('.') | Where-Object { [int]$_ -gt 65534 }).Count -gt 0) {
    throw 'ReleaseVersion numeric components must not exceed 65534.'
}

$publishParent = [IO.Path]::GetFullPath((Join-Path $root 'publish'))
$candidateRoot = [IO.Path]::GetFullPath((Join-Path $publishParent 'unsigned-candidate'))
if (-not $candidateRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate output must stay inside the source workspace.'
}
if (Test-Path -LiteralPath $publishParent) {
    if ((Get-Item -LiteralPath $publishParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The candidate output parent must not be a reparse point.'
    }
}
if (Test-Path -LiteralPath $candidateRoot) {
    throw 'The candidate output already exists; use a fresh checkout. Existing output is never removed.'
}
New-Item -ItemType Directory -Path $candidateRoot | Out-Null

# Only these apphosts belong to this project. Loose native libraries and any other
# outputs stay in build/, outside the artifact submitted to any future signer.
$projects = [ordered]@{
    'CodeSearch.Cli' = 'codesearch.exe'
    'CodeSearch.Mcp' = 'codesearch-mcp.exe'
    'LocalLm.Mcp' = 'locallm-mcp.exe'
    'LocalAi.Cli' = 'localai.exe'
    'LocalAi.Launcher' = 'localai-launcher.exe'
    'LocalAi.Broker' = 'LocalAi.Broker.exe'
    'LocalAi.Installer' = 'LocalAi.Installer.exe'
}
$buildRoot = Join-Path $candidateRoot 'build'
foreach ($entry in $projects.GetEnumerator()) {
    $project = Join-Path $root ('src/' + $entry.Key + '/' + $entry.Key + '.csproj')
    $output = Join-Path $buildRoot $entry.Key
    Write-Host ('Building unsigned candidate: ' + $entry.Key)
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' `
        '-p:BuildInParallel=false' '-p:UseSharedCompilation=false' '-p:ContinuousIntegrationBuild=true' `
        "-p:Version=$ReleaseVersion" "-p:VersionPrefix=$numericVersion" `
        "-p:AssemblyVersion=$numericVersion.0" "-p:FileVersion=$numericVersion.0" `
        "-p:InformationalVersion=$ReleaseVersion" -o $output
    if ($LASTEXITCODE -ne 0) { throw ('Candidate build failed: ' + $entry.Key + ', exit ' + $LASTEXITCODE) }
}

# Publish the artifact directory only after every output has passed validation.
$stagedArtifact = Join-Path $candidateRoot 'staged-artifact'
$artifact = Join-Path $candidateRoot 'artifact'
$ownPe = Join-Path $stagedArtifact 'own-pe'
New-Item -ItemType Directory -Path $ownPe | Out-Null
$files = @()
foreach ($entry in $projects.GetEnumerator()) {
    $source = Join-Path (Join-Path $buildRoot $entry.Key) $entry.Value
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw ('Required own candidate binary is missing: ' + $entry.Value)
    }
    if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw ('An own candidate binary must not be a reparse point: ' + $entry.Value)
    }
    if ((Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'NotSigned') {
        throw ('Expected a new unsigned candidate binary: ' + $entry.Value)
    }
    $destination = Join-Path $ownPe $entry.Value
    Copy-Item -LiteralPath $source -Destination $destination
    $files += [ordered]@{
        path = 'own-pe/' + $entry.Value
        size = (Get-Item -LiteralPath $destination).Length
        sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$metadata = [ordered]@{
    schema = 'localai.unsigned-candidate.v1'
    signed = $false
    version = $ReleaseVersion
    repository = 'https://github.com/MrAliev/local-ai-toolkit'
    sourceCommit = $env:GITHUB_SHA
    workflowRun = 'https://github.com/MrAliev/local-ai-toolkit/actions/runs/' + $env:GITHUB_RUN_ID
    runAttempt = [long]$env:GITHUB_RUN_ATTEMPT
    files = $files
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagedArtifact 'unsigned-candidate.json') -Encoding utf8
# Both absolute operands are fixed children of the checked, newly created workspace
# output above; no caller-supplied path is moved or recursively deleted.
Move-Item -LiteralPath $stagedArtifact -Destination $artifact
Write-Host ('Unsigned candidate built: ' + $artifact + '. No installable release was signed or published.')
