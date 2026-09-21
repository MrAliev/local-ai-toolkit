# Cold and concurrent search measurements (#619)

[Русская версия](cold-concurrent.ru.md)

This experiment extends the latency probe with `concurrent` and `cold` modes.
It changes no production scheduling, cache, retention or response contract.
All model and metadata requests use the shared broker. No cloud-token savings
are attributed to these measurements.

## Reproduction and boundaries

Build the broker at `ecf4062e80f09eb4891cbcbc2d66eb220a4d59a5` and the probe with
this change. Use the [broker drain/start procedure](query-cache.md), verify its
host PID/path before and after each run, and restore the installed broker afterwards.
Set `LOCALAI_EMBEDDING_TIMINGS=1` in each measured broker's environment.
Use one fresh broker process for each run, in this order:

| Output name | Case count | Sixth probe argument |
| --- | ---: | --- |
| sequential-1 | 34 | sequential |
| concurrent-1 | 34 | concurrent |
| concurrent-2 | 34 | concurrent |
| sequential-2 | 34 | sequential |
| cold | 6 | cold |

The command is documented in [README](README.md). All runs use the same clean
independent mixed corpus, generation, model digest and binary hashes recorded in
the evidence. Policy is RequireFullVram, 10-second idle retention, 128 embedding
batch items. No unrelated inference, indexing or tests run alongside the probe.
The installed RC22 broker is restored afterwards; this is not a runtime upgrade.

The concurrent mode allows four overlapping SearchAsync calls against one shared
SearchService and broker. This models concurrent requests in one client process;
it is not a test of four separate MCP processes. Actual start offsets demonstrate
overlap. Results are serialized in corpus order after each complete pass.

The cold mode first submits one foreground embedding outside measurement so the
new broker tracks residency. Before each case, it waits for the normal idle policy
to unload the model, then confirms absence through a broker Processes job. It
checks presence and digest before the immediate paired repeat. Checks and waiting
are outside SearchAsync timers. Polling faster than the idle interval would itself
keep the broker active, so the probe waits idle interval plus two seconds between
checks. A failed exploratory run using frequent checks is excluded from evidence.

Cold means absent from backend model residency, not cold OS file caches or a reboot.
The first six cases are English/Russian C# queries selected in corpus order; this
small subset does not establish cold behavior across every language or query type.
The immediate repeat combines resident-model and query-vector-cache effects.
It cannot isolate the contribution of either one.

All durations use the boundaries in README. Queue and execution are nested in
embedding client time; backend observations are nested inside execution. Broker
preflight may load a model before the final embedding request, so backend load
duration alone does not capture all cold-start preparation. Diagnostics are enabled
for every run, so these are diagnostic-mode observations, not a comparison against
the earlier default-off cache benchmark. No speed threshold is a test assertion.

## 2026-09-22 results

Nearest-rank p50/p90, milliseconds; each row contains 34 requests:

| Run / pass | Search p50 | Search p90 | Queue p50 | Queue p90 | Execution p50 | Execution p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sequential 1 / first | 285.355 | 389.616 | 57.806 | 105.029 | 38.323 | 42.148 |
| Concurrent 1 / first | 402.779 | 2697.731 | 99.139 | 2214.614 | 33.419 | 42.617 |
| Concurrent 2 / first | 412.242 | 2786.416 | 113.410 | 2203.847 | 34.621 | 45.657 |
| Sequential 2 / first | 289.756 | 392.316 | 51.071 | 108.157 | 39.513 | 43.768 |
| Sequential 1 / repeat | 283.126 | 380.632 | 68.151 | 112.655 | 3.236 | 4.505 |
| Concurrent 1 / repeat | 354.322 | 424.082 | 118.306 | 157.454 | 3.297 | 4.813 |
| Concurrent 2 / repeat | 342.868 | 430.329 | 105.273 | 168.662 | 3.481 | 5.005 |
| Sequential 2 / repeat | 282.348 | 378.494 | 65.147 | 110.042 | 3.094 | 5.761 |

The repeated pass spans 10.036/10.028 seconds sequentially and 3.263/3.162 seconds
with four clients, measured from the first search start to the last search finish.
Higher throughput does not mean lower per-request latency. The concurrent first
pass has a long queue tail after fresh broker startup; the measurements alone do
not justify removing scheduler fairness or residency constraints.

Six cold/warm pairs, milliseconds (with six samples, p90 is the maximum):

| Confirmed condition | Search p50 | Search p90 | Queue p50 | Queue p90 | Execution p50 | Execution p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Model absent, uncached query | 5199.121 | 5552.463 | 2150.776 | 2209.477 | 2813.834 | 3081.248 |
| Model resident, immediate cached repeat | 278.288 | 295.698 | 69.011 | 95.766 | 3.709 | 5.412 |

Cold backend load p50/p90 is 2759.290/3021.848 ms. The six warm observations report
10,558,709,431 bytes of model VRAM residency; this is not total device usage or peak
occupancy. Warm cache hits have no backend durations. The routing receipt's WasCold
field is null here, so cold labels come from the explicit residency checks. Pure
prompt-evaluation duration is absent throughout; it is not reported as zero.

All 284 ordered/scored hit lists match the corresponding sequential first-pass
result: 272 full-corpus searches plus 12 subset searches. Both concurrent runs
reached exactly four overlapping searches. Full-corpus quality remains
recall@10=0.9666666667, precision@5=0.1933333333, mean first relevant rank=1.7666666667,
and no-answer false-positive rate=0.5. Subset quality reports are not substitutes
for full-corpus results. Legacy token estimates inside quality reports are not
measured cloud savings from this experiment.

`Save-LoadEvidence.ps1 -InputDirectory <raw-directory> -OutputDirectory <evidence-directory>`
validates complete runs, stable identities and policy, cache observations, actual
overlap, cold/warm residency and complete ordered/scored hit-list hashes. It omits
query text and unrelated installed-model inventory. Evidence is under
`evidence/2026-09-22-cold-concurrent`.

The issue remains open for alternating search/file/OCR retention measurements,
exact-symbol evaluation and further measured end-to-end improvements. These
observations identify remaining costs; they do not claim that #619 is complete.
