# Active worktree readiness probe

[Русская версия](README.ru.md)

This probe compares the time until the requested worktree can select a complete new
generation with the time until all repository maintenance finishes (#620). It generates
an isolated Git repository: 32 Markdown files of 128 lines, main and active at the new
commit, and another worktree at the old commit. Both immutable bases are built before
measurement. Each measured run restores the same previous pointer and removes only the
probe's new-generation overlays and worktree publication files. It never modifies the
user's runtime. All embedding requests go through the shared LocalAi broker.

Build this project with `dotnet build -c Release`. To compare another revision, copy
`Program.cs` and the project into a temporary directory and build with
`-p:SourceRoot=<absolute-path-to-that-revision>`. The probe requires the broker runtime
beside its executable; `BrokerBinary.props` copies it during development builds.

Run `SyncReadinessProbe prepare <new-fixture-directory> <setup.json>` once. Run each
compiled implementation as `SyncReadinessProbe measure <same-fixture-directory> <result.json>`.
Set `SYNC_PROBE_REVISION` to the measured revision. Redirect stdout/stderr to a file.
Run implementations sequentially in alternating order, with no other broker work.
The fixture marker and source identity are checked before any artifact reset.

The stopwatch starts just before dispatching the sync and includes Git inspection,
planning, publication, overlay work and retention. An independent reader checks the
selected base through the implementation's normal resolver, with 100 ms pauses between
checks. `LastNotReadyMs` / `ActiveReadyMs` bound observed readiness; polling and filesystem
caches affect timings. `FullMaintenanceMs` stops when ExecuteAsync returns. Search
validation and model identity queries happen outside these timings. The two durations
overlap and must not be added. The probe also verifies both worktrees' search results,
the final global pointer and unchanged source identity.

This is a warm, prebuilt-base scenario, not a cold full build, a transport benchmark,
or a claim that embedding becomes faster. Broker batching/deduplication and model
residency remain enabled. A transport observer timing out does not cancel or prove
failure of a separately running sync. Observe that job instead of starting a duplicate.
Token savings are not measured by this timing experiment.

The retained measurements are in `evidence/2026-09-21/`.
Use `Save-Evidence.ps1 -InputDirectory <results> -OutputDirectory <evidence>` to check
the source/model identities, pointer states and retrieval invariants, and retain all six
samples. On 2026-09-21 the median observed active readiness was 28,569.22 ms before and
1,321.56 ms after; complete maintenance was 29,110.16 ms and 29,118.55 ms. The active
top five and other-worktree top three hits matched. The other-worktree fifth hit varied
between two chunks in both groups after re-embedding; the raw outputs preserve this.
