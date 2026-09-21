param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Revision,
    [Parameter(Mandatory)][string]$Destination,
    [string]$SourceRepository
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Probe destination must be new' }
New-Item -ItemType Directory -Path $Destination | Out-Null
git -C $Repository archive --format=zip --output (Join-Path $Destination 'source.zip') $Revision
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
Expand-Archive -LiteralPath (Join-Path $Destination 'source.zip') -DestinationPath (Join-Path $Destination 'source')
$files = @('DocumentationPackStore.cs','DocumentationRenderer.cs','DocumentationHtmlResources.cs')
if ($SourceRepository) {
    foreach ($relative in $files) {
        Copy-Item -LiteralPath (Join-Path $SourceRepository "src/LocalAi.Documentation/$relative") -Destination (Join-Path $Destination "source/src/LocalAi.Documentation/$relative")
    }
}
$hashes = [ordered]@{}
foreach ($relative in $files) {
    $path = Join-Path $Destination "source/src/LocalAi.Documentation/$relative"
    if (Test-Path -LiteralPath $path) { $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'source-hashes.json') -Encoding utf8
$probe = Join-Path $Destination 'probe'
New-Item -ItemType Directory -Path $probe | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'DocumentationResourcesProbe.csproj') -Destination $probe
dotnet build (Join-Path $probe 'DocumentationResourcesProbe.csproj') --configuration Release *> (Join-Path $Destination 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Probe build failed; see $Destination/build.log" }
Write-Output (Join-Path $probe 'bin/Release/net10.0/DocumentationResourcesProbe.exe')
