[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z')]
    [string]$Version,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
$repository = 'MrAliev/local-ai-toolkit'
if ($env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_EVENT_NAME -cne 'push' -or
    $env:GITHUB_REF -cne 'refs/heads/releases') {
    throw 'Publication requires the release branch workflow after merge.'
}

$names = @('LocalAi.Installer.exe', 'localai-package.zip', 'release-manifest.json', 'release-manifest.sig')
$assets = foreach ($name in $names) {
    $file = Get-Item -LiteralPath (Join-Path $ArtifactRoot $name)
    if ($file.PSIsContainer -or $file.Length -le 0) { throw "Missing release file: $name" }
    [pscustomobject]@{
        Name = $name
        Path = $file.FullName
        Size = $file.Length
        Digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$tag = "refs/tags/$Version"
function Test-ReleaseTag {
    $refs = @(& git ls-remote --tags "https://github.com/$repository.git" $tag "${tag}^{}")
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the release tag.' }
    if ($refs.Count -eq 0) { return $false }

    $target = @($refs | Where-Object { $_.EndsWith("`t${tag}^{}") })
    if ($target.Count -eq 0) { $target = @($refs | Where-Object { $_.EndsWith("`t$tag") }) }
    if ($target.Count -ne 1 -or ($target[0] -split '\s+')[0] -cne $SourceCommit) {
        throw 'The release tag does not reference the requested main commit.'
    }
    return $true
}

[void](Test-ReleaseTag)
$prerelease = $Version.Contains('-')
$endpoint = "repos/$repository/releases"
$request = @{
    tag_name = $Version
    target_commitish = $SourceCommit
    name = "Local AI $Version"
    draft = $true
    prerelease = $prerelease
    generate_release_notes = $true
}
$draftJson = $request | ConvertTo-Json -Compress | & gh api --method POST $endpoint --input -
if ($LASTEXITCODE -ne 0) { throw 'Could not create a new release draft.' }
$draft = $draftJson | ConvertFrom-Json
if (-not $draft.draft -or $draft.tag_name -cne $Version -or $draft.target_commitish -cne $SourceCommit -or $draft.id -le 0) {
    throw 'The created draft does not match the release request.'
}

$uploadUri = "https://uploads.github.com/repos/$repository/releases/$($draft.id)/assets"
if ($draft.upload_url -cne ($uploadUri + '{?name,label}')) {
    throw 'The draft upload endpoint does not match its release ID.'
}
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
foreach ($asset in $assets) {
    $uri = $uploadUri + '?name=' + [Uri]::EscapeDataString($asset.Name)
    Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/octet-stream' `
        -InFile $asset.Path -MaximumRedirection 0 -TimeoutSec 600 | Out-Null
}
$uploaded = & gh api "$endpoint/$($draft.id)/assets?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Could not verify uploaded release files.' }
$uploaded = @($uploaded | ConvertFrom-Json)
if ($uploaded.Count -ne $assets.Count) { throw 'The draft has an unexpected number of files.' }
foreach ($asset in $assets) {
    $match = @($uploaded | Where-Object { $_.name -ceq $asset.Name })
    if ($match.Count -ne 1 -or $match[0].state -cne 'uploaded' -or
        $match[0].size -ne $asset.Size -or $match[0].digest -cne $asset.Digest) {
        throw "Uploaded bytes do not match the verified $($asset.Name)."
    }
}

if (-not (Test-ReleaseTag)) {
    @{ref = $tag; sha = $SourceCommit} | ConvertTo-Json -Compress |
        & gh api --method POST "repos/$repository/git/refs" --input - --silent
    if ($LASTEXITCODE -ne 0) { throw 'Could not create a tag at the requested main commit.' }
}
if (-not (Test-ReleaseTag)) { throw 'The verified release tag is not visible.' }
$publish = @{draft = $false; make_latest = $(if ($prerelease) {'false'} else {'true'})}
$publishedJson = $publish | ConvertTo-Json -Compress | & gh api --method PATCH "$endpoint/$($draft.id)" --input -
if ($LASTEXITCODE -ne 0) { throw 'GitHub did not publish the verified draft.' }
$published = $publishedJson | ConvertFrom-Json
if ($published.draft -or $published.id -ne $draft.id -or $published.prerelease -ne $prerelease) {
    throw 'GitHub returned an unexpected publication state.'
}
if (-not (Test-ReleaseTag)) { throw 'The published release tag is not visible.' }
"release_id=$($published.id)" >> $env:GITHUB_OUTPUT
Write-Output $published.html_url
