param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Revision,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Probe destination must be new' }
New-Item -ItemType Directory -Path $Destination | Out-Null
git -C $Repository archive --format=zip --output (Join-Path $Destination 'source.zip') $Revision
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
Expand-Archive -LiteralPath (Join-Path $Destination 'source.zip') -DestinationPath (Join-Path $Destination 'source')
$relative = 'src/LocalAi.Broker.Core/DurableQueue.cs'
$queueSource = Join-Path $Destination "source/$relative"
# Only this implementation is substituted; dependencies come from the named immutable revision.
Copy-Item -LiteralPath (Join-Path $Repository $relative) -Destination $queueSource
$hash = (Get-FileHash -LiteralPath $queueSource -Algorithm SHA256).Hash.ToLowerInvariant()
$hash | Set-Content -LiteralPath (Join-Path $Destination 'queue-source.sha256') -Encoding utf8
$text = [IO.File]::ReadAllText($queueSource)
$readAnchor = 'var document = JsonSerializer.Deserialize<RequestDocument>('
if (($text.Split($readAnchor).Count - 1) -ne 1) { throw 'Unexpected request-read implementation' }
$text = $text.Replace($readAnchor, "QueueReadProbeMetrics.RecordRead(jobDirectory);`n        $readAnchor")
$leasePattern = '(private sealed class MutexLease[^\r\n]*\r?\n    \{)'
if ([regex]::Matches($text, $leasePattern).Count -ne 1) { throw 'Unexpected mutex lease implementation' }
$text = [regex]::Replace($text, $leasePattern, '$1' + "`n        private readonly long _probeAcquired = System.Diagnostics.Stopwatch.GetTimestamp();")
if (($text.Split('mutex.ReleaseMutex();').Count - 1) -ne 1) { throw 'Unexpected mutex release implementation' }
$text = $text.Replace('mutex.ReleaseMutex();', "QueueReadProbeMetrics.HeldTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _probeAcquired;`n            mutex.ReleaseMutex();")
[IO.File]::WriteAllText($queueSource, $text)
@'
namespace LocalAi.Broker;
public static class QueueReadProbeMetrics
{
    public static long RequestReads, RequestBytes, HeldTicks;
    public static readonly Dictionary<string, long> RequestLengths = new(StringComparer.Ordinal);
    public static void RecordRead(string directory)
    {
        RequestReads++;
        if (RequestLengths.TryGetValue(directory, out var length)) RequestBytes += length;
    }
    public static void Reset() => (RequestReads, RequestBytes, HeldTicks) = (0, 0, 0);
}
'@ | Set-Content -LiteralPath (Join-Path $Destination 'source/src/LocalAi.Broker.Core/QueueReadProbeMetrics.cs') -Encoding utf8
$probe = Join-Path $Destination 'probe'
New-Item -ItemType Directory -Path $probe | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'QueueContentionProbe.cs'),(Join-Path $PSScriptRoot 'QueueReadProbe.csproj') -Destination $probe
dotnet build (Join-Path $probe 'QueueReadProbe.csproj') --configuration Release *> (Join-Path $Destination 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Probe build failed; see $Destination/build.log" }
Write-Output (Join-Path $probe 'bin/Release/net10.0/QueueReadProbe.exe')
