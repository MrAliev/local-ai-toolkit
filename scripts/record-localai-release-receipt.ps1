# Records bytes observed after publication. This is not build provenance or a signature.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [long]$ReleaseId,
    [string]$Repository = 'MrAliev/local-ai-toolkit',
    [string]$OutputRoot = 'publish/release-asset-receipt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$expectedNames = @('LocalAi.Installer.exe', 'localai-package.zip', 'release-manifest.json', 'release-manifest.sig')
$fixedRepository = 'MrAliev/local-ai-toolkit'
if ($ReleaseId -le 0) { throw 'ReleaseId must be a positive integer.' }
if (-not [string]::Equals($Repository, $fixedRepository, [StringComparison]::Ordinal)) {
    throw 'Only the MrAliev/local-ai-toolkit repository is supported.'
}
$releaseApi = "https://api.github.com/repos/$fixedRepository/releases/$ReleaseId"
$assetsApi = "$releaseApi/assets"
$headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'LocalAi-release-asset-receipt'
}
if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { $headers.Authorization = "Bearer $env:GH_TOKEN" }

function Get-Field {
    param([object]$Value, [string]$Name)
    if ($null -eq $Value) { throw 'GitHub returned missing metadata.' }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-PositiveInteger {
    param([object]$Value, [string]$Label)
    $isInteger = $Value -is [byte] -or $Value -is [sbyte] -or $Value -is [short] -or
        $Value -is [ushort] -or $Value -is [int] -or $Value -is [uint] -or
        $Value -is [long] -or $Value -is [ulong]
    [long]$number = 0
    if (-not $isInteger -or -not [long]::TryParse([string]$Value, [ref]$number) -or $number -le 0) {
        throw "GitHub returned an invalid $Label."
    }
    return $number
}

function Get-MetadataText {
    param([object]$Value, [string]$Label)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt 2048 -or $Value -match '[\x00-\x1f\x7f]') {
        throw "GitHub returned an invalid $Label."
    }
    return $Value
}

