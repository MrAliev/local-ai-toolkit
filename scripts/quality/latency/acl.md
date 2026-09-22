# Avoid rewriting already exact runtime ACLs (#619)

[Русская версия](acl.ru.md)

Every default broker client secures the Windows runtime tree. CodeSearch creates
such a client for each embedding request. The old implementation rewrote and then
validated every ACL, even when already exact. A live stage probe found this work
took 1440–1533 ms per call, versus approximately 290 ms for a repeated SearchAsync
using an existing client. Status rendering added approximately 390–410 ms.
These exploratory stage numbers identified the target; they are not the controlled
before/after comparison below.

`RuntimeAcl.Ensure` now reads each real ACL and repairs only a mismatch. It uses the
same validator for the initial check and post-repair verification. No verdict is
cached: each call still traverses the tree and checks every encountered object.
Protected explicit full-control entries for the current user and administrators,
exact inheritance/propagation flags, reparse-point refusal and moved-job handling
remain required. If the initial ACL read is denied, repair is attempted and its
result must pass the normal verification; other access failures are not ignored.
New paths and changed permissions continue to be repaired.

The change applies to Windows. It does not alter the non-Windows path, broker
queue/cache policies, search ranking or usage-report contracts. The installed RC22
runtime is not upgraded by this source change.

## Reproduce the complete MCP comparison

Before editing, build and save a complete `CodeSearch.Mcp/bin/Release/net10.0`
directory at baseline `f30b4d17243ec8a06c711b8eb614ecdd6543ceb9`. Build the candidate
and save a separate directory. Each needs its ordinary adjacent broker files so
client bootstrap resolves normally. Both clients use the already running installed
RC22 shared broker; do not replace or restart it between runs.

```powershell
python scripts/quality/latency/acl_probe.py `
  <clean-mixed-corpus> <cases.json> <baseline-client-directory> `
  <candidate-client-directory> <new-output-directory>
dotnet run --project scripts/quality/acl-identity/AclIdentity.csproj -c Release -- `
  <baseline-client-directory> <candidate-client-directory> <output-directory>/code-identity.json
python scripts/quality/latency/save_acl_evidence.py `
  <output-directory> <evidence.json>
```

Do not run tests, indexing, other inference or configuration/model changes alongside
the experiment. The order is baseline, candidate, candidate, baseline; each run
starts a fresh persistent MCP process and searches all 34 independent corpus cases
twice. The shared broker, OS caches and query-vector cache are not reset. Model
digests are not recorded here; no model replacement is permitted during the run.
Natural archive maintenance and growth of the runtime tree are not frozen, so this
is a live-runtime measurement on one Windows host, not a fixed-size ACL benchmark.

Timers include the full JSON-RPC round trip, search, status formatting and usage
reporting, but exclude process startup and index-status observations at run boundaries.
Parameters are topK=10 and maxPerFile=3. All displayed hits, their order, rounded
scores, signatures, chunk IDs and snippets must match for each query across all
eight passes; random untrusted-content nonces are excluded. Full-precision internal
scores are not exposed by MCP. This equality check does not replace the existing
independent quality baseline or improve its known no-answer limitations.

The evidence validator requires four complete 68-query runs, stable corpus commit,
index path, policy, broker process and per-variant binary hashes. Raw DLL hashes differ
between builds; the separate identity check verifies identical method instructions,
signatures, local signatures, exception regions, user-string literals and assembly
references in CodeSearch.Mcp, CodeSearch.Core and Broker.Client. It links that check
to the exact measured binary hashes. Git source changes affect RuntimeAcl only in
production. It retains per-query timings and displayed-hit hashes, omitting source
snippets and conditional token estimates. This benchmark claims no cloud savings.

## Correctness

The new operation-budget regression first failed with `ACL write budget exceeded`
on the old implementation. It reads real ACLs for every path of an already secured
temporary tree and allows zero writes. A second real-filesystem test changes one
child's trustees and verifies that only that child is repaired, with sibling content
unchanged. The recovery test verifies repair after an initial denied read and its
subsequent validation. Existing tests cover restrictive-DACL setter failures,
invalid entries/rights/inheritance, vanished jobs, junction escape and cycles.
No test asserts machine speed.

## 2026-09-22 results

Complete MCP calls, nearest-rank milliseconds; each row has 34 queries:

| Run | Pass | p50 | p90 |
| --- | --- | ---: | ---: |
| Baseline 1 | First | 2288.647 | 2365.065 |
| Baseline 1 | Repeat | 2321.931 | 2421.122 |
| Candidate 1 | First | 1291.216 | 1335.183 |
| Candidate 1 | Repeat | 1304.712 | 1338.877 |
| Candidate 2 | First | 1311.686 | 1347.376 |
| Candidate 2 | Repeat | 1344.489 | 1374.231 |
| Baseline 2 | First | 2576.704 | 2709.560 |
| Baseline 2 | Repeat | 2630.584 | 2730.452 |

Both candidate runs reduce full-query p50/p90. The baseline also drifts as the live
runtime changes; these observations establish the direction and measured values,
not a universal percentage guarantee. All 272 displayed result lists match across
the eight passes. No new model retention or VRAM allocation is introduced.
Validated evidence is in [2026-09-22-acl.json](evidence/2026-09-22-acl.json).
