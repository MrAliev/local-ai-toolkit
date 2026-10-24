# What the command line gets

[Contents](README.md#contents) · [Русская версия](what-the-command-line-gets.ru.md)

Use `localai --help` or `codesearch --help` for the command list, and
`localai repo status --help` or `codesearch help search` for a command's syntax.
`help`, `--help` or `-h` before the command words, or `--help` or `-h` anywhere after them,
writes help to stdout and exits successfully; extra or unknown words after a leading `help`
are refused. `localai prune` and `localai update` also take `/?` as a request for their usage,
which they print to stderr with exit code 2.
Help follows the saved language or `LOCALAI_LANGUAGE` (`en` / `ru`). Existing JSON output
contracts are unchanged; `--json` requests a machine response rather than prose help.

The set is **not the same**: some tools exist only here, some only over MCP.

| Command | What it does |
| --- | --- |
| `codesearch search --query "…"` | the same search by meaning and by name |
| `codesearch get-chunk --id …` | pull a match in full |
| `codesearch status` | where the index is, whether it drifted, whether the repo is connected |
| `codesearch scan` | what would go into the index at all |
| `codesearch index` / `overlay` | build an index by hand, outside Local AI's management |
| `codesearch evaluate --cases …` | run the search-quality corpus |
| `localai semantic definition` / `references` / `implementations` / `relationships` | navigate by symbol |
| `localai semantic impact --path src/Type.cs --line 12 --json` | direct indexed C# references and incoming relationships |
| `localai ask "…" <file> …` | mechanical work over files |
| `localai triage <log>` | make sense of a log |
| `localai read-image "…" <file>` | read images or one PDF |
| `localai documents index` / `search` | build a PDF OCR index or find a literal phrase with source references |
| `localai translate --in <file> --from … --to …` | translate |
| `localai sync` | build or refresh the index |
| `localai hooks install` | install the hooks that keep the index current |
| `localai repo status` | whether a repository is connected |
| `localai doctor` | what is wrong with the installation, read-only |
| `localai diagnostics` | preview recorded diagnostics or save them as a ZIP |
| `localai hardware plan` | estimate model/context capacity from signed release metadata and this computer |
| `localai model status` / `pull` / `preflight` | models: installed, fetch, prove the fit |
| `localai policy show` / `set` | the model residency policy |
| `localai prune` | clear out stale generations and overlays, the queue archive, old telemetry, records of abandoned repositories and old versions |
| `localai telemetry` | a summary of local jobs |
| `localai update` | update the installation |

`localai sync --root <worktree>` prepares only the requested worktree, including when
the shared base changes. Use `--all-worktrees` explicitly to maintain every linked
worktree; the requested tree is processed first. `--base-only` builds no overlays.
`SYNC_BASE`
reports whether the base is reused; `SYNC_PLAN` lists the explicit scope, worktree count
and pending worktrees in processing order,
and `WORKTREE_PLAN` identifies reused or rebuilt overlays. `ACTIVE_READY` reports when
the requested tree can use the new generation, separately from the final sync duration.
Other worktrees continue using the previous generation until their overlays are ready.
With `--all-worktrees`, maintenance continues in the same sync process; do not start
another sync while it runs.
Admission uses one upper-bound estimate for the base and all selected worktree passes.
An interrupted run keeps completed worktrees usable and can be restarted. An MCP wait
timeout leaves the backend state unknown: observe `localai repo status --follow`.

`codesearch evaluate --cases …` resolves C# symbol targets to their source declarations
before querying. Split chunks such as `[1/6]` and `[2/6]` belong to a single target. Use the
optional `signature` and `namespace` target fields to disambiguate names; signatures that
still match multiple declarations are rejected rather than scored. Path-only targets
continue to accept any hit in the specified file. These changes affect evaluation scoring;
search ranking and the calibrated threshold remain unchanged.

Evaluation corpora may include an optional `excludedPaths` list of repository-relative file
paths and directory prefixes ending in `/`. These are literal paths, not globs; either slash
style is accepted. When the `--cases` file is inside the repository, it is excluded
automatically; list other reference material explicitly. Exclusions apply before both vector
and lexical ranking. Normal search continues to include ordinary tests.

Exclusion paths must stay inside the repository and must not cover a declared relevant
target. Evaluation output records the source commit/tree and dirty-content hash separately
from the indexed commit/tree, plus the index generation/model, corpus SHA-256, resolved
annotations, search options, and effective exclusions. A run fails if its source snapshot,
index generation, or annotations change while it is running. Reproducing a dirty run also
requires retaining the uncommitted source files; a hash alone cannot restore them.

The [retained candidate-scope baseline](https://github.com/MrAliev/local-ai-toolkit/blob/main/tests/CodeSearch.Tests/Fixtures/SearchEvaluation/baseline-candidate-scope-2026-09-16.json)
records Recall@10 72.2%, Precision@5 13.3%, and a 100% false-positive rate on six no-answer
cases. With one annotated target for each of the 18 answerable queries, Precision@5 has a
20% ceiling. These figures describe this 24-case corpus, not overall product quality;
thresholds remain unchanged. Concurrent broker work and unavailable queue/residency
telemetry make its timings unsuitable as an isolated performance benchmark. Independent
corpora were built in [#601](https://github.com/MrAliev/local-ai-toolkit/issues/601), now closed;
their baseline is under [Independent quality corpora](building-and-testing.md#independent-quality-corpora).

**Not on the command line.** `lsp_open_document` and `lsp_close_document` take the text of an
unsaved buffer — and in a shell there is no such buffer, so there is nothing to hand over.

**Not over MCP.** Seven commands are deliberately left to you, for three reasons:

**It already happens by itself — no need to call it.**

- `localai prune` — every successful sync removes the superseded generations, overlays and
  staging of the repository it synced, and says what it removed when it removed anything.
  `localai prune` does the same for every repository, and also clears the queue archive,
  telemetry past its retention, records of abandoned repositories, and old installed versions
  and launcher backups.
- `localai sync` — after a commit, checkout, merge or rebase the Git hooks installed by
  `localai hooks install` run it. The agent has `index_refresh`, which runs the same sync
  within a size bound: for edits saved but not yet committed, and after a commit no hook ran
  for. A cold build, or a branch switch that changes most of the tree, is refused with the
  command to run in the background instead.

**It is a product update, and it is run by hand.**

- `localai update` downloads the newest verified release, installs its binaries as a new
  version and switches the launcher to it. Models, prerequisites and client registrations are
  left as they are: clients reach the new version through the launcher they are already
  registered on. Run it when you choose to update. If the release also changes client
  instructions, run its `LocalAi.Installer.exe` with the relevant client selected: a
  runtime-only update does not rewrite those instructions.

**It is information for you, in a shape made for reading.**

The agent has no use for it in that shape — it gets a report line for every call it makes,
and `index_status` answers for the index. A person needs the wider picture:

- `localai doctor` — what is wrong with the installation: its version and files, the broker and
  its queue, the model catalog and whether each task has an installed model, the policy files,
  the last update check, and the repository you are in (or name with `--root`). Read-only; it
  starts nothing and repairs nothing.
  The repository row separates this worktree's usable index, generation and navigation from
  repository-wide state and sync progress. A completed worktree can be `Current` while another
  worktree's sync is incomplete. `Recent` means recent recorded progress, not proof a process
  is running; unfinished progress older than five minutes is `Interrupted`. Neither means all
  worktrees are ready. The JSON facts keep both scopes and the affected worktree explicit.
  Read-only semantic checks read at most 4 KiB: `HeaderOnly` confirms matching headers, not full
  corpus integrity or navigation readiness. That uncertainty is `Unknown` in the doctor row;
  actual navigation validates its snapshot before answering. An unreadable header or shared
  progress record is reported explicitly without discarding known worktree facts.
  Each check ends `Ok`, `Warning`, `Unknown` or `Failed`. The verdict is `Failed` if any check
  failed, else `Warning` if any warned, else `Ok`; the exit code is 1 only for `Failed`.
  `Unknown` means this machine holds no trusted, current answer to what the check asks, and it
  changes neither the verdict nor the exit code. `task-models` takes the installed models from
  a list the broker records at start, every 15 minutes while it runs, and whenever it picks a
  model for a task or answers `localai model status`; when that list is more than 30 minutes
  old, the check is `Unknown`. `--json` returns the verdict with the counts `failed`, `warned`
  and `unknown`; an `Ok` verdict can come with `unknown` above zero.
- `localai telemetry` — distinct jobs, attempts, known workflows, and confirmed delivered
  calls. Model/profile tables show attempt outcomes and durations; cloud input/output
  comparisons have separate totals and known/unknown coverage.
- `codesearch scan` — what would be indexed at all. You look at it while setting a repository
  up, to see the size of the job, not while asking a question about code.
- `codesearch evaluate` — the search-quality corpus. A tool for developing Local AI itself,
  not for working with your code.

---

← [What a cloud agent gets](what-a-cloud-agent-gets.md) · [Contents](README.md#contents) · [Code search is not text search](code-search-is-not-text-search.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