function Get-MetadataTimestamp {
    param([object]$Value, [string]$Label, [switch]$Optional)
    if ($Optional -and $null -eq $Value) { return $null }
    [DateTimeOffset]$parsed = [DateTimeOffset]::MinValue
    if ($Value -is [DateTimeOffset]) {
        $parsed = $Value
    }
    elseif ($Value -is [DateTime]) {
        # Invoke-RestMethod can deserialize GitHub's ISO timestamps automatically.
        # An unspecified DateTime has lost the timezone evidence and is not accepted.
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { throw "GitHub returned an invalid $Label." }
        $parsed = [DateTimeOffset]$Value
    }
    elseif ($Value -is [string]) {
        $timestamp = Get-MetadataText $Value $Label
        if ($timestamp -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z\z' -or
            -not [DateTimeOffset]::TryParse($timestamp, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$parsed)) {
            throw "GitHub returned an invalid $Label."
        }
    }
    else {
        throw "GitHub returned an invalid $Label."
    }
    return $parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Snapshot {
    try {
        # Do not follow API redirects with the credential-bearing metadata request.
        $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers -MaximumRedirection 0 -ErrorAction Stop
        $assetResponse = Invoke-RestMethod -Uri "$assetsApi`?per_page=100&page=1" -Headers $headers -MaximumRedirection 0 -ErrorAction Stop
        $rawAssets = @($assetResponse)
    }
    catch { throw 'GitHub release metadata request failed.' }

    $id = Get-PositiveInteger (Get-Field $release 'id') 'release ID'
    if ($id -ne $ReleaseId) { throw 'GitHub returned a different release ID.' }
    $draft = Get-Field $release 'draft'
    $prerelease = Get-Field $release 'prerelease'
    if ($draft -isnot [bool] -or $draft -or $prerelease -isnot [bool]) {
        throw 'The release must be published and have valid publication metadata.'
    }
    if (-not [string]::Equals((Get-Field $release 'url'), $releaseApi, [StringComparison]::Ordinal) -or
        -not [string]::Equals((Get-Field $release 'assets_url'), $assetsApi, [StringComparison]::Ordinal)) {
        throw 'GitHub returned a release API URL outside the expected repository.'
    }
    if ($rawAssets.Count -ne $expectedNames.Count) { throw 'The release must contain exactly the four expected assets.' }

    $byName = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $ids = [Collections.Generic.HashSet[long]]::new()
    foreach ($asset in $rawAssets) {
        $name = Get-Field $asset 'name'
        # Accept only literal, case-sensitive names; no path ever comes from metadata.
        if ($name -isnot [string] -or -not ($expectedNames -ccontains $name) -or $byName.ContainsKey($name)) {
            throw 'GitHub returned an unexpected, unsafe, or duplicate asset name.'
        }
        $assetId = Get-PositiveInteger (Get-Field $asset 'id') 'asset ID'
        $size = Get-PositiveInteger (Get-Field $asset 'size') 'asset size'
        if (-not $ids.Add($assetId)) { throw 'GitHub returned duplicate asset IDs.' }
        $assetApi = "https://api.github.com/repos/$fixedRepository/releases/assets/$assetId"
        if (-not [string]::Equals((Get-Field $asset 'url'), $assetApi, [StringComparison]::Ordinal)) {
            throw 'GitHub returned an asset API URL outside the expected repository.'
        }
        if (-not [string]::Equals((Get-Field $asset 'state'), 'uploaded', [StringComparison]::Ordinal)) {
            throw 'Every release asset must be completely uploaded.'
        }
        $digest = Get-Field $asset 'digest'
        if ($null -ne $digest) {
            if ($digest -isnot [string] -or $digest -cnotmatch '^sha256:[0-9a-fA-F]{64}\z') {
                throw 'GitHub returned an invalid asset digest.'
            }
            $digest = $digest.ToLowerInvariant()
        }
        $byName.Add($name, [ordered]@{
            id = $assetId; name = $name; size = $size; apiDigest = $digest; apiUrl = $assetApi
            createdAt = Get-MetadataTimestamp (Get-Field $asset 'created_at') 'asset creation timestamp'
            updatedAt = Get-MetadataTimestamp (Get-Field $asset 'updated_at') 'asset update timestamp'
        })
    }
    $assets = @($expectedNames | ForEach-Object { $byName[$_] })
    return [ordered]@{
        id = $id
        tag = Get-MetadataText (Get-Field $release 'tag_name') 'release tag'
        declaredTarget = Get-MetadataText (Get-Field $release 'target_commitish') 'declared release target'
        publishedAt = Get-MetadataTimestamp (Get-Field $release 'published_at') 'publication timestamp'
        createdAt = Get-MetadataTimestamp (Get-Field $release 'created_at') 'release creation timestamp'
        updatedAt = Get-MetadataTimestamp (Get-Field $release 'updated_at') 'release update timestamp' -Optional
        prerelease = $prerelease
        assets = $assets
    }
}

function Assert-SafeDirectoryPath {
    param([string]$Path)
    $current = $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { $attributes = $null }
        catch [IO.DirectoryNotFoundException] { $attributes = $null }
        if ($null -ne $attributes) {
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
                throw 'The output path must contain only ordinary directories, without links or reparse points.'
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}

function Assert-EmptyOutput {
    param([string]$Path)
    Assert-SafeDirectoryPath $Path
    if ([IO.Directory]::Exists($Path) -and [IO.Directory]::EnumerateFileSystemEntries($Path).GetEnumerator().MoveNext()) {
        throw 'The output directory must be new or empty.'
    }
}

function Remove-OwnedStagingDirectory {
    param([string]$Path)
    # No recursive deletion, and no traversal of a substituted link, including on failure.
    try {
        Assert-SafeDirectoryPath $Path
        $assetDirectory = [IO.Path]::Combine($Path, 'assets')
        Assert-SafeDirectoryPath $assetDirectory
        foreach ($name in $expectedNames) { [IO.File]::Delete([IO.Path]::Combine($assetDirectory, $name)) }
        [IO.File]::Delete([IO.Path]::Combine($Path, 'receipt-predicate.json'))
        if ([IO.Directory]::Exists($assetDirectory)) { [IO.Directory]::Delete($assetDirectory, $false) }
        if ([IO.Directory]::Exists($Path)) { [IO.Directory]::Delete($Path, $false) }
    }
    catch { } # Preserve any unexpected content rather than expanding cleanup's scope.
}

function Get-WitnessNumber {
    param([string]$Value, [string]$Label)
    if ([string]::IsNullOrEmpty($Value)) { return $null }
    [long]$number = 0
    if ($Value -cnotmatch '^[1-9][0-9]*\z' -or -not [long]::TryParse($Value, [ref]$number)) {
        throw "The $Label environment variable is invalid."
    }
    return $number
}

if ([string]::IsNullOrWhiteSpace($OutputRoot) -or $OutputRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'A local output directory is required.'
}
if ((Get-Location).Provider.Name -ne 'FileSystem') { throw 'A filesystem working directory is required.' }
$outputPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputRoot, (Get-Location).ProviderPath))
$parentPath = [IO.Directory]::GetParent($outputPath)
if ($null -eq $parentPath) { throw 'The filesystem root cannot be used as the output directory.' }
Assert-EmptyOutput $outputPath
$workflowRunId = Get-WitnessNumber $env:GITHUB_RUN_ID 'GITHUB_RUN_ID'
$workflowRunAttempt = Get-WitnessNumber $env:GITHUB_RUN_ATTEMPT 'GITHUB_RUN_ATTEMPT'
$snapshot = Get-Snapshot
$snapshotJson = ConvertTo-Json -InputObject $snapshot -Depth 10 -Compress

