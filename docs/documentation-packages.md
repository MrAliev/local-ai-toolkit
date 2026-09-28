# Documentation packages

[Contents](README.md#contents) · [Русская версия](documentation-packages.ru.md)

```powershell
lai localai docs build --root R:\YourRepo           # up to 5 public C# types
lai localai docs build --root R:\YourRepo --limit 20
lai localai docs build --root R:\YourRepo --all
lai localai docs build --root R:\YourRepo --standalone-html
```

The semantic index must match the source files; run `lai localai sync --root R:\YourRepo`
after changing code. A unit is one precisely indexed public C# type, including its members
and partial declarations. Type selection follows source path and position. Other indexed
languages can supply references, but do not become documentation units in this version.
Preparation reads and parses candidate files until the requested number of public types is
selected, then also reads their partial declarations and required indexed definitions.
Git/index freshness checks still cover the worktree; `--limit` does not bypass them.

The first scope pilot selects a literal repository-relative file or directory:

```powershell
lai localai docs build --root R:\YourRepo --path src/YourLibrary --plan --json
lai localai docs build --root R:\YourRepo --path src/YourLibrary --all
```

`--path` is case-sensitive, accepts either slash, and matches complete path segments;
it does not expand globs or accept absolute paths or `..`. Selection happens before
the default five-type limit (`--limit` or `--all` can change it). Partial declarations
and required indexed dependencies can lie outside the selected path. An empty selection
is reported as `scope_empty`. Symbol and commit-difference selectors are outside this pilot.
`--plan` lists selected symbol IDs, declaration paths and dependency paths without model
inference or creating a documentation package. Its JSON also includes the repository
snapshot and source/reference/history hashes, so the selection can be checked against
the indexed sources. A scoped build prints its plan before generation. Changed dependency
definitions invalidate the affected text cache; applying a scoped package preserves manual
edits and leaves other units in the destination untouched. Existing commands and MCP
interfaces retain their behavior; this pilot adds CLI options only.

Build writes under the runtime's `repositories/<repository-id>/documentation` directory,
outside the worktree. The identity is the same Git common-directory identity used by the index.
Each unit contains EN/RU Markdown, HTML and PDF, SVG/PNG diagrams, and third-party notices.
By default HTML pages share one content-addressed stylesheet in `artifacts/_assets` with
both fonts embedded as data URLs, avoiding cross-file font requests when opened locally.
One copy serves every unit and retained package. `docs apply` copies this resource once
to `docs/generated/_assets`, preserving relative links. Keep this directory with the pages.
Use `--standalone-html` to embed fonts, the diagram and license notices in each HTML page
for copying individually; the link to the other language still needs that other page.
Changing this option rerenders validated text without another model call. Resource hashes
are checked before apply, and edited destination stylesheets are preserved and reported like
other manual edits. A build can repair a missing or damaged runtime stylesheet from bundled resources.
Open `index.en.html` or `index.ru.html` in the printed package directory: changes and their
reasons, failures with the fate of the previous document, a reuse count, and the contents.
Review the prose and translation for meaning; automatic checks prove references and syntax.

Source bodies, indexed references/relationships and related definitions, and the last ten
commit messages scoped to those files are hashed separately. Unchanged units reuse the exact
artifact bytes without a model call. Validated bilingual text is cached separately: a changed
snapshot label or main source coordinate rerenders it with current details, and a damaged
rendered file can be rebuilt without another generation call. A failed unit keeps its previous valid document, if one exists, and is
named in the report. Completed units survive an interrupted build. `--json` returns the report;
**check its `failed` count**: a completed report can contain failed units. Retained artifacts
kept after a failure keep their original snapshot labels and are marked in the report.
Within a repository's record, documentation history is cleaned only by an explicit
`docs prune --apply` command. The record itself can still go: when `localai prune` removes an
abandoned repository record, or the uninstaller removes repository indexes, its documentation
goes with it.

```powershell
lai localai docs pin --root R:\YourRepo --package <package-id>  # before opening a package
lai localai docs prune --root R:\YourRepo --keep 5 --dry-run
lai localai docs prune --root R:\YourRepo --keep 5 --apply
lai localai docs unpin --root R:\YourRepo --package <package-id> # after closing it
```

The package ID is the 32-character directory name printed by build. Pin packages before
opening them and unpin them after closing: arbitrary browser tabs and external readers
cannot be detected automatically. Prune without `--apply` is a preview. It lists every
candidate's bytes and keep/delete reasons; execution recomputes the plan under the same
lock used by build, apply and pin. Default retention is the five most recently written
package manifests (minimum one), plus current, pinned and applied versions. Latest type
checkpoints and the two most recent valid artifacts per type are always retained, along
with referenced text and shared resources. Explicit protections can exceed `--keep`.

Apply records each managed file's source package, including partial applications and
preserved manual edits. Legacy or incomplete provenance preserves all history until a
successful apply reconciles every old baseline entry. An interrupted build likewise
preserves history until a build completes. Cleanup refuses reparse points and malformed
protection metadata. It never touches the worktree; unrecognized entries are preserved.
Directories interrupted during deletion are retired atomically and reclaimed on the next
run. See [retention measurements and checks](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/quality/documentation-retention/README.md).

Text checkpoints are keyed by source/reference/history hashes, generator facts/graph, and
explicit prompt, validator and route versions. The route identity also includes the pinned
overview model sequence and the validated routing catalogue, including content changes that
leave `catalogVersion` unchanged. Renderer versions and the full current presentation input
invalidate only rendered files. Changes to coordinates embedded in cited facts still invalidate
the text. Old render-only caches remain readable and rebuild once on the next build.
See [cache versions and measurements](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/quality/documentation-cache/README.md) for
the version-bump rules, recovery behavior, and measurement boundaries.

Start with the default subset and use its measured elapsed time before choosing `--all`.
Generation uses the shared broker, its queue/progress and the configured residency policy.
Build does not download models or relax that policy. Overview generation uses `gpt-oss:20b`
with one repair attempt, then `qwen3.5:9b`; Russian translation uses the configured
`PlainTranslation` route. The underlying renderer and measured model comparison are described
in [the diagram benchmark](https://github.com/MrAliev/local-ai-toolkit/blob/main/benchmarks/documentation-diagrams/README.md).

After reading the package:

```powershell
lai localai docs apply --root R:\YourRepo
```

Apply copies the available documents to `docs/generated/<symbol-hash>/`, runs no model and
creates no commit. It skips files edited since the previous apply and prints their paths;
files left by a different subset are retained. If sources or HEAD changed since build, build
again before applying. Symbolic links and junctions in the destination are refused. Review
the resulting Git diff and commit the accepted files together. No documentation hook is installed.

---

← [Impact evidence: C# pilot](impact-evidence-c-pilot.md) · [Contents](README.md#contents) · [Building and testing](building-and-testing.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
