# Documentation retention evidence

[Русская версия](README.ru.md)

Issue #623 adds explainable, opt-in cleanup to the shared-resource implementation in #680. `localai docs prune --root <repository>` is a read-only preview; `--apply` recomputes and executes the plan under the same lock as build, apply and pin. `--keep N` keeps at least N recent packages (default 5, minimum 1, ordered by manifest modification time with ID as a tie breaker).

Current packages, explicit pins, per-file applied origins, pending applies, latest artifacts, two valid artifact versions per type, and their shared resources/text checkpoints are additional protections. They can exceed the requested count. Pin a package before opening it with `localai docs pin --root <repository> --package <32-hex-id>`; unpin after closing it. Arbitrary browser tabs and external readers are not detected automatically.

Incomplete builds and applied baselines without matching provenance conservatively protect all history until a completed build or reconciled apply. Malformed protection metadata and reparse points refuse deletion. Unknown entries are preserved. Interrupted deletion moves owned directories out of the live graph before deleting individual files, so a later run can finish retired remnants. Generated worktree files are never cleanup targets.

## Reproduce

Run from the repository root with .NET 10. Use a new private fixture directory; the probe refuses an existing one and never loads the installed runtime or a model.

```powershell
dotnet run --project scripts/quality/documentation-retention/DocumentationRetentionProbe.csproj --configuration Release -- <new-fixture-directory> <result.json>
dotnet test --project tests/LocalAi.IntegrationTests/LocalAi.IntegrationTests.csproj --configuration Release -- --filter-class '*Documentation*'
dotnet test LocalAi.slnx --configuration Release --max-parallel-test-modules 1 --timeout 20m
```

The probe builds eight source revisions for 1, 5 and 12 types using deterministic valid draft replay. It pins revision 1, applies revision 2, and keeps two recent packages. Before/after refer to the same store immediately around cleanup, not to separate generation runs. It hashes every retained file and applied worktree file, checks required package/artifact links, verifies exact reclaimed-byte accounting and an unchanged dry run, and requires a repeated prune to reclaim zero bytes. A ninth revision followed by cleanup must keep the same package/artifact/text counts. Tests use controlled events for cancellation/concurrency; no assertion depends on machine speed.

## Recorded result

[Full plans and measurements](evidence/retention.json), [source hashes](evidence/source-hashes.json). Implementation baseline: `ca84edde34fa9fc3b1b43869a123c8c146f71c32`; the hashes identify the candidate files, normalized to LF. Random IDs and elapsed-time metadata can change individual byte totals between runs.

| Types | Before, bytes | After, bytes | Reclaimed, bytes | Retained packages | Artifacts / text checkpoints |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 3,387,048 | 2,416,833 | 970,215 | 4 | 4 / 4 |
| 5 | 11,082,877 | 6,271,316 | 4,811,561 | 4 | 20 / 20 |
| 12 | 24,583,647 | 13,033,194 | 11,550,453 | 4 | 48 / 48 |

All retained and applied file hashes remained unchanged. Four packages remain because the two older protected versions are additional to the two recent ones. After revision 9, these counts remain constant. Explicit pins, distinct applied versions, new types and incomplete legacy state can still grow storage; this is a retention policy, not a hard byte quota.

The measurement boundary is logical file lengths inside the documentation store, including its metadata and one shared CSS. It excludes filesystem allocation, the applied worktree copy, indexes, model quality and latency. Model invocations: **0**. Cloud-token savings: **not applicable**. Shared fonts and standalone HTML have separate evidence in [documentation-resources](../documentation-resources/README.md); no browser visual acceptance is claimed by this probe.

The initial CLI preview test failed on the baseline with exit 2 instead of 0. After implementation, all **136 documentation tests passed**, with no failures or skips. Full-suite results are recorded in the pull request.
