#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputRoot,
    [ValidateSet('All', 'Search', 'Ocr', 'Prepare')][string] $Mode = 'All',
    [string] $LocalAi,
    [string] $CodeSearch,
    [string] $OcrModel = 'glm-ocr:bf16',
    [ValidateSet('independent', 'heldout-v2')][string] $SearchSuite = 'independent',
    [string] $PreparedRoot
)

$ErrorActionPreference = 'Stop'
function Assert-PlainPath([string] $Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $entry = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'quality_linked_path_refused'
        }
    }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputRoot))
Assert-PlainPath $OutputRoot
if ($OutputRoot.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputRoot.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'quality_output_must_be_outside_repository'
}
if ((Test-Path -LiteralPath $OutputRoot) -and (Get-ChildItem -LiteralPath $OutputRoot -Force | Select-Object -First 1)) {
    throw 'quality_output_must_be_empty'
}
if ($Mode -ne 'Prepare' -and (-not $LocalAi -or -not (Test-Path -LiteralPath $LocalAi -PathType Leaf))) {
    throw 'quality_localai_binary_required'
}
if ($Mode -in @('All', 'Search') -and (-not $CodeSearch -or -not (Test-Path -LiteralPath $CodeSearch -PathType Leaf))) {
    throw 'quality_codesearch_binary_required'
}
if ($LocalAi) { $LocalAi = [IO.Path]::GetFullPath($LocalAi) }
if ($CodeSearch) { $CodeSearch = [IO.Path]::GetFullPath($CodeSearch) }
$preparation = $null
if ($PreparedRoot) {
    if ($Mode -ne 'Search') { throw 'quality_prepared_sources_require_search_mode' }
    $PreparedRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PreparedRoot))
    Assert-PlainPath $PreparedRoot
    if ($OutputRoot.Equals($PreparedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputRoot.StartsWith($PreparedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $PreparedRoot.StartsWith($OutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'quality_output_overlaps_preparation'
    }
    foreach ($name in @('csharp', 'typescript', 'python', 'mixed')) {
        Assert-PlainPath (Join-Path $PreparedRoot "sources/$name")
        Assert-PlainPath (Join-Path $PreparedRoot "results/$name/cases.json")
    }
    $preparation = Get-Content -LiteralPath (Join-Path $PreparedRoot 'preparation.json') -Raw | ConvertFrom-Json
    if ($preparation.schemaVersion -ne 1 -or $preparation.searchSuite -ne $SearchSuite) {
        throw 'quality_preparation_suite_mismatch'
    }
}
$null = New-Item -ItemType Directory -Path $OutputRoot -Force
$utf8 = [Text.UTF8Encoding]::new($false)

function Save-Json($Value, [string] $Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100) + "`n", $utf8)
}

function Invoke-Recorded([string] $Executable, [string[]] $Arguments, [string] $Stem, [string] $WorkingDirectory = $repository) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $stdoutPath = $Stem + '.stdout.log'
    $stderrPath = $Stem + '.stderr.log'
    # Progress must be readable before the child exits, including its first short line.
    $stdout = [IO.FileStream]::new($stdoutPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $stderr = [IO.FileStream]::new($stderrPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite, 1, $true)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $null = $process.Start()
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $process.WaitForExit()
        $null = $outCopy.GetAwaiter().GetResult()
        $null = $errCopy.GetAwaiter().GetResult()
        $timer.Stop()
        return [pscustomobject]@{ exitCode = $process.ExitCode; elapsedMs = $timer.ElapsedMilliseconds;
            stdout = $stdoutPath; stderr = $stderrPath; executable = $Executable; arguments = $Arguments }
    }
    finally { $process.Dispose(); $stdout.Dispose(); $stderr.Dispose() }
}

function Require-Success($Run) {
    if ($Run.exitCode -ne 0) { throw "quality_command_failed: $($Run.exitCode); $($Run.stderr)" }
}

