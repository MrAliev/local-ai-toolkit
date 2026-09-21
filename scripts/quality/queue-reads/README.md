# Queue request-read measurement

[Русская версия](README.ru.md)

The probe measures `ListQueuedAsync` and a duplicate `EnqueueAsync` with 1, 10 and
100 queued requests. Each request contains either a short text or a base64 payload
representing 1 MiB of deterministic synthetic image bytes. Jobs are never executed;
the probe creates only private durable queue directories and calls no model/backend.

`Prepare-Probe.ps1 -Repository <checkout> -Revision <base-commit> -Destination <new-directory>`
archives the named revision into a new temporary directory, substitutes the checkout's
`DurableQueue.cs`, records its SHA-256 and instruments that temporary copy. It copies
this probe alongside the private source and builds it. Production code receives no
diagnostic hooks. Use the same base revision for before/after comparisons when only
`DurableQueue.cs` changes.

Run the printed executable with `<new-runtime-parent> <output.json> <source-sha256>`;
the source hash is in `queue-source.sha256`. Redirect stdout/stderr to a file. The fixture
setup is outside all measurements. Each operation warms up once, then records three
samples. The duplicate matches the last directory in the enumeration, requiring the
complete deduplication scan. Run before and after sequentially without other workload.

Instrumentation counts actual request deserializations and the corresponding on-disk
bytes, using file sizes captured before timing. Mutex hold time starts immediately
after acquisition and stops immediately before release; it includes maintenance,
validation and garbage collection, but excludes lock acquisition. Counters/timestamps
add a small identical instrumentation cost. Total elapsed time also includes lock
acquisition and method overhead. Managed allocation is the current-thread delta around
these synchronous queue operations, not peak memory or retained heap size. Timings and
allocations are diagnostics, not cloud input/output token savings.

The intended optimization reuses each validated request only while the same queue mutex
is held. The next operation reads it again, including after a failure. It does not trust
file size or modification time as proof of unchanged content, add persistent metadata,
change durable JSON contracts, or require SQLite. State and cross-file validation remain
in place. Regression tests cover a payload held by another reader after the initial scan,
same-size/backdated substitution and invalid payloads after a previous listing; existing
tests cover leases, deduplication and crash recovery.

The executable also accepts `concurrency <new-fixture-directory> <output.json>`. Four
independent processes start at a file gate and enqueue 80 requests with shared and unique
keys; validation requires 50 jobs and sequences 1..50. Four new processes then compete
for a lease; exactly one may acquire it and 49 jobs must remain queued. The two-minute
guard detects a hang rather than asserting machine speed. This uses no model.

The 2026-09-21 evidence is retained in `evidence/2026-09-21/`, assembled with
`Save-Evidence.ps1`. For 100 image requests, median mutex hold time fell from 2,729.61 to
708.59 ms for listing and from 2,029.84 to 678.11 ms for duplicate enqueue. Request reads
fell from 400/300 to 100/100, and managed allocations from 3.032/2.274 GB to 0.761/0.759 GB.
The full 1/10/100 text/image matrix is recorded, including variability in individual runs.
The cross-process check passed: 50 unique jobs, one active lease, 49 queued jobs.
