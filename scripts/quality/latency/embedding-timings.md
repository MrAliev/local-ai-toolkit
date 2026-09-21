# Embedding timing diagnostics (#619)

[Русская версия](embedding-timings.ru.md)

Set `LOCALAI_EMBEDDING_TIMINGS=1` in the broker process environment to capture
embedding timing diagnostics. It is off by default. Starting only a new client with
that variable does not change an already-running broker. Drain the current broker
before launching the measured binary, as described in [the cache experiment](query-cache.md).
All model calls still use the shared broker queue.

The broker preserves optional backend `total_duration`, `load_duration`,
`prompt_eval_duration` and `prompt_eval_count` fields. Durations are integer nanoseconds.
Missing, negative, fractional, out-of-range or nonnumeric values remain null. A load
greater than total, or prompt evaluation exceeding the remaining total, is discarded.
Usable vectors are still returned when optional metrics are invalid. A cache hit has
`CacheHit=true` and null backend measurements; it never repeats the original computation's
durations or token count.

The [Ollama embedding API](https://docs.ollama.com/api/embed) documents total and load
duration, plus input tokens. It does not promise a separate prompt-evaluation duration
for embedding. Capture that field only when supplied; **total minus load includes
backend overhead and is not a measurement of pure inference**. Model loading may be
small but nonzero for a resident model, so load time alone is not a cold/warm classifier.
Metrics describe the successful backend response; earlier attempts and broker preparation
are outside these backend durations.

Opted-in observations go to
`<runtime>/telemetry/embedding-timings/<job-id>.json`. They contain only schema version,
job ID, timestamp, cache status and numeric backend metrics: no query, vector, model
name, source path or answer. Files are written atomically. A failed diagnostic write
produces a `result-observe` diagnostic and does not fail a successful job. Failed or
cancelled executions do not produce successful-result observations.

This is a separate diagnostic format. Existing embedding bodies, response envelopes
and receipts are unchanged. Files are retained for the measurement operator to collect
or remove; disable the variable after the diagnostic session. No files are written by
this observer in the default mode.

The latency probe joins each observation to the existing receipt by JobId, after stopping
the search timer. Missing files mean unavailable diagnostics; malformed or mismatched
records fail the probe. Receipt execution ends before the optional diagnostic write;
the client/search wall clock includes that write. The sidecar read itself is outside the
search timer. All backend durations are nested inside execution, not additive to it.
The legacy probe `cacheHit` field belongs only to its optional client prototype; the
production broker observation is `backendTiming.backend.cacheHit`.

## 2026-09-22 validation

Source: `25e36df7265991b41645bff6436f5c06553b5390` plus this diagnostic change.
Two fresh processes of the same measured broker binary served the same runtime queue:
first diagnostics off, then on. The installed RC22 broker was restored afterwards.
No indexing, tests or other inference ran concurrently. Both used RequireFullVram,
10-second idle retention and the clean 34-case mixed corpus from the cache experiment.
Corpus, generation, model digest, source tree and binary hashes are recorded in the
evidence. Neither pass is a forced-cold model test.

Nearest-rank quantiles, milliseconds:

| Diagnostic mode / pass | Search p50 | Search p90 | Broker execution p50 | Broker execution p90 |
| --- | ---: | ---: | ---: | ---: |
| Off / first use | 286.747 | 388.065 | 39.285 | 41.408 |
| On / first use | 294.896 | 392.879 | 40.722 | 44.879 |
| Off / repeated | 280.254 | 290.056 | 2.940 | 3.693 |
| On / repeated | 285.231 | 378.278 | 2.835 | 4.939 |

This is a diagnostic-cost check, not a speedup claim. Overall latency changed and the
repeated-pass p90 increased; one sequential pair cannot separate write cost from
queue/polling variation. Keep diagnostics off for ordinary performance comparisons.

All 136 complete hit lists matched, including scores and ordering. The enabled run
recorded 34 backend calls and 34 cache hits; the disabled run had no records. Among
the 34 backend observations, total duration p50/p90 was 32.001/33.726 ms and load
duration 1.500/2.000 ms. Prompt evaluation duration was absent in all 34 observations;
input-token count p50/p90 was 45/51. No missing duration is reported as zero.

`Save-EmbeddingTimingEvidence.ps1 -InputDirectory <raw-directory> -OutputDirectory <evidence-directory>`
expects `off.json`, `on.json` and their probe sidecars. It verifies all 136 results,
identities, disabled diagnostics, 34 backend observations and 34 cache hits before
writing sanitized evidence. Results are under `evidence/2026-09-22-embedding-timings`.

Eleven deterministic tests cover transport extraction, missing/invalid metrics,
cache-hit attribution, record identity, strict vector-contract compatibility and
diagnostic storage failures. #619 stays open for forced-cold/concurrent and alternating
search/file/OCR retention measurements and exact-symbol evaluation. Cloud-token savings
are not claimed for this experiment.