Assert-SafeDirectoryPath $parentPath.FullName
[void][IO.Directory]::CreateDirectory($parentPath.FullName)
Assert-SafeDirectoryPath $parentPath.FullName
$stagePath = [IO.Path]::Combine($parentPath.FullName, '.localai-release-receipt-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Directory]::Exists($stagePath) -or [IO.File]::Exists($stagePath)) { throw 'The staging path already exists.' }
[void][IO.Directory]::CreateDirectory($stagePath)
$stageAssets = [IO.Path]::Combine($stagePath, 'assets')
[void][IO.Directory]::CreateDirectory($stageAssets)
$promoted = $false
try {
    $observations = @()
    foreach ($asset in $snapshot.assets) {
        Assert-SafeDirectoryPath $stageAssets
        $destination = [IO.Path]::Combine($stageAssets, $asset.name)
        $downloadHeaders = $headers.Clone()
        $downloadHeaders.Accept = 'application/octet-stream'
        try {
            # PowerShell strips Authorization on redirects; never opt into preserving it.
            # Only the fixed API asset-ID URL is passed, never browser_download_url or a tag expression.
            [void](Invoke-WebRequest -Uri $asset.apiUrl -Headers $downloadHeaders -OutFile $destination -MaximumRedirection 5 -PreserveAuthorizationOnRedirect:$false -ErrorAction Stop)
        }
        catch { throw 'A GitHub release asset download failed.' }
        Assert-SafeDirectoryPath $stageAssets
        $attributes = [IO.File]::GetAttributes($destination)
        if (($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -ne 0) {
            throw 'A downloaded asset is not an ordinary file.'
        }
        $stream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($stream.Length -ne $asset.size) { throw 'A downloaded asset does not match its API size.' }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($algorithm.ComputeHash($stream)).ToLowerInvariant() }
            finally { $algorithm.Dispose() }
        }
        finally { $stream.Dispose() }
        if ($null -ne $asset.apiDigest -and -not [string]::Equals('sha256:' + $hash, $asset.apiDigest, [StringComparison]::Ordinal)) {
            throw 'A downloaded asset does not match its API SHA256 digest.'
        }
        $observations += [ordered]@{ id = $asset.id; name = $asset.name; size = $asset.size; sha256 = $hash; apiDigest = $asset.apiDigest }
    }

    $after = Get-Snapshot
    if (-not [string]::Equals($snapshotJson, (ConvertTo-Json -InputObject $after -Depth 10 -Compress), [StringComparison]::Ordinal)) {
        throw 'The published release or its assets changed during observation.'
    }
    $predicate = [ordered]@{
        schema = 'localai.release-asset-receipt.v1'
        repository = $fixedRepository
        releaseId = $snapshot.id
        tag = $snapshot.tag
        # This is the publisher's claim, not a verified build-source identity.
        declaredTarget = $snapshot.declaredTarget
        publishedAt = $snapshot.publishedAt
        assets = $observations
        witness = [ordered]@{ workflowRunId = $workflowRunId; workflowRunAttempt = $workflowRunAttempt }
        observedAt = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture)
        sourceBuildVerified = $false
        authenticodeVerified = $false
    }
    $predicatePath = [IO.Path]::Combine($stagePath, 'receipt-predicate.json')
    Assert-SafeDirectoryPath $stagePath
    $predicateStream = [IO.File]::Open($predicatePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $predicate -Depth 10))
        $predicateStream.Write($bytes, 0, $bytes.Length)
    }
    finally { $predicateStream.Dispose() }

    # All remote and byte checks finish before touching a pre-existing empty output.
    Assert-EmptyOutput $outputPath
    Assert-SafeDirectoryPath $stageAssets
    $emptyBackup = $null
    if ([IO.Directory]::Exists($outputPath)) {
        $emptyBackup = [IO.Path]::Combine($parentPath.FullName, '.localai-empty-output-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::Move($outputPath, $emptyBackup)
    }
    try { [IO.Directory]::Move($stagePath, $outputPath); $promoted = $true }
    catch {
        if ($null -ne $emptyBackup -and -not [IO.Directory]::Exists($outputPath) -and -not [IO.File]::Exists($outputPath)) {
            Assert-SafeDirectoryPath $emptyBackup
            [IO.Directory]::Move($emptyBackup, $outputPath)
        }
        throw
    }
    if ($null -ne $emptyBackup) {
        try { [IO.Directory]::Delete($emptyBackup, $false) }
        catch { Write-Warning 'The receipt was saved, but its empty output backup could not be removed.' }
    }
}
finally { if (-not $promoted) { Remove-OwnedStagingDirectory $stagePath } }

[ordered]@{
    outputRoot = $outputPath
    assetsPath = [IO.Path]::Combine($outputPath, 'assets')
    predicatePath = [IO.Path]::Combine($outputPath, 'receipt-predicate.json')
    releaseId = $ReleaseId
    assetCount = $expectedNames.Count
} | ConvertTo-Json -Compress
