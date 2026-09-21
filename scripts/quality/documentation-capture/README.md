# Documentation source collection

[Русская версия](README.ru.md)

This probe isolates the source-collection part of #622. It compares the implementation at
`f636319a5ba55bc2f6ff8e9bc62c4864c3d79ff4` with the scoped reader. Text/render cache separation
and generation/validation/render timings remain separate work in that issue.

The frozen fixture uses that commit's source archive and its actual published index:
865 semantic documents, 10,877 chunks, and a 190,908,768-byte CIDX. The fixture gets its own
Git repository and runtime. Only repository/generation/Git identities are rewritten;
source hashes, symbols, occurrences, relationships, chunks, and vectors are preserved.
The single synthetic fixture commit supplies identical scoped history to both versions.

`Capture` includes Git inspection, manifest checksum verification over the real CIDX,
SIDX loading, source validation, syntax parsing, fact/hash construction, scoped Git history,
and the final freshness check. It excludes model generation, validation of a draft,
rendering, writing a documentation package, and applying it. No model runs.
Counters are injected into temporary source copies only: `SourceReads` counts documentation
source reads, not Git inspection's own reads or index I/O; `SyntaxParses` counts C# parses
in `DocumentationInputs`. Allocations are current-thread managed allocation deltas, not peak RAM.

Each limit (1, 5, all) gets one warmup and three measured captures. Run the baseline group,
then the candidate group, alone on the same machine; filesystem caches are warm. The complete
serialized units must have exactly the same SHA-256 across all six samples for each limit.
This checks the selection, content, source/reference/history hashes, and ordering together.
Tests impose no speed threshold. Timing and allocation changes are not cloud-token savings.

## Recorded results

Three-sample medians on the same Windows host, September 21, 2026:

| Limit | Capture before → after (ms) | Source reads | C# parses | Managed allocations before → after (bytes) |
| --- | ---: | ---: | ---: | ---: |
| 1 | 1,404.34 → 1,121.47 | 865 → 1 | 843 → 1 | 431,316,400 → 251,906,344 |
| 5 | 1,597.58 → 1,395.24 | 865 → 6 | 843 → 3 | 461,847,944 → 295,481,680 |
| All 1,269 | 102,247.16 → 102,835.16 | 865 → 824 | 843 → 817 | 28,056,915,288 → 28,056,557,776 |

All serialized units match exactly. The limited requests improve; the all-types capture
does not show a speed benefit (its after samples span 99.79–104.66 seconds). Most all-types
work remains in the unchanged per-unit facts, hashing, and history pipeline. Allocation
totals accumulate over a capture and must not be interpreted as resident memory.

## Reproduce

Run from the repository in PowerShell. Keep the published generation for the baseline
commit available as `$generation`. Use a new, task-owned `$run` directory. Do not point
fixture commands at a user checkout.

```powershell
$revision = 'f636319a5ba55bc2f6ff8e9bc62c4864c3d79ff4'
$run = Join-Path $env:TEMP ('documentation-capture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
git archive --format=zip --output "$run/baseline.zip" $revision
Expand-Archive "$run/baseline.zip" "$run/baseline-source"
Expand-Archive "$run/baseline.zip" "$run/corpus"
& ./scripts/quality/documentation-capture/Prepare-Probe.ps1 -Repository $PWD -Revision $revision -SourceRepository "$run/baseline-source" -Destination "$run/before-run"
& ./scripts/quality/documentation-capture/Prepare-Probe.ps1 -Repository $PWD -Revision $revision -Destination "$run/after-run"
$before = "$run/before-run/probe/bin/Release/net10.0/DocumentationCaptureProbe.exe"
$after = "$run/after-run/probe/bin/Release/net10.0/DocumentationCaptureProbe.exe"
# Set $generation to the baseline generation directory containing manifest.json/base.cidx/semantic.sidx.
& $before prepare "$run/corpus" "$run/runtime" $generation
& $before measure "$run/corpus" "$run/runtime" "$run/before-run/results.json" "$run/before-run/source-hashes.json"
& $after measure "$run/corpus" "$run/runtime" "$run/after-run/results.json" "$run/after-run/source-hashes.json"
& ./scripts/quality/documentation-capture/Save-Evidence.ps1 -InputDirectory $run -OutputDirectory "$run/evidence"
```

The script refuses to reuse a probe destination. It archives dependencies at the named
revision and substitutes only the two documentation collection files from `SourceRepository`
(the current checkout by default). Evidence records their SHA-256 before instrumentation.
Raw samples and the checked summary live in [evidence](evidence/summary.json).
