#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$temporaryInfo = [IO.Directory]::CreateTempSubdirectory('localai-heldout-test-')
$temporary = $temporaryInfo.FullName
$savedConfig = @{}
foreach ($name in @('GIT_CONFIG_COUNT','GIT_CONFIG_KEY_0','GIT_CONFIG_VALUE_0','GIT_CONFIG_KEY_1','GIT_CONFIG_VALUE_1')) {
    $savedConfig[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:GIT_CONFIG_COUNT = '2'
$env:GIT_CONFIG_KEY_0 = 'user.name'; $env:GIT_CONFIG_VALUE_0 = 'Synthetic fixture test'
$env:GIT_CONFIG_KEY_1 = 'user.email'; $env:GIT_CONFIG_VALUE_1 = 'fixture@example.invalid'
try {
    $prepared = Join-Path $temporary 'prepared'
    & (Join-Path $PSScriptRoot 'run.ps1') -Mode Prepare -SearchSuite heldout-v2 -OutputRoot $prepared | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Raw | ConvertFrom-Json
    if ($manifest.corpora.Count -ne 3) { throw 'Expected three distinct source repositories.' }
    $allIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @('csharp','typescript','python')) {
        $source = Join-Path $prepared "sources/$name"
        $cases = Get-Content -LiteralPath (Join-Path $prepared "results/$name/cases.json") -Raw | ConvertFrom-Json
        if ($cases.cases.Count -ne 20 -or @($cases.cases | Where-Object noAnswer).Count -ne 10) { throw 'Case denominators changed.' }
        foreach ($case in $cases.cases) { if (-not $allIds.Add($case.id)) { throw 'Duplicate query identity.' } }
        $sourceText = (Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
            ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
        foreach ($case in $cases.cases) { if ($sourceText.Contains($case.query, [StringComparison]::Ordinal)) { throw 'Query text leaked into source corpus.' } }
    }
    $protocol = Join-Path $repository 'tests/CodeSearch.Tests/Fixtures/SearchEvaluation/heldout-v2/protocol.json'
    $validator = Join-Path $PSScriptRoot 'evaluate-holdout.cs'
    $validation = & dotnet run --file $validator --configuration Release -- validate $protocol $prepared
    if ($LASTEXITCODE -ne 0) { throw 'Protocol/reference validation failed.' }
    $parsed = ($validation -join "`n") | ConvertFrom-Json
    if (-not $parsed.validated -or $parsed.retrievalExecuted -or $parsed.queries -ne 60 -or $parsed.intents -ne 30) {
        throw 'Offline validation did not establish the expected data contract.'
    }
    $localAi = Join-Path $repository 'src/LocalAi.Cli/bin/Release/net10.0/localai.exe'
    $codeSearch = Join-Path $repository 'src/CodeSearch.Cli/bin/Release/net10.0/codesearch.exe'
    $overlap = Join-Path $prepared 'sources/csharp/run'
    foreach ($inputRoot in @($prepared, ($prepared + [IO.Path]::DirectorySeparatorChar))) {
        $overlapRefused = $false
        try {
            & (Join-Path $PSScriptRoot 'run.ps1') -Mode Search -SearchSuite heldout-v2 -PreparedRoot $inputRoot -OutputRoot $overlap -LocalAi $localAi -CodeSearch $codeSearch | Out-Null
        }
        catch {
            if ($_.Exception.Message -notlike '*quality_output_overlaps_preparation*') { throw }
            $overlapRefused = $true
        }
        if (-not $overlapRefused -or (Test-Path -LiteralPath $overlap)) { throw 'Output overlap was accepted or polluted prepared sources.' }
    }
    function Save-TestJson($value, [string] $path) {
        [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 100) + "`n", $utf8)
    }
    $frozen = Get-Content -LiteralPath $protocol -Raw | ConvertFrom-Json
    $frozen.status = 'frozen-unmeasured'
    $frozen.config.modelDigest = $null
    $frozen.config.fullSearchOptions = $parsed.fullSearchOptions
    $frozen.config | Add-Member qualityProfile $parsed.qualityProfile -Force
    $frozen.freeze = [ordered]@{ corpusHashes = @{}; sourceTrees = @{}; generationIds = @{} }
    $baselineRoot = Join-Path $temporary 'baseline'
    $candidateRoot = Join-Path $temporary 'candidate'
    $reportTemplates = @{}
    foreach ($item in $manifest.corpora) {
        $name = $item.name
        $frozen.freeze.corpusHashes[$name] = $item.corpusSha256
        $frozen.freeze.sourceTrees[$name] = $item.sourceTree
        $frozen.freeze.generationIds[$name] = "fixture-$name-generation"
        $cases = Get-Content -LiteralPath (Join-Path $prepared "results/$name/cases.json") -Raw | ConvertFrom-Json
        $reportTemplates[$name] = ([ordered]@{
            schemaVersion = 1; mode = 'profile'; caseCount = 20; repository = Join-Path $prepared "sources/$name"
            generationId = "fixture-$name-generation"; model = $frozen.config.requestedModel
            sourceCommit = $item.sourceCommit; sourceTree = $item.sourceTree; dirtyHash = $null
            indexedCommit = $item.sourceCommit; indexedTree = $item.sourceTree; corpusSha256 = $item.corpusSha256
            candidateScope = $parsed.preparedInputs.$name.candidateScope; annotations = $parsed.preparedInputs.$name.annotations
            searchOptions = $parsed.fullSearchOptions; qualityProfile = $parsed.qualityProfile; useQueryInstruction = $true
            observations = @($cases.cases | ForEach-Object { @{ caseId = $_.id; hits = @(); elapsed = '00:00:00'; brokerQueueWait = $null } })
        } | ConvertTo-Json -Depth 100)
        foreach ($root in @($baselineRoot, $candidateRoot)) { $null = New-Item -ItemType Directory -Path (Join-Path $root "results/$name") }
    }
    $frozenPath = Join-Path $temporary 'frozen.json'
    Save-TestJson $frozen $frozenPath
    $binaries = @(@{ file = 'codesearch.dll'; sha256 = ('a' * 64); version = 'test'; recordedProductVersion = 'fixture' })
    $plan = [ordered]@{ schemaVersion = 1; protocolSha256 = (Get-FileHash -LiteralPath $frozenPath).Hash.ToLowerInvariant()
        baseline = @{ codeSearch = $binaries }; candidate = @{ codeSearch = $binaries } }
    $planPath = Join-Path $temporary 'artifact-plan.json'
    function Reset-Comparison {
        Save-TestJson $plan $planPath
        foreach ($root in @($baselineRoot, $candidateRoot)) {
            Save-TestJson @{ completed = $true; binaryIdentityStable = $true; codeSearch = $binaries; codeSearchAfter = $binaries } (Join-Path $root 'run.json')
            foreach ($name in $reportTemplates.Keys) { [IO.File]::WriteAllText((Join-Path $root "results/$name/baseline.json"), $reportTemplates[$name], $utf8) }
        }
    }
    function Invoke-Comparison([string] $name, [string] $expectedError) {
        $PSNativeCommandUseErrorActionPreference = $false
        $output = Join-Path $temporary "$name.json"
        $stderr = Join-Path $temporary "$name.stderr"
        & dotnet run --file $validator --configuration Release -- compare $frozenPath $prepared $planPath $baselineRoot $candidateRoot $output > (Join-Path $temporary "$name.stdout") 2> $stderr
        if ($expectedError -eq 'parity-mismatch') {
            if ($LASTEXITCODE -ne 1 -or -not (Test-Path -LiteralPath $output)) { throw 'Raw-hit mismatch did not produce a failed comparison report.' }
            $mismatch = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
            if ($mismatch.parityPassed -or $mismatch.mismatches.Count -ne 1) { throw 'Raw-hit mismatch was not detected exactly once.' }
        }
        elseif ($expectedError) {
            if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $output) -or
                -not [IO.File]::ReadAllText($stderr).Contains($expectedError, [StringComparison]::Ordinal)) {
                throw "Comparison did not refuse the expected condition: $name."
            }
        }
        elseif ($LASTEXITCODE -ne 0 -or -not (Get-Content -LiteralPath $output -Raw | ConvertFrom-Json).parityPassed) {
            throw 'Valid synthetic comparison failed.'
        }
    }
    Reset-Comparison
    Invoke-Comparison 'valid' ''
    $equivalentPath = Join-Path $candidateRoot 'results/csharp/baseline.json'
    $equivalent = Get-Content -LiteralPath $equivalentPath -Raw | ConvertFrom-Json
    $instant = [DateTimeOffset]::Parse(([DateTime]$equivalent.qualityProfile.calibratedAtUtc).ToUniversalTime().ToString('O'), [Globalization.CultureInfo]::InvariantCulture)
    $equivalent.qualityProfile.calibratedAtUtc = $instant.ToOffset([TimeSpan]::FromHours(1)).ToString('O')
    Save-TestJson $equivalent $equivalentPath
    Invoke-Comparison 'equivalent-instant' ''
    Reset-Comparison
    $hitPath = Join-Path $candidateRoot 'results/csharp/baseline.json'
    $different = Get-Content -LiteralPath $hitPath -Raw | ConvertFrom-Json
    $different.observations[0].hits = @(@{ path = 'different.cs'; symbol = 'Different'; startLine = 1; endLine = 1; responseCharacters = 10; sourceLines = 1 })
    Save-TestJson $different $hitPath
    Invoke-Comparison 'raw-hit-mismatch' 'parity-mismatch'
    Reset-Comparison
    $wrongPlan = $plan | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $wrongPlan.protocolSha256 = 'wrong'
    Save-TestJson $wrongPlan $planPath
    Invoke-Comparison 'protocol' 'Artifact plan does not bind'
    Reset-Comparison
    $wrongPlan = $plan | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $wrongPlan.candidate.codeSearch[0].sha256 = 'wrong'
    Save-TestJson $wrongPlan $planPath
    Invoke-Comparison 'fingerprint' 'Observed before/after binaries differ'
    Reset-Comparison
    $changedRun = Get-Content -LiteralPath (Join-Path $candidateRoot 'run.json') -Raw | ConvertFrom-Json
    $changedRun.codeSearchAfter[0].sha256 = 'changed-during-run'
    Save-TestJson $changedRun (Join-Path $candidateRoot 'run.json')
    Invoke-Comparison 'changed-binary' 'Observed before/after binaries differ'
    foreach ($mutation in @('profile','profile-time','profile-time-invalid','profile-time-missing','profile-time-null','profile-time-newline','profile-time-space','profile-time-no-offset','options','generation','references','scope')) {
        Reset-Comparison
        foreach ($root in @($baselineRoot, $candidateRoot)) {
            $path = Join-Path $root 'results/csharp/baseline.json'
            $changed = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            switch ($mutation) {
                profile { $changed.qualityProfile.minVectorScore = 0.99 }
                profile-time { $changed.qualityProfile.calibratedAtUtc = $instant.AddMinutes(1).ToString('O') }
                profile-time-invalid { $changed.qualityProfile.calibratedAtUtc = 'invalid' }
                profile-time-missing { $changed.qualityProfile.PSObject.Properties.Remove('calibratedAtUtc') }
                profile-time-null { $changed.qualityProfile.calibratedAtUtc = $null }
                profile-time-newline { $changed.qualityProfile.calibratedAtUtc = $instant.ToString('O') + "`n" }
                profile-time-space { $changed.qualityProfile.calibratedAtUtc = $instant.ToString('O') + ' ' }
                profile-time-no-offset { $changed.qualityProfile.calibratedAtUtc = '2001-01-01T00:00:00' }
                options { $changed.searchOptions.topK = 1 }
                generation { $changed.generationId = 'wrong' }
                references { $changed.annotations[0].id = 'unreviewed' }
                scope { $changed.candidateScope = @{} }
            }
            Save-TestJson $changed $path
        }
        $expected = switch ($mutation) {
            generation { 'Measured source/index identity differs' }
            { $_ -in @('profile-time-invalid','profile-time-missing','profile-time-null','profile-time-newline','profile-time-space','profile-time-no-offset') } { 'Holdout comparison identity/config mismatch' }
            { $_ -in @('references','scope') } { 'Measured scope/references differ' }
            default { 'Measured configuration differs' }
        }
        Invoke-Comparison $mutation $expected
    }
    Reset-Comparison
    $dirtyPath = Join-Path $prepared 'sources/python/unreviewed.py'
    [IO.File]::WriteAllText($dirtyPath, '# unreviewed source change', $utf8)
    Invoke-Comparison 'dirty' 'Prepared source bytes/current identity changed'
    Remove-Item -LiteralPath $dirtyPath
    # Corrupt a task-owned annotation copy. Reuse must refuse before it could call a model.
    # Python is last: earlier corpora must not be evaluated before this check.
    $casePath = Join-Path $prepared 'results/python/cases.json'
    [IO.File]::AppendAllText($casePath, " `n", $utf8)
    $refused = $false
    try {
        & (Join-Path $PSScriptRoot 'run.ps1') -Mode Search -SearchSuite heldout-v2 -PreparedRoot $prepared -OutputRoot (Join-Path $temporary 'refused') -LocalAi $localAi -CodeSearch $codeSearch | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike '*quality_prepared_inputs_changed*') { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'Changed prepared annotations were accepted.' }
    if (Test-Path -LiteralPath (Join-Path $temporary 'refused/results/csharp/status.stdout.log')) { throw 'A native evaluator was reached before all inputs were checked.' }
    Write-Output 'Heldout data contract passed: 60 queries, 30 paired intents, 3 repos; synthetic parity and provenance refusals verified without retrieval.'
}
finally {
    foreach ($name in $savedConfig.Keys) { [Environment]::SetEnvironmentVariable($name, $savedConfig[$name], 'Process') }
    $resolved = (Resolve-Path -LiteralPath $temporary).Path
    if ($resolved -ne $temporary -or -not $temporaryInfo.Name.StartsWith('localai-heldout-test-') -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing cleanup outside the owned heldout-test directory.'
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
exit 0