function Source-Identity([string] $Source, [string] $Stem) {
    $revision = Invoke-Recorded 'git' @('-C', $Source, 'rev-parse', 'HEAD', 'HEAD^{tree}') ($Stem + '-revision')
    Require-Success $revision
    $values = [IO.File]::ReadAllLines($revision.stdout)
    $status = Invoke-Recorded 'git' @('-C', $Source, 'status', '--porcelain', '--untracked-files=all') ($Stem + '-status')
    Require-Success $status
    if ($values.Count -ne 2 -or [IO.File]::ReadAllText($status.stdout).Trim()) { throw 'quality_sources_not_clean' }
    return [ordered]@{ sourceCommit = $values[0]; sourceTree = $values[1] }
}

function Binary-Identity([string] $Path) {
    if (-not $Path) { return $null }
    $file = Get-Item -LiteralPath $Path
    # Framework-dependent apphosts do not identify their implementation. Include the DLLs.
    $files = @($file) + @(Get-ChildItem -LiteralPath $file.DirectoryName -File -Filter '*.dll')
    return @($files | Sort-Object FullName -Unique | ForEach-Object {
        [ordered]@{ file = $_.Name; version = $_.VersionInfo.FileVersion; recordedProductVersion = $_.VersionInfo.ProductVersion;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}

function Broker-Identity {
    if (-not $IsWindows) { return @{ discovery = 'unavailable'; binaries = @() } }
    return @(Get-CimInstance Win32_Process -Filter "name='LocalAi.Broker.exe'" | ForEach-Object {
        [ordered]@{ processId = $_.ProcessId; path = $_.ExecutablePath; binaries = @(Binary-Identity $_.ExecutablePath) }
    })
}

$provenance = [ordered]@{
    schemaVersion = 1; startedUtc = [DateTime]::UtcNow.ToString('O'); mode = $Mode
    sourceCommit = (& git -C $repository rev-parse HEAD); sourceStatus = @(& git -C $repository status --porcelain)
    platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    processorCount = [Environment]::ProcessorCount
    localAi = @(Binary-Identity $LocalAi); codeSearch = @(Binary-Identity $CodeSearch)
    localAiAfter = @(); codeSearchAfter = @(); binaryIdentityStable = $null
    brokerBefore = @(Broker-Identity); brokerAfter = @()
    completed = $false
    searchSuite = $SearchSuite; preparedRoot = $PreparedRoot
}
Save-Json $provenance (Join-Path $OutputRoot 'run.json')

try {
    if ($Mode -in @('All', 'Search', 'Prepare')) {
        if (-not $PreparedRoot) {
            $globalUsings = Join-Path $OutputRoot 'GlobalUsings.cs'
            [IO.File]::WriteAllText($globalUsings, 'global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic;', $utf8)
            Add-Type -Path @($globalUsings, (Join-Path $repository 'tests/Shared/SyntheticFrontendRepository.cs'),
                (Join-Path $PSScriptRoot 'QualityCorpusSources.cs'))
            $author = (& git -C $repository config user.name)
            $email = (& git -C $repository config user.email)
            if (-not $author -or -not $email) { throw 'quality_git_identity_required' }
        }
        $names = if ($SearchSuite -eq 'heldout-v2') { @('csharp', 'typescript', 'python') } else { @('csharp', 'typescript', 'python', 'mixed') }
        $preparedCorpora = @()
        $workItems = @()
        foreach ($name in $names) {
            $source = Join-Path $(if ($PreparedRoot) { $PreparedRoot } else { $OutputRoot }) "sources/$name"
            $resultDirectory = Join-Path $OutputRoot "results/$name"
            $null = New-Item -ItemType Directory -Path $resultDirectory -Force
            # Annotations and all receipts live beside the source tree, never inside it.
            $cases = Join-Path $resultDirectory 'cases.json'
            if ($PreparedRoot) {
                $record = @($preparation.corpora | Where-Object name -eq $name)
                if ($record.Count -ne 1) { throw 'quality_preparation_corpus_missing' }
                Copy-Item -LiteralPath (Join-Path $PreparedRoot "results/$name/cases.json") -Destination $cases
            }
            else {
                [LocalAi.TestSupport.QualityCorpusSources]::Write($source, $name, $SearchSuite)
                Copy-Item -LiteralPath (Join-Path $repository "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/$SearchSuite/$name.json") -Destination $cases
                Require-Success (Invoke-Recorded 'git' @('init', '--initial-branch=main', $source) (Join-Path $resultDirectory 'git-init'))
                Require-Success (Invoke-Recorded 'git' @('-C', $source, '-c', 'core.autocrlf=false', 'add', '.') (Join-Path $resultDirectory 'git-add'))
                Require-Success (Invoke-Recorded 'git' @('-C', $source, '-c', "user.name=$author", '-c', "user.email=$email",
                    'commit', '-m', "Create $SearchSuite $name search sources") (Join-Path $resultDirectory 'git-commit'))
            }
            $identity = Source-Identity $source (Join-Path $resultDirectory 'source')
            $caseHash = (Get-FileHash -LiteralPath $cases -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($PreparedRoot -and ($record[0].sourceCommit -ne $identity.sourceCommit -or
                $record[0].sourceTree -ne $identity.sourceTree -or $record[0].corpusSha256 -ne $caseHash)) {
                throw 'quality_prepared_inputs_changed'
            }
            $preparedCorpora += [ordered]@{ name = $name; sourceCommit = $identity.sourceCommit; sourceTree = $identity.sourceTree; corpusSha256 = $caseHash }
            $workItems += [ordered]@{ name = $name; source = $source; cases = $cases; resultDirectory = $resultDirectory; identity = $identity; caseHash = $caseHash }
        }
        if ($Mode -eq 'Prepare') {
            Save-Json ([ordered]@{ schemaVersion = 1; searchSuite = $SearchSuite; corpora = $preparedCorpora }) (Join-Path $OutputRoot 'preparation.json')
        }
        else { foreach ($work in $workItems) {
            $name = $work.name; $source = $work.source; $cases = $work.cases; $resultDirectory = $work.resultDirectory
            if (-not $PreparedRoot) {
                Write-Output "Indexing $name sources. Progress: $resultDirectory/sync.stderr.log"
                $sync = Invoke-Recorded $LocalAi @('sync', '--root', $source) (Join-Path $resultDirectory 'sync')
                Save-Json $sync (Join-Path $resultDirectory 'sync-run.json')
                Require-Success $sync
            }
            $status = Invoke-Recorded $CodeSearch @('status', '--root', $source, '--json') (Join-Path $resultDirectory 'status')
            Require-Success $status
            Write-Output "Evaluating $name references."
            $run = Invoke-Recorded $CodeSearch @('evaluate', '--root', $source, '--cases', $cases) (Join-Path $resultDirectory 'evaluate')
            Save-Json $run (Join-Path $resultDirectory 'evaluate-run.json')
            Require-Success $run
            $after = Source-Identity $source (Join-Path $resultDirectory 'source-after')
            if ($after.sourceCommit -ne $work.identity.sourceCommit -or $after.sourceTree -ne $work.identity.sourceTree -or
                (Get-FileHash -LiteralPath $cases -Algorithm SHA256).Hash.ToLowerInvariant() -ne $work.caseHash) {
                throw 'quality_inputs_changed_during_evaluation'
            }
            $evaluation = Get-Content -LiteralPath $run.stdout -Raw | ConvertFrom-Json
            if ($evaluation.caseCount -ne (Get-Content -LiteralPath $cases -Raw | ConvertFrom-Json).cases.Count -or
                $evaluation.corpusSha256 -ne (Get-FileHash -LiteralPath $cases -Algorithm SHA256).Hash.ToLowerInvariant()) {
                throw 'quality_evaluation_provenance_mismatch'
            }
            Copy-Item -LiteralPath $run.stdout -Destination (Join-Path $resultDirectory 'baseline.json')
            Write-Output "Completed ${name}: $($evaluation.caseCount) cases."
        } }
    }
    if ($Mode -in @('All', 'Ocr')) {
        Add-Type -Path (Join-Path $PSScriptRoot 'OcrQualityScoring.cs')
        $fixtures = Join-Path $repository 'tests/Fixtures/OcrQuality'
        $manifestPath = Join-Path $fixtures 'corpus.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $ocr = Join-Path $OutputRoot 'results/ocr'
        $null = New-Item -ItemType Directory -Path $ocr -Force
        $question = 'Transcribe all text exactly, preserving line breaks and repeated lines. Return only the transcription in the original language.'
        $results = @()
        foreach ($case in $manifest.cases) {
            $image = Join-Path $fixtures $case.image
            $reference = Join-Path $fixtures $case.reference
            if ((Get-FileHash -LiteralPath $image).Hash.ToLowerInvariant() -ne $case.imageSha256 -or
                (Get-FileHash -LiteralPath $reference).Hash.ToLowerInvariant() -ne $case.referenceSha256) {
                throw 'quality_ocr_reference_hash_mismatch'
            }
            Write-Output "Reading $($case.id) with $OcrModel."
            $run = Invoke-Recorded $LocalAi @('read-image', $question, $image, '--profile', 'Ocr', '--model', $OcrModel,
                '--language', 'en', '--json') (Join-Path $ocr $case.id)
            $response = $null
            $parseError = $null
            try { $response = Get-Content -LiteralPath $run.stdout -Raw | ConvertFrom-Json }
            catch { $parseError = $_.Exception.Message }
            $valid = $run.exitCode -eq 0 -and $response.ok -eq $true -and $response.data.answer -is [string]
            $answer = if ($response.data.answer -is [string]) { $response.data.answer } else { $null }
            $fields = [LocalAi.Quality.OcrFieldReference[]]@($case.fields | ForEach-Object {
                [LocalAi.Quality.OcrFieldReference]::new($_.label, $_.value)
            })
            $score = [LocalAi.Quality.OcrQualityScoring]::Measure([IO.File]::ReadAllText($reference), $answer, $valid, $fields, $run.elapsedMs)
            # This is raw model data. Never execute it or remove repeated lines before scoring.
            if ($null -ne $answer) { [IO.File]::WriteAllText((Join-Path $ocr ($case.id + '.answer.txt')), $answer, $utf8) }
            $results += [ordered]@{ id = $case.id; language = $case.language; length = $case.length;
                imageSha256 = $case.imageSha256; referenceSha256 = $case.referenceSha256;
                score = $score; response = $response; parseError = $parseError; invocation = $run }
            Save-Json ([ordered]@{ schemaVersion = 1; modelRequested = $OcrModel; question = $question;
                corpusSha256 = (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant(); cases = $results }) (Join-Path $ocr 'baseline.json')
            Write-Output "Completed $($case.id): technical=$valid; elapsedMs=$($run.elapsedMs)."
        }
    }
    $provenance.completed = $true
}
finally {
    $provenance.brokerAfter = @(Broker-Identity)
    $provenance.localAiAfter = @(Binary-Identity $LocalAi)
    $provenance.codeSearchAfter = @(Binary-Identity $CodeSearch)
    $provenance.binaryIdentityStable =
        (($provenance.localAi | ConvertTo-Json -Depth 20 -Compress) -ceq ($provenance.localAiAfter | ConvertTo-Json -Depth 20 -Compress)) -and
        (($provenance.codeSearch | ConvertTo-Json -Depth 20 -Compress) -ceq ($provenance.codeSearchAfter | ConvertTo-Json -Depth 20 -Compress))
    if (-not $provenance.binaryIdentityStable) { $provenance.completed = $false }
    $provenance.finishedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Json $provenance (Join-Path $OutputRoot 'run.json')
    if (-not $provenance.binaryIdentityStable) { throw 'quality_binary_identity_changed' }
}
