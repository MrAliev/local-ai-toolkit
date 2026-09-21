param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Revision,
    [Parameter(Mandatory)][string]$Destination,
    [string]$SourceRepository = $Repository
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Probe destination must be new' }
New-Item -ItemType Directory -Path $Destination | Out-Null
git -C $Repository archive --format=zip --output (Join-Path $Destination 'source.zip') $Revision
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
Expand-Archive -LiteralPath (Join-Path $Destination 'source.zip') -DestinationPath (Join-Path $Destination 'source')
$hashes = [ordered]@{}
foreach ($name in @('DocumentationWorkspace','DocumentationInputs')) {
    $relative = "src/LocalAi.Documentation/$name.cs"
    $target = Join-Path $Destination "source/$relative"
    Copy-Item -LiteralPath (Join-Path $SourceRepository $relative) -Destination $target
    $hashes[$name] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $text = [IO.File]::ReadAllText($target)
    $anchor = if ($name -eq 'DocumentationWorkspace') { 'var text = File.ReadAllText(path);' } else { 'var tree = CSharpSyntaxTree.ParseText(text);' }
    if (($text.Split($anchor).Count - 1) -ne 1) { throw "Unexpected instrumentation anchor in $name" }
    $counter = if ($name -eq 'DocumentationWorkspace') { 'SourceReads' } else { 'SyntaxParses' }
    $text = $text.Replace($anchor, "DocumentationCaptureMetrics.$counter++; $anchor")
    [IO.File]::WriteAllText($target, $text)
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'source-hashes.json') -Encoding utf8
@'
namespace LocalAi.Documentation;
public static class DocumentationCaptureMetrics
{
    public static int SourceReads, SyntaxParses;
    public static void Reset() => (SourceReads, SyntaxParses) = (0, 0);
}
'@ | Set-Content -LiteralPath (Join-Path $Destination 'source/src/LocalAi.Documentation/DocumentationCaptureMetrics.cs') -Encoding utf8
$probe = Join-Path $Destination 'probe'
New-Item -ItemType Directory -Path $probe | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'DocumentationCaptureProbe.csproj') -Destination $probe
dotnet build (Join-Path $probe 'DocumentationCaptureProbe.csproj') --configuration Release *> (Join-Path $Destination 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Probe build failed; see $Destination/build.log" }
Write-Output (Join-Path $probe 'bin/Release/net10.0/DocumentationCaptureProbe.exe')
