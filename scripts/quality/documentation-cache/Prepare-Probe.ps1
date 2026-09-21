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
foreach ($relative in @('DocumentationPackStore.cs','DocumentationGenerator.cs','DocumentationValidator.cs','DocumentationRenderer.cs','DocumentationCacheVersions.cs','Resources/DocumentationText.resx','Resources/DocumentationText.ru.resx')) {
    $from = Join-Path $SourceRepository "src/LocalAi.Documentation/$relative"
    if (-not (Test-Path -LiteralPath $from)) { continue }
    $target = Join-Path $Destination "source/src/LocalAi.Documentation/$relative"
    Copy-Item -LiteralPath $from -Destination $target
    $hashes[$relative] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'source-hashes.json') -Encoding utf8
$validator = Join-Path $Destination 'source/src/LocalAi.Documentation/DocumentationValidator.cs'
$text = [IO.File]::ReadAllText($validator).Replace("`r`n", "`n")
$anchor = "public static IReadOnlyList<string> Validate(DocumentationInput input, DocumentationDraft draft)`n    {"
if (($text.Split($anchor).Count - 1) -ne 1) { throw 'Unexpected validator anchor' }
[IO.File]::WriteAllText($validator, $text.Replace($anchor, "$anchor`n        using var probeMeasurement = DocumentationCacheMetrics.Validation();"))
$renderer = Join-Path $Destination 'source/src/LocalAi.Documentation/DocumentationRenderer.cs'
$text = [IO.File]::ReadAllText($renderer)
$anchor = 'var (svg, png) = DocumentationDiagram.Render(input);'
if (($text.Split($anchor).Count - 1) -ne 1) { throw 'Unexpected renderer anchor' }
[IO.File]::WriteAllText($renderer, $text.Replace($anchor, "using var probeMeasurement = DocumentationCacheMetrics.Rendering();`n        $anchor"))
@'
using System.Diagnostics;
namespace LocalAi.Documentation;
public static class DocumentationCacheMetrics
{
    public static long ValidationTicks, RenderTicks;
    public static int Validations, Renders;
    public static void Reset() { ValidationTicks = RenderTicks = 0; Validations = Renders = 0; }
    public static IDisposable Validation() { Validations++; return new Measurement(false); }
    public static IDisposable Rendering() { Renders++; return new Measurement(true); }
    private sealed class Measurement(bool rendering) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (rendering) Interlocked.Add(ref RenderTicks, elapsed);
            else Interlocked.Add(ref ValidationTicks, elapsed);
        }
    }
}
'@ | Set-Content -LiteralPath (Join-Path $Destination 'source/src/LocalAi.Documentation/DocumentationCacheMetrics.cs') -Encoding utf8
$probe = Join-Path $Destination 'probe'
New-Item -ItemType Directory -Path $probe | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'LiveCacheProbe.cs'),(Join-Path $PSScriptRoot 'DocumentationCacheProbe.csproj') -Destination $probe
dotnet build (Join-Path $probe 'DocumentationCacheProbe.csproj') --configuration Release *> (Join-Path $Destination 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Probe build failed; see $Destination/build.log" }
Write-Output (Join-Path $probe 'bin/Release/net10.0/DocumentationCacheProbe.exe')
