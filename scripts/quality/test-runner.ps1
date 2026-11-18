#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'run.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw ($errors | Out-String) }
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Recorded'
}, $false)
if (-not $function) { throw 'The process-recording function is missing.' }
# Exercise the actual function without starting the model benchmark or copying its implementation.
. ([scriptblock]::Create($function.Extent.Text))
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('localai-quality-runner-' + [Guid]::NewGuid().ToString('N'))))
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $temporary.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test directory.' }
$null = New-Item -ItemType Directory -Path $temporary
try {
    $shell = (Get-Process -Id $PID).Path
    $child = Join-Path $temporary 'child.ps1'
    [IO.File]::WriteAllText($child, @'
param([string] $ProgressLog)
$ErrorActionPreference = 'Stop'
$reader = [IO.File]::Open($ProgressLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$reader.Dispose()
[Console]::Out.Write("result")
[Console]::Error.Write("diagnostic")
exit 37
'@, [Text.UTF8Encoding]::new($false))
    $stem = Join-Path $temporary 'case'
    $run = Invoke-Recorded $shell @('-NoProfile', '-File', $child, ($stem + '.stdout.log')) $stem $temporary
    if (@($run).Count -ne 1) { throw "Expected one invocation record, received $(@($run).Count)." }
    if ($run.exitCode -ne 37) { throw 'The native exit code was lost.' }
    if ([IO.File]::ReadAllText($run.stdout) -ne 'result') { throw 'The standard output was changed.' }
    if ([IO.File]::ReadAllText($run.stderr) -ne 'diagnostic') { throw 'The standard error was changed.' }
    if ($run.elapsedMs -lt 0) { throw 'Invalid elapsed duration.' }
    $liveChild = Join-Path $temporary 'live-child.ps1'
    [IO.File]::WriteAllText($liveChild, @'
param([string] $ProgressLog)
$ErrorActionPreference = 'Stop'
[Console]::Error.Write('phase-one')
[Console]::Error.Flush()
$wait = [Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $stream = [IO.File]::Open($ProgressLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { $visible = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($visible -eq 'phase-one') { break }
    if ($wait.Elapsed -gt [TimeSpan]::FromSeconds(30)) { throw 'Progress was not visible while the child was running.' }
    [Threading.Thread]::Sleep(10)
}
[Console]::Out.Write('live-progress-observed')
exit 0
'@, [Text.UTF8Encoding]::new($false))
    $liveStem = Join-Path $temporary 'live'
    $live = Invoke-Recorded $shell @('-NoProfile', '-File', $liveChild, ($liveStem + '.stderr.log')) $liveStem $temporary
    if ($live.exitCode -ne 0 -or [IO.File]::ReadAllText($live.stdout) -ne 'live-progress-observed') {
        throw 'Process progress was buffered until exit.'
    }
    Write-Output 'Process recording contract passed: one record, native exit code, and both unmodified streams.'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $temporary).Path
    if ($resolved -ne $temporary -or -not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolved).StartsWith('localai-quality-runner-') -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing cleanup outside the owned runner-test directory.'
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
