# Alternating search, file and OCR measurements (#619)

[Русская версия](retention.ru.md)

This is a diagnostic of the installed RC22 runtime, not a retention-policy change.
It measures complete MCP calls over persistent stdio clients through the shared
LocalAi broker. It does not call the backend directly or claim cloud-token savings.

## Reproduction

Use the installed launcher and the existing clean mixed evaluation corpus:

The installed runtime is located through the Windows known-folder API. Overriding
`LOCALAPPDATA` does not select another installation. These diagnostics require Windows
and stop if the known-folder lookup fails; they do not fall back to an environment path.

```powershell
python scripts/quality/latency/retention_probe.py `
  <clean-mixed-corpus> tests/Fixtures/OcrQuality/short-repeated-en.png `
  <new-output-directory-outside-corpus>
python scripts/quality/latency/save_retention_evidence.py `
  <output-directory>/raw.json <evidence.json>
```

The probe requires the unchanged 10-second idle policy. Do not run indexing, tests,
other inference, model replacement or configuration changes alongside it. It records
the broker PID/start/path, hashes of the three installed single-file executables,
fixture hashes, clean corpus commit and MCP server versions, and verifies stability
at the end. It does not force a broker restart or empty its query-vector cache.
An interrupted or incomplete run is not accepted by the evidence exporter.

After an initial search, each of three cycles performs: immediate repeated search,
file extraction, residency observation, repeated search, OCR, residency observation,
repeated search, 15 seconds without broker requests, residency observation and
repeated search. The same English weight-pricing query and two-line English fixture
are used throughout. A cycle starts immediately after the preceding cycle's final
search. This is a small repeated scenario, not full-corpus quality evaluation.

Every timer spans JSON-RPC request submission to the parsed response, including
MCP formatting and transport. Client initialization, residency observations and
idle waiting are outside search timers. Residency observations are real broker
jobs and add time/activity between operations; this is not an uninstrumented user
session. `timings` retains the server's reported nested boundaries and unknown
fields. Do not subtract them to label an unmeasured remainder as pure inference.

The exporter checks all 13 searches for identical displayed hits, including order,
rounded scores, signatures, chunk IDs and snippets; only random boundary nonces
are excluded. MCP does not expose full-precision scores here. It verifies current,
unchanged index paths and checks file/OCR content against the independent reference.
Extra text is recorded as a formatting deviation, never as exact-match success.
Model answers remain untrusted data and are omitted from the retained evidence.

## 2026-09-22 results

Nearest-rank milliseconds, three samples per row (p90 is the maximum):

| MCP operation / preceding condition | p50 | p90 |
| --- | ---: | ---: |
| Search / immediate repeat | 2073.629 | 2150.102 |
| Search / file task and residency observation | 4156.575 | 4377.715 |
| Search / OCR and residency observation | 4230.882 | 4243.418 |
| Search / 15-second idle and residency observation | 4201.182 | 4245.186 |
| File extraction | 7259.446 | 7396.252 |
| OCR | 28827.985 | 34414.383 |

All 13 displayed search results matched, with `WeightPricing.Quote` first. All
three OCR answers exactly matched the two repeated reference lines. All three
file answers preserved the reference once but added a heading/path: they failed
the requested exact-output format. Timing completion is not format correctness.

After each file task only `qwen3.5:9b` was resident, using 5,420,875,774 model VRAM
bytes; after each OCR only `qwen3-vl:8b-instruct-q8_0` was resident, using
9,908,917,370 bytes. Both were reported fully resident. Each OCR usage report names
both `glm-ocr:bf16 [offload_unknown]` and `qwen3-vl:8b-instruct-q8_0`; the combined
duration must not be attributed to a single model. After every idle interval the
resident list was empty. Search's embedding offload status was not recorded.

Immediate repeats report 38–94 ms queue time and 2–7 ms broker execution, yet
take about two seconds end to end. The other repeated-search groups report
2128–2214 ms queue time and 2–8 ms execution. These nested observations identify
both scheduler waiting and additional MCP-path overhead as investigation targets;
they do not identify the unmeasured remainder's implementation cause.

Validated per-call evidence, including the original usage notices, is in
[2026-09-22-retention.json](evidence/2026-09-22-retention.json). `AlreadyPresent`
deliberately excludes source-reading credit in this diagnostic: the notices are
conditional overhead estimates, not account consumption or verified savings.

## Interpretation

Longer idle retention cannot avoid eviction required by a competing model under
the existing residency policy. This experiment does not compare retention durations,
establish an optimal value or justify keeping multiple models in VRAM. Resident
bytes are point observations of model residency, not device-wide or peak usage.
No retention change is accepted from this small matrix.

The installed RC22 source revision is `25e36df7265991b41645bff6436f5c06553b5390`.
It includes query-vector caching but predates the later backend-timing diagnostic.
The observed index-memory-cache flag is not proof of a query-vector-cache hit.
Model digests and backend load durations are not exposed by these status responses;
they are not inferred from model names. This evidence must not be presented as a
measurement of the latest source build or combined with SearchAsync-only timers.

The overall #619 acceptance remains open pending investigation of remaining
MCP end-to-end overhead. Earlier full-corpus improvements and their quality checks
remain documented in the other experiments; this matrix does not replace them.
