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
    Write-Output 'Process recording contract passed: one record, native exit code, and both unmodified streams.'
}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
