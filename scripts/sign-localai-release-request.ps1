[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Signer,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$Models,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z')]
    [string]$Version,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
$keyText = $env:LOCALAI_MANIFEST_SIGNING_KEY
Remove-Item Env:LOCALAI_MANIFEST_SIGNING_KEY -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($keyText)) {
    throw 'The release-signing environment must provide LOCALAI_MANIFEST_SIGNING_KEY.'
}

$package = Join-Path $ArtifactRoot 'localai-package.zip'
$packageUri = "https://github.com/MrAliev/local-ai-toolkit/releases/download/$Version/localai-package.zip"
$keyDirectory = [IO.Directory]::CreateTempSubdirectory('localai-release-key-')
$keyPath = Join-Path $keyDirectory.FullName 'manifest-key.pkcs8.der'
$keyBytes = $null
try {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $access = [Security.AccessControl.DirectorySecurity]::new()
    $access.SetOwner($owner)
    $access.SetAccessRuleProtection($true, $false)
    $access.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $owner, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $keyDirectory.FullName -AclObject $access

    $keyBytes = [Convert]::FromBase64String($keyText)
    $keyText = $null
    [IO.File]::WriteAllBytes($keyPath, $keyBytes)
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    & dotnet $Signer sign --package $package --package-uri $packageUri --release-version $Version `
        --version-directory $SourceCommit.Substring(0, 12) --models $Models `
        --private-key $keyPath --out $ArtifactRoot
    if ($LASTEXITCODE -ne 0) { throw 'Release manifest signing failed.' }
}
finally {
    $keyText = $null
    if ($null -ne $keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length) }
    [IO.File]::Delete($keyPath)
    $keyDirectory.Delete()
}

& dotnet $Signer verify-package --package $package `
    --manifest (Join-Path $ArtifactRoot 'release-manifest.json') `
    --signature (Join-Path $ArtifactRoot 'release-manifest.sig')
if ($LASTEXITCODE -ne 0) { throw 'The signed release package did not verify.' }
