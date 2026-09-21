# Broker query-vector cache (#619)

[Русская версия](query-cache.ru.md)

The broker reuses a vector for a repeated interactive CodeSearch query. It validates
the current model digest through an internal Tags request in the same broker job.
The key hashes the exact model name, digest, prepared input (including instructions),
requested context and cache schema version. A model tag alone never permits reuse.
Indexing, native embedding and batches do not use this cache.

Unknown, duplicate or unreadable identities bypass reuse. A miss checks the digest
again after embedding; a changed identity does not populate the cache. Failed,
cancelled, zero or otherwise unusable vectors are not stored. Waiting cancellation
does not cancel an active query. Entries live only in the broker process and use LRU
eviction at 128 entries or 8 MiB of serialized vector results. This is a payload limit,
not a limit on total process memory. Oversized vectors are returned without caching.
Digest checks observe backend identity; they cannot make an external model replacement
transactional with inference.

Only vectors are reused. Every SearchAsync call still inspects the repository and
selects its current generation/overlay before ranking. No result list, source snapshot
or snippet is cached here. Residency policy and queue scheduling are unchanged.

## Measurement on 2026-09-22

Baseline: `66b6f5a7180fc05da5ae79e3c1ce5a49cf39a1f9`. Candidate: that revision
with the query-vector cache. The baseline broker was published before the edit.
The probe captures SHA-256 of the broker executable, broker assembly, broker core
and search core; hashing only an apphost would not identify its implementation.

The four runs used baseline, candidate, candidate, baseline order. Each began with a
fresh broker process serving the same shared runtime queue. The current broker was
asked to drain using shutdown.request addressed to its PID and start time, then the
selected executable was launched with `serve --runtime <shared-runtime>`. Its host.json
PID and executable path were checked before and after each run. The installed broker
was restored afterwards. No concurrent tests, indexing or other inference ran.

Each run used the existing probe command without the optional `cache` argument:
34 cases, two passes. The old client-side prototype was disabled. The first pass
starts with an empty broker cache; the second repeats the queries. Neither forces
a cold backend model. Policy stayed RequireFullVram with 10-second idle retention
and MaxEmbeddingBatchItems=128. The clean mixed corpus and generation are the same
as the earlier snapshot experiment in README.md: 87 files / 227 chunks, corpus
SHA-256 `0990bd3f1e0c1041c7c633913126b3de57adb8a3d7d413180c905eb43d5cf994`,
model `qwen3-embedding:8b-q8_0`, unchanged digest
`9704fd987c12aa746934ea9f99dc85527c83c6e4b98b3a10b94689332ee866bb`.

Warm pass, nearest-rank quantiles, milliseconds:

| Run | Search p50 | Search p90 | Broker execution p50 | Broker execution p90 |
| --- | ---: | ---: | ---: | ---: |
| Baseline 1 | 270.77 | 374.80 | 34.39 | 37.62 |
| Candidate 1 | 258.96 | 266.70 | 3.16 | 4.35 |
| Candidate 2 | 258.13 | 356.21 | 3.13 | 4.40 |
| Baseline 2 | 258.81 | 366.06 | 34.86 | 35.78 |

The reproducible improvement is broker execution for repeated queries. End-to-end
median improvement is small, especially in the second pair; queue/polling variation
dominates and the p90 improvement varies. First-pass execution p50 increased from
32.63/35.62 ms to 41.82/42.02 ms because misses validate identity twice. This tradeoff
does not promise faster one-off queries. Execution still includes metadata and any
model loading, not just inference. Backend load/inference remain unseparated.

All 272 complete hit lists matched baseline, including scores and order. Every pass
kept recall@10=0.9666666667, precision@5=0.1933333333, mean first relevant rank=1.7666666667
and no-answer false-positive rate=0.5. Existing no-answer quality is not improved.
No cloud-token savings are attributed to this benchmark.

`Save-QueryCacheEvidence.ps1 -InputDirectory <raw-directory> -OutputDirectory <evidence-directory>`
expects baseline-1, candidate-1, candidate-2 and baseline-2 probe JSON files and their
sidecars. It verifies all 272 retrievals and stable source/corpus/model/client identities,
then saves per-query timings, hit hashes and quality reports. Checked-in evidence is
under `evidence/2026-09-22`.

Sixteen deterministic transport tests cover identity/input/context changes, failure,
cancellation, concurrent reuse and both eviction limits. Full cold/concurrent backend
measurements, alternating search/file/OCR retention, exact-symbol semantics and separate
backend timing remain in #619; this increment does not close that issue.
