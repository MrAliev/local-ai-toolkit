# Search latency probe

[Русская версия](README.ru.md)

This diagnostic compares `SearchService.SearchAsync` over the independent evaluation
corpus. It uses the shared LocalAi broker and the installed residency policy. It does
not call Ollama directly, change retention, or claim cloud token savings.

The next experiment measures a [broker query-vector cache](query-cache.md), including
the cost of validating model identity and four runs with identical search results.
The [embedding timing diagnostics](embedding-timings.md) then separate backend-reported
load/total time and cache hits without changing public response contracts.
The [cold and concurrent measurements](cold-concurrent.md) check confirmed model
absence and four simultaneous clients against the same retrievals.

Build and run from PowerShell (substitute the existing clean corpus and broker paths):

```powershell
dotnet build scripts/quality/latency/LatencyProbe.csproj -c Release
$env:CODESEARCH_LATENCY_REVISION = git rev-parse HEAD
dotnet scripts/quality/latency/bin/Release/net10.0/LatencyProbe.dll `
  <clean-corpus-root> <independent-mixed.json> <output-outside-corpus.json> 34 <LocalAi.Broker.exe>
```

Run without concurrent indexing, tests, OCR or other inference. Keep the same corpus,
index generation, broker binary, model digest and policy for both implementations.
For a baseline checkout, copy this probe outside that checkout and point its project
reference at that checkout's `CodeSearch.Core.csproj`; record its revision. The probe
records source and corpus identities and verifies source stability at the end. Compare
the before/after model digests as well. A failed/incomplete run is not evidence.

By default, each run searches every case twice in corpus order. Pass 0 includes first-use costs;
pass 1 is a repeated, warm sequence. Neither is a forced cold model test. Quantiles use
nearest rank: sort a pass, take element `ceil(p * count)` (one based). No speed
threshold belongs in the test suite.

The timing boundaries are:

- `totalMs`: the entire `SearchAsync` call, including source snippet reads. It excludes
  MCP formatting, transport and the MCP status call.
- `beforeEmbeddingMs`: root/snapshot resolution, index load/cache lookup, overlay
  selection and query preparation.
- `embeddingMs`: the embedding client call, including broker startup, submission,
  queueing, polling, vector conversion and normalization.
- `afterEmbeddingMs`: ranking and snippet construction after that call.
- `queueMs` / `executionMs`: broker receipt durations, nested inside `embeddingMs`.
  Execution can include model loading and preflight. Optional `backendTiming` sidecars
  expose the successful embedding response's total/load durations when diagnostics
  are enabled. They do not cover earlier preparation or retries. Missing fields stay
  null; total minus load is not pure inference. Nested durations must not be added.

Use the sixth argument `concurrent` for two passes with up to four overlapping
SearchAsync calls through one shared SearchService and broker. `startedAtMs` records
actual overlap; results are saved in corpus order. The sixth argument `cold` instead
waits for verified model absence before each new query, then immediately repeats it
while resident. It primes broker residency tracking outside measurement, uses the
installed idle policy (1–30 seconds supported), and polls less often than that interval.
Cold/warm residency checks and idle waiting are outside search timers. Neither mode
changes the policy; both record it and reject a change during the run.

An optional sixth argument `cache` runs a diagnostic prototype: query vectors are
reused only after reading the model digest through a separate broker Tags job. Misses
validate the digest again after embedding. This prototype is confined to the probe,
keeps at most two entries per case in this two-pass run, and is not a production cache.
`identityMs` is nested inside `embeddingMs`; `cacheHit` identifies reuse. It deliberately
measures the cost of safe identity validation rather than caching by a mutable tag.

## 2026-09-21: reuse one fresh snapshot within each search (#619)

Baseline: `7105a22a51b37fd8d5c57a7a1ca98ab48ffa264c`. Candidate: that revision
with the request-scoped snapshot change in this PR. Installed broker version directory:
`f10ff0ec19a5`; the candidate artifact records the exact broker and core SHA-256.
Both runs used the same installed broker; this was not a runtime upgrade.
The initial baseline probe did not capture binary hashes; its artifact leaves those
fields null. The checked-in probe captures them for subsequent reproductions.

The mixed corpus was clean at commit `2a9b6285b7a7bbecf2c826ae41d3bdd53aabf94e`,
tree `4d6f312ef41ca1372a3816738ab9afaa5a1e5ec5`, 87 files / 227 chunks, with generation
`1364bc2a1d1891bae169bc138d397cc633055e64e824193014fd010383fd364b`.
Model: `qwen3-embedding:8b-q8_0`, unchanged digest
`9704fd987c12aa746934ea9f99dc85527c83c6e4b98b3a10b94689332ee866bb`.

Warm pass, 34 English/Russian C#/TypeScript/Python and no-answer cases, milliseconds:

| Boundary | Before p50 | After p50 | Before p90 | After p90 |
| --- | ---: | ---: | ---: | ---: |
| Whole SearchAsync | 650.20 | 319.96 | 678.35 | 424.77 |
| Before embedding | 440.68 | 189.97 | 454.83 | 204.97 |
| Embedding client | 215.77 | 125.55 | 226.92 | 226.15 |
| Ranking/snippets after embedding | 1.01 | 1.02 | 2.34 | 2.10 |

The repeatable target is the roughly 250 ms reduction in snapshot preparation. The
embedding median also varied between runs; do not attribute that additional difference
to this change. This is one sequential before/after run on one Windows machine, not a
general performance guarantee, a concurrent-load test or an MCP end-to-end result.

All 68 result lists matched exactly, including order and scores. Warm-pass recall@10
remained 0.9666666667, precision@5 0.1933333333, mean first relevant rank 1.7666666667,
and no-answer false-positive rate 0.5. The latter is an existing quality limitation.
The evidence directory saves per-query timings, SHA-256 of the complete serialized hit
lists, and both quality reports. Legacy cloud-token estimates inside quality reports
are not measured savings for this benchmark.

`Save-Evidence.ps1 -InputDirectory <raw-output-directory> -OutputDirectory <evidence-directory>`
expects `baseline.json`, `after.json`, `pilot-cache.json` and their probe sidecars. It
hashes complete hit arrays (including empty arrays), verifies matching corpus/model
identities and all 68 retrievals, and omits the unrelated installed-model inventory.

The separate four-case digest-cache pilot took about 2.68 seconds even on cache hits:
each identity lookup alone cost about 2.24 seconds. It was rejected. It is not a
34-case performance comparison and its quality totals cover only those four cases.

This increment leaves #619 open: broker-level digest-safe caching, separately exposed
model-load/inference timings, exact-symbol semantics, forced-cold/concurrent runs and
alternating search/file/OCR retention measurements remain. Ranking and residency policy
are unchanged. A new search still inspects Git and resolves the current generation;
only duplicate inspection within one call is removed.
