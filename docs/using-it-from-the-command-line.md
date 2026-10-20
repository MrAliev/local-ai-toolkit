# Using it from the command line

[Contents](README.md#contents) · [Русская версия](using-it-from-the-command-line.ru.md)

**On this page**

- [Step 1. How to run anything](#step-1-how-to-run-anything)
- [Step 2. Where to run it from](#step-2-where-to-run-it-from)
- [Step 3. Connect a repository](#step-3-connect-a-repository)
- [Step 4. Use it](#step-4-use-it)
  - [Find code by meaning](#find-code-by-meaning)
  - [Walk from a symbol](#walk-from-a-symbol)
  - [Ask about files you already know](#ask-about-files-you-already-know)
  - [Triage a log](#triage-a-log)
  - [Read an image, translate a document](#read-an-image-translate-a-document)
  - [Look at the state of things](#look-at-the-state-of-things)
  - [Estimate capacity on the target computer](#estimate-capacity-on-the-target-computer)
  - [Prepare and verify transferred offline materials](#prepare-and-verify-transferred-offline-materials)
  - [Plan the offline environment and its known disk budget](#plan-the-offline-environment-and-its-known-disk-budget)
  - [Apply a bundled dependency or import model artifacts](#apply-a-bundled-dependency-or-import-model-artifacts)
  - [Execute selected offline steps with a durable journal](#execute-selected-offline-steps-with-a-durable-journal)
  - [Install pinned SCIP packages from an offline npm cache](#install-pinned-scip-packages-from-an-offline-npm-cache)
  - [When something refuses](#when-something-refuses)

## Step 1. How to run anything

Everything goes through the **launcher** — the stable entry point that does not move when the
product updates. The version directory under `bin\versions\` changes with every release, so
never write a path into it, neither in a script nor in a client:

```powershell
& "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run <command>
```

To stop repeating that, make an alias for the session:

```powershell
function lai { & "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run @args }
```

The examples below use `lai <command>`.

## Step 2. Where to run it from

**From anywhere.** Commands that work on a repository take `--root`:

```powershell
lai localai repo status --root C:\work\myapp
```

With no `--root`, the repository containing the current directory is used. So this works too:

```powershell
cd C:\work\myapp
lai localai repo status
```

**A worktree is the same repository.** A repository is identified by its shared git
directory, so all of its worktrees, Claude, Codex and the command line see one index. But for
a build, point `--root` at **the tree you are editing**: an overlay is built for one exact
state of the files.

## Step 3. Connect a repository

Indexing is opt-in per repository. Until you ask, nothing is read.

```powershell
lai localai repo status --root C:\work\myapp
```

```
NOT_CONFIGURED: C:\WORK\MYAPP\.GIT — offer CodeSearch, LocalLm, shared broker, mainline generations, branch overlays, hooks, Claude MCP and Codex MCP.
```

Connect it and install the hooks:

```powershell
lai localai sync --root C:\work\myapp
lai localai hooks install --root C:\work\myapp
```

The first build reads the whole repository and prints its progress in figures: chunks
embedded out of the total, the rate and an estimate of the minutes left. You do not have to
watch the window: `localai repo status --follow` shows the phase and its figures while it
runs.

From then on the hooks keep the index current by themselves: after a commit, checkout, merge
or rebase. Hooks you already had are not overwritten — Local AI joins the chain and calls them
first.

Check:

```powershell
lai codesearch status --root C:\work\myapp
```

## Step 4. Use it

The full list of commands is in the [What the command line gets](what-the-command-line-gets.md)
table. Here is what they look like in use.

### Find code by meaning

```powershell
lai codesearch search --query "who is subscribed to a broker job finishing" --top 3
```

What it printed on 2026-09-06; line numbers and scores move as the code changes:

```
src\LocalAi.Broker.Core\DurableQueue.cs:1318-1323  [Method]  cos=0.616
  IBrokerQueue.CompleteAsync
  Task CompleteAsync( Guid jobId, string workerId, Guid leaseId, JsonElement body, …)
  chunk_id: cs1.AUAwZWNjOTAx…

src\LocalAi.Contracts\LocalRunObserver.cs:27-27  [Type]  cos=0.604
  BrokerJobPending
  public sealed record BrokerJobPending(bool Running): LocalRunStep
  chunk_id: cs1.AUAwZWNjOTAx…
```

The word "subscribe" appears nowhere in the code that was found — it matched by meaning.
`cos` is how close; `chunk_id` goes to `codesearch get-chunk` to pull the fragment in full:

```powershell
lai codesearch get-chunk --id cs1.AUAwZWNjOTAx…
```

### Walk from a symbol

Once you have a place, keep going with navigation rather than another search. Lines and
columns count from one — exactly as search prints them and an editor shows them. Pass the
start line a hit prints; for `CompleteAsync` that is line 1392 today:

```powershell
lai localai semantic references --path src/LocalAi.Broker.Core/DurableQueue.cs --line 1392 --column 1
lai localai semantic implementations --path src/LocalAi.Broker.Core/DurableQueue.cs --line 1392 --column 1
```

### Ask about files you already know

```powershell
lai localai ask "List the public members of this class, one per line." src/CodeSearch.Core/Chunking/GenericTextChunker.cs
```

For extraction/classification, opt into a checked flat JSON object with
`--profile Extraction` (or `Classification`) and `--schema contract.json`.
MCP `ask_local` accepts the same JSON as the optional `outputSchema` string.
For example, classify a supplied issue into `bug` or `question` with this contract:

```json
{"schemaVersion":1,"fields":[{"name":"label","type":"String","allowedValues":["bug","question"]}]}
```

```powershell
lai localai ask "Classify this issue." issue.txt --profile Classification --schema contract.json --json
```

Version 1 is a bounded flat-object contract, **not general JSON Schema**: up to 64
case-sensitive fields, `String`, `Integer` (signed Int64), `Number`, or `Boolean`;
`required` defaults to true, `allowedValues` is an exact string enum. Nested values,
nulls, extra/duplicate fields, code fences and extra prose fail validation. The schema
is limited to 32,768 characters and the response to 1,048,576 characters. Unknown
schema keywords are rejected before inference. Cancellation is preserved.

The broker checks each attempt, including fallback. With `--json`, success adds
`data.validation`, and a rejected result exits 65 with `error.code=result_rejected` and
`error.validation` and no answer data. Without `--json`, success prints `validation: <status>`
on stderr, and a rejected result exits 65, prints `result_rejected:` with the validation on
stderr and nothing on stdout. MCP returns `{answer,validation}` inside its untrusted-content
boundary. Statuses are `Valid`, `Empty`, `InvalidJson`, `SchemaMismatch`; only `Valid`
carries a parsed `value`. Rejected output earns no cloud-token savings estimate.
**Valid means the structure matches, not that the classification or extracted facts
are correct**: check them against the supplied source. Without a schema, legacy
CLI/MCP output is unchanged and the broker records `NotRequested`. This opt-in feature
requires an updated broker as well as the client. Full translation retains its own
validator; a broker success does not replace it.

The model's answer is written to stdout. A separate usage notice on stderr identifies the
call and the models actually used, reports elapsed time, and shows one approximate token
saving (or additional overhead). The underlying INPUT and OUTPUT values remain in the report.
Runtime calls use a **conditional first-read comparison**, not account billing:
INPUT is the tokenized source units minus the arguments and model-visible response;
OUTPUT is the validated, delivered local answer minus the generated tool request. Either
difference can be negative; an unsuccessful, invalid or undelivered result earns no savings
estimate. A later cloud answer and transport framing outside that payload
are excluded from both sides. In methodology `cloud-comparison-v2`, MCP counts the tool name
and arguments as JSON, decoded result text (including the usage notice), and structured result
JSON, separated by newlines. JSON uses literal Unicode rather than transport escape sequences.
Hidden `_meta` in requests and results is excluded. Transport byte/character volumes still
measure the complete serialized result, including metadata. Non-text result blocks make the
response token estimate unknown; their encoded bytes are not treated as text tokens.
CLI counts serialized arguments and stdout followed by stderr, including the usage notice.
These representations are explicit proxies, not a measurement of a client's model context:
client-specific framing and deduplication of text/structured results are unknown.

Text uses the bundled `microsoft-ml-tokenizers-2.0.0:o200k_base` vocabulary, checked against
independent reference counts. Images use `openai-gpt-4o-tiles-v1` at high detail; PDF comparisons
use the rendered page images, not a provider's native PDF upload. Neither profile claims to
match the cloud model used by your account. Duplicate source units within a call count once;
triage counts unique fragment text without overlap. Search compares reading only the files
behind valid hits, not the entire repository. Missing evidence remains unknown with a reason;
administrative operations and code-only translation report not applicable.

The default `NotPresent` assumption means the source has not entered the cloud context.
For an already read source, pass `sourceContext: "AlreadyPresent"` to the supported MCP tool,
or `_meta: {"localai/sourceContext":"AlreadyPresent"}` on the request. Use `"Unknown"` if
context presence cannot be established. CLI uses `LOCALAI_SOURCE_CONTEXT` with the same values.
There is no cross-call context ledger: callers must supply this information on repeated reads.
Only counts, profile IDs and the scenario are persisted, never source/answer text. CLI response
capture is bounded to 8 Mi characters per stream; exceeding it makes the response token count
unknown. Existing JSON fields remain compatible; the canonical usage report and notice carry
this comparison, while older task-specific scalar estimates retain their legacy meaning.
Evidence is bounded to 16,384 distinct source units per call; longer streams report
`source_evidence_limit` instead of crediting a partial baseline.

### Triage a log

```powershell
lai localai triage build.log --question "why did the build fail"
```

Reads a log file of any length. It goes through the models the routing catalog names for log
triage in route order — candidates, then fallbacks — and for each installed one trial-loads its
context sizes from largest to smallest, up to the limit in `log-triage.json`, until the broker
accepts a load (by default, only when the model loads entirely into video memory). It then
works through the file in pieces sized to the accepted context.
It takes a stream too, so the log need not be saved at all:

```powershell
dotnet build 2>&1 | lai localai triage -
```

### Read an image, translate a document

```powershell
lai localai read-image "What error is on this screenshot, and in which window?" C:\shots\error.png
lai localai translate --in docs\notes.md --from Russian --to English --markdown --out docs\notes.en.md
```

`read_image` and `localai read-image` also accept one PDF, up to 256 MiB.
Pass its path without other files and without `--together`. Pages are rendered
and sent to the model one at a time. In OCR mode, each page's reading is returned
in full, in source order, under a numbered page heading. Other modes merge the
page answers. The console reports the current page and total; the final notice
names the file and the number of pages read. PDFs have no eight-page limit.

Use `--pages "1,3-5"` (MCP: `pages: "1,3-5"`) to read selected PDF pages.
Numbers start at one and ranges include both ends. Pages are processed in document
order without duplicates, keeping their original numbers in headings and progress.
Only selected pages are rendered. Invalid ranges or numbers beyond the document
are refused before calling a model. Omit the option to read every page.

```powershell
lai localai read-image "Transcribe the text" manual.pdf --profile Ocr --pages "1,3-5"
```

For PDF OCR, add `--out transcript.txt` (MCP: `outPath`) to save the complete UTF-8
transcript and receive a short file pointer. The output directory must already
exist. An existing output file is replaced atomically after every selected page
finishes; errors or cancellation keep the old file. The source PDF cannot be the
output. Nothing is silently shortened or returned inline on a write failure.
File contents remain untrusted data. Without an output path, OCR returns the
transcript inline as before.

Add `--checkpoint ocr-progress` (MCP: `checkpointDirectory`) to resume PDF OCR.
Repeat the same request after a stop: completed pages are loaded from disk without
rendering or calling a model again; only unfinished pages are processed. A fully
completed session can publish the transcript to another output file without inference.
The checkpoint contains page text and receipts, and allows only one active writer.
Use an empty directory for a new session and keep the output file outside it.
Changes to PDF bytes, selected pages, question, language, model override, routing
catalog or processor build are refused; use a new directory for those requests.
Each checkpoint record is limited to 4 MiB; oversized records fail without truncation.

```powershell
lai localai read-image "Transcribe the text" manual.pdf --profile Ocr --checkpoint ocr-progress --out transcript.txt
```

Pages are rendered at up to 150 DPI, with at most 4 million pixels per page.
Encrypted or malformed PDFs report an error with the file path; password input
is not supported.

See the [PDF parser and OCR measurements](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/quality/pdf/README.md) for separate
text-layer, damaged-document and raster-limit checks, plus the limits of that evidence.

Document search indexes **PDF, TXT, Markdown** (`.md`, `.markdown`) **and DOCX**. Keep the index
outside the source folder; only supported files directly in that folder are included.
PDFs still use OCR by default. Other supported formats are read directly without a model.

```powershell
lai localai documents index --source C:\Documents\Scans --index C:\Documents\SearchIndex
lai localai documents index --source C:\Documents\Text --index C:\Documents\TextIndex --pdf-mode TextLayer
lai localai documents search --index C:\Documents\SearchIndex --query "invoice number" --limit 10 --json
```

MCP equivalents are `index_documents(sourceDirectory, indexDirectory, model?, pdfMode?)` and
`search_documents(indexDirectory, query, limit?, sourceContext?)`. Search finds a
case-insensitive literal phrase, returns at most one excerpt per source unit, and makes no
model call. Every PDF hit identifies the file, its SHA-256 version, one-based page,
normalized whole-page region `(0,0,1,1)`, extraction kind and source excerpt.
OCR hits also name the extraction model and mark transcription accuracy unverified.
`ocrOffset`, `matchLength` and `textOffset` count UTF-16 code units in the page's OCR
text; they are not coordinates of words in the PDF. No paraphrases or generated answers
are mixed with that text. OCR can misread or omit text; a missing match proves only
that the phrase was absent from the indexed OCR.

Explicit `--pdf-mode TextLayer` (MCP `pdfMode: "TextLayer"`) reads embedded PDF text
with PdfPig 0.1.16. It **never falls back to OCR**: pages with no extracted text are
listed in the index result's `emptyPages`; reindex with `--pdf-mode Ocr` to recognize
page images. Embedded text can contain hidden characters or a different reading order.
For these formats, `textKind` distinguishes `ocr`, `pdf-text` and `plain-text`; `textVerification` is
`unverified`, `layout-unverified` or `source-text` respectively. Text-layer extraction
does not claim a match to the visible page.

TXT/Markdown hits have null `page`, `region`, `model` and `ocrOffset`; their one-based
`startLine`/`endLine` describe the excerpt and `matchStartLine`/`matchEndLine` describe
the match. `matchOffset`, `textOffset` and `matchLength` count UTF-16 code units in the
decoded text file or extracted PDF page. Text is preserved, including line endings;
UTF-8 and BOM-marked UTF-16/UTF-32 are supported. Invalid encodings and NUL-containing
input are refused. Plain-text search returns at most one excerpt per file.

DOCX search reads main-body paragraphs and paragraphs in top-level table cells.
It joins adjacent text runs, preserves tabs and explicit breaks, and returns at most
one excerpt per paragraph. `textKind: "docx-text"` and `textVerification: "layout-unverified"`
identify this extraction. Each hit's `docxSource` contains the declared main `part`,
`namespaceUri` for the `w` XPath prefix, and an exact `xpath`. One-based `paragraph`
counts direct paragraphs in the body or cell; `table`, `row` and `cell` count direct
XML siblings and are null for body paragraphs. Merged-cell positions describe XML,
not visual grid columns. File SHA-256 pins these references to that version.
`page`, `region`, `model`, `ocrOffset` and line fields are null. UTF-16 offsets refer
to the extracted paragraph. Pages, numbering, styles, hidden text and reading order
are not rendered or verified; text from hidden runs can be present.

DOCX extraction is deliberately partial. A paragraph containing unsupported inline
content (such as fields, drawings, notes or tracked revisions) is omitted in full
to prevent false phrase matches across a skipped fragment. Unsupported blocks,
content controls and nested tables are omitted. Header/footer, note, comment, style,
media and other unindexed package parts are listed in `extractionNotices`, with exact
part names and structural locations where available. External relationships are
reported and never fetched; hyperlink display text can still be read. Notices are
limited to 64 per file; `additionalNotices` counts the remainder across files.
Both index and search return notices, including searches with no hits. Search includes
notices only for sources that still pass its freshness checks. No match does not prove
the phrase is absent from the complete Word document.

Repeat `documents index` to refresh. Unchanged documents are reused; changed documents
are re-read and deleted entries disappear. Changing PDF mode invalidates extraction
reuse. Version-one and version-two indexes remain searchable; refresh atomically upgrades storage
to version three. The CLI/MCP response schema remains version one with additive fields;
`indexSchemaVersion` names the stored format. Search checks file hashes before returning
hits and reports changed, deleted or unreadable sources in `excludedSources`. Refresh
after changes; newly added files become searchable after that refresh. Publication is
atomic and cancellation keeps the previous snapshot; interrupted new OCR work is
repeated on the next run. Stored text remains local in `snapshot.json`. Its page hashes
detect accidental corruption, not deliberate tampering by someone able to rewrite it.

Pilot limits: 32 files, 128 pages per PDF, 256 MiB per PDF, 8 MiB per other file, 1 GiB
in total, 64,000 extracted characters per PDF page and 8 million in the index.
Results contain at most 2,000 characters
per excerpt and accept a 1–256-character phrase. Indexing reports token savings as not
applicable. Search compares only the source units represented in returned hits with the
complete tool request/response; this conditional estimate can be negative and is not
account usage. It does not count the entire corpus or replay earlier OCR usage.
DOCX additionally allows up to 256 archive entries, 8 MiB per expanded part,
32 MiB total declared expansion, 4096 direct body/cell paragraphs and 64,000 characters
per extracted paragraph. Consumed XML is also bounded by actual bytes, 100,000 reader
nodes and depth 64 per part. DTDs, ambiguous/unsafe package paths and duplicate entries
are refused. No archive content is written to source paths. Index summary `pages` counts
PDF pages; `paragraphs` counts extracted DOCX paragraphs, and `docxDocuments` counts DOCX files.
PDF parsing has cooperative cancellation between
pages, not a hard process deadline or memory sandbox.

Languages are given as **names**, not codes: `--to Russian`, not `--to ru`.

Translation checks reject copied source prose when the requested language names differ,
including copies disguised by whitespace, punctuation or an appended attribution note.
Each fragment is checked, so translating one fragment does not excuse copying another.
Numeric literals must keep their spelling and order, including table cells and repeated
values; decimal localization or reordering is conservatively refused. Fenced code is
preserved separately, and model tags in generated attribution do not count as source
numbers. Invalid output follows only the catalog's fallback order; if no attempt passes,
the tool returns an explicit failure and does not write the translated file. Rejected
work retains its inference accounting and earns no successful translation credit.
These are deterministic checks, **not verification of meaning or target language**.
Unchanged names or words shared between languages can also be refused; same-language
requests skip the copied-prose check. Code-only Markdown remains a no-model preservation
operation. Documentation translations use the same unchanged-text and numeric checks.

For resumable translation, add `--checkpoint translation-progress` (MCP
`translate_local`: `checkpointDirectory`). Repeat the same request after cancellation
or a failed output write. Fragments passing structure and content checks are saved atomically and
reused; protected fenced code is copied unchanged. A fully cached result makes no model
call and does not replay old token counts or timings. Structural validation does not
certify the translation's meaning or language.

Use an empty directory for a new session. Changes to input text, source/target languages,
Markdown mode, routing catalog, processor build or UI language require a new directory.
Checkpoint records contain translated text and receipts, are limited to 4 MiB each,
and allow one writer at a time. Keep the output outside the checkpoint directory;
aliases of the source or checkpoint files are refused. File output is published by
atomic replacement, preserving the old file on cancellation or failure.

```powershell
lai localai translate --in manual.md --from English --to Russian --markdown --checkpoint translation-progress --out manual.ru.md
```

With `--markdown`, a document containing only fenced code blocks and whitespace is
returned unchanged without calling a model or adding a translation note. Local token
usage is zero; cloud token savings are not applicable. In `--json`, this successful
result has `reason: "no_translatable_text"`, `modelCalls: 0`, and `null` for the model,
residency, and both cloud savings estimates. Results that use a model keep their existing fields.

### Look at the state of things

```powershell
lai localai repo status --root C:\work\myapp   # is the repository connected
lai codesearch status --root C:\work\myapp     # where the index is, whether the mainline has moved past it
lai localai model status                       # which models are installed and resident
lai localai policy show                        # residency and update policy
lai localai telemetry                          # a summary of local jobs
lai localai doctor                             # what is wrong with the installation
```

`lai localai diagnostics` displays the complete diagnostic manifest and projected data without
saving a bundle. `lai localai diagnostics --out C:\support\localai.zip` saves those same contents
as a ZIP in an existing folder; it refuses to replace an existing destination. Add `--json` for
the existing machine envelope with `saved`, `manifest` and `contents` fields. Help, cancellation
and the administrative cloud-token report apply; collecting a bundle calls no model or network
service. Collection does not modify its sources; the usual CLI usage report is still recorded.
No new MCP tool or changes to existing MCP responses are introduced by this pilot.

The ZIP contains `manifest.json` and five JSON projections: collector/.NET/OS versions and the
recorded installed release, active queue state counts, recorded repository/index build states,
recognized broker error categories, and aggregate metric status counts. Cloud INPUT and OUTPUT
remain separate; measured and estimated samples have separate minimum/maximum values, including
negative savings. Unknown and inapplicable values retain their own counts. There is no summed
savings claim across calls or workflows. The current diagnostics call finishes after collection
and therefore is not included in its own snapshot.

Prompts, documents, answers, raw logs, exception messages, paths, repository/model names,
job/call IDs, settings and environment variables are excluded. There is no include-content
option. Repository ordinals identify entries only within this snapshot. The collector commit
and active version directory are installation version identifiers, not job/call IDs. These
records **do not prove current service health or index freshness**: files are read independently,
with capture timestamps and explicit missing/unreadable/invalid/linked/limited source counts.
Links are not followed. The first pilot samples at most 1,024 active jobs, 256 repositories,
2,048 retained usage reports, 1 MiB per source file (smaller bounds for states/reports), and
32 MiB of source bytes overall. Truncated samples are not necessarily the newest records.
The two broker log rotations contribute recognized categories only; unknown categories become
`Other`. Manifest file sizes and SHA-256 hashes describe the exact projected ZIP entries, not
the private source files. A cancelled or failed export does not publish a partial ZIP.

### Estimate capacity on the target computer

`localai hardware plan --release <folder> [--disk <existing-path>] [--json]` verifies the release manifest's signature and combines its model sizes with the current `model-routing.json` and residency policy. It estimates model/context combinations using fresh GPU capacity and available physical RAM measurements, with conservative memory reserves. Every combination requires preflight on this machine before use; existing or transferred residency proofs are ignored. The command loads no models, makes no network requests and changes no residency policy.

The release folder must contain compatible `release-manifest.json` and `release-manifest.sig` files signed by the embedded release key. The ZIP is not read or verified by this command. `--disk` selects an existing local directory; the default is LocalApplicationData. UNC/device paths and mapped network drives are rejected. The JSON envelope contains `plan`, `catalogSource`, `policySource`, `dependencies` and explanatory notices. Missing or corrupt policy files retain the strict default, with `fileFound`/`fileUsed` showing the source; an invalid configured catalog is refused. This administrative command reports cloud-token savings as not applicable.

Capacity uses the largest eligible single GPU, without summing adapters. The shared recommendation engine adds 1 GiB of runtime reserve and 256 KiB per context token to the signed base estimate. RAM is assessed conservatively against the same complete-model estimate. `fits`, `exceeds` and `unknown` are estimates; `residencyProof` is always `preflight-required`. Unknown hardware or unsigned model/context costs remain unknown. Disabled/Never catalog entries are excluded. Policy changes require the existing explicit `localai policy set` command.

Dependency checks establish executable presence on local PATH entries only; versions and usability remain unverified. The disk estimate is a lower bound covering recommended model downloads (once per model) and the package size recorded in the signed manifest. Unpacked runtime files, dependency installers, indexes and temporary-space reserves are not fully included. `complete` remains false even when the known minimum fits. Existing model files are not credited against downloads, and optional/manual models are listed without being included in the default disk total.

### Prepare and verify transferred offline materials

```powershell
lai localai offline bundle --source C:\prepared --out E:\localai-offline --json
lai localai offline verify --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --json
```

The source must contain `release-manifest.json`, `release-manifest.sig`, `localai-package.zip`,
and `offline-components.json`. The release signature is checked against the embedded release key;
the copied package's length and SHA-256 must match that signed manifest. The ZIP is not extracted
or inspected here; the normal installer remains responsible for package-content verification.
Source and destination must be local, the output parent must exist, and the output folder must be
new. No downloads or bundled executables are started. Run these commands with the published
self-contained CLI; this transfer step does not need a global .NET SDK.

An example source inventory (replace the declared version and supply the actual file):

```json
{
  "schemaVersion": 1,
  "components": [
    {
      "kind": "dependency",
      "id": "dependency.ollama",
      "version": "user-verified-version",
      "files": ["dependencies/ollama/OllamaSetup.exe"]
    }
  ]
}
```

`kind` is `dependency`, `model` or `runtime`. Dependency IDs come from the existing catalog:
`dependency.git`, `dependency.ollama`, `dependency.github-cli`, `dependency.dotnet-sdk-10`,
`dependency.nodejs-20`, `dependency.scip-typescript`, `dependency.python-3-12`, and
`dependency.scip-python`. A model ID is an exact enabled tag from the current `model-routing.json`.
A runtime component uses ID `localai` and the signed release version; it can list additional
runtime files, such as `LocalAi.Installer.exe`. The three signed release files are always included.
Every supplied component needs a version label and an explicit file list: no wildcards or recursive
folder expansion. Versions and component membership are declarations, not compatibility evidence.
Model artifacts must already be obtained through the normal authorized model workflow; this command
does not invoke Ollama, import a model store, or copy broker state/residency proofs automatically.

The bundle preserves relative paths and adds `offline-manifest.json`. Only declared files are copied;
all catalog dependencies and enabled models are listed as `included` or `missing`. Files shared by
components are copied/count once. `transferredBytes` counts listed materials; `totalBundleBytes`
also includes the inventory itself. These are transfer/storage lower bounds, not installed-space
requirements: expansion, SDK workloads, model import, indexes and temporary space are unaccounted.
Inventory limits are 8 MiB, 8,192 material files and 1 TiB total material bytes. Preparation publishes
by moving a completed staging folder; failures/cancellation do not publish a partial output.

Keep the reported `manifestSha256` separately in a trusted location. The inventory is not a new
publisher signature: copying a hash alongside an altered inventory would not establish trust.
`offline verify` first requires that independent hash, then checks every file and the signed runtime
metadata/package hash. Missing, extra, modified or linked files, unsafe paths and case collisions
are refused. Verification reflects the bytes observed during that invocation; it does not authorize
later execution of a changed file. Both commands use the standard JSON envelope and administrative
cloud INPUT/OUTPUT reporting (not applicable).

Missing components stay explicit even when integrity verification succeeds. `completeForInstallation`
remains false and `installationState` remains `installation-unverified`. Clean-machine/no-network
acceptance is still pending in #612.
These commands establish a reproducible transfer inventory and byte integrity, not a fully installable
or usable offline environment.

### Plan the offline environment and its known disk budget

```powershell
lai localai offline plan --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --disk C:\prepared --json
```

This read-only command re-verifies the bundle and lists the sequence: catalog dependencies, the
normal runtime installer, model import, separate Ollama root configuration and broker preflight.
All dependencies and enabled models in the current catalog are accounted for, including missing
inventory rows; this is catalog coverage, not a recommendation to install every optional component.
Installed dependency versions are not detected. Included dependency recipes and the model graph
use the same validators as the individual application commands. Unsupported recipes and invalid
model graphs remain explicit; planning never launches an installer or model, copies model files,
changes configuration, or applies the sequence. `--mode apply` is not supported here.

`--disk` must name an existing local directory without links, junctions or mounted-directory
aliases. The disk report is a single-volume planning scenario, not a reservation or a cross-volume
installation check. The known budget separates runtime artifact bytes, ZIP expansion, unique model
files, the largest sequential dependency staging payload, retained npm materials and the two
copies of a bundled uninstaller. Archive expansion comes from the
trusted ZIP's bounded central/local-header and layout checks shared with the normal installer;
it does not replace decompression, package-metadata verification or Authenticode validation.

The conservative known additional-space budget is:

```text
runtime artifacts + unique model files
  + retained npm materials
  + 2 * bundled LocalAi.Installer.exe (when the runtime component lists it)
  + max(2 * compressed runtime package + runtime ZIP expansion,
        runtime ZIP expansion + largest validated dependency staging payload)
```

The two compressed copies reserve the normal directory-feed/verifier preparation flow; the runtime
stage is additional to installed artifacts and stays retained while selected dependencies execute.
Model import publishes its copied stage by renaming it,
so shared blobs and the model stage are not counted twice. Existing bundle storage is reported by
`offline bundle` separately. Missing/invalid material sizes, dependency installed/temporary sizes,
old-version/launcher backups, indexes, caches and filesystem overhead remain explicit unknowns.
Below-budget free space yields `below-known-budget`; unavailable space yields `unknown`; enough
space still yields `incomplete`. `readyForInstallation` and disk `complete` remain false. A successful
JSON envelope means the plan was generated, not that the environment is ready. Selected execution
is available through `offline execute` below; clean-machine/no-network acceptance remains outstanding in #612. Cloud INPUT/OUTPUT
accounting is administrative (not applicable).

### Apply a bundled dependency or import model artifacts

```powershell
lai localai offline install-dependency --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --component dependency.ollama --json
lai localai offline import-models --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --out C:\localai-models --json
```

Both commands default to `--mode plan`; add `--mode apply` to execute the displayed operation.
Each invocation verifies the complete bundle again against the separately trusted inventory hash.
Use the published self-contained CLI. Both commands report administrative cloud INPUT/OUTPUT as
not applicable and do not invoke a model.

Dependency application requires `offline-actions.json` in the source and its explicit inclusion in
the `runtime` component's `files` before building the bundle. For example:

```json
{
  "schemaVersion": 1,
  "actions": [{
    "component": "dependency.ollama",
    "kind": "exe",
    "installer": "dependencies/ollama/OllamaSetup.exe",
    "arguments": []
  }]
}
```

The empty argument list above is an interactive-launch example, not a verified unattended recipe.
Supply the vendor's documented arguments for the exact full offline installer you prepared. The
installer and every companion file must belong to that dependency's inventory entry and share one
directory with unique filenames. Nested payload directories are unsupported. Application runs one
EXE or MSI on Windows, after copying and hashing the selected files in a protected temporary folder
and retaining file handles against replacement throughout execution. MSI uses the system `msiexec`
with `/i`, `/qn`, `/norestart`; recipe arguments may only be uppercase `PROPERTY=value` entries.
There is no shell-script, package-manager or network fallback in the EXE/MSI command. Pinned SCIP
npm caches use the separate `offline install-npm` command below.
The inventory hash trusts the supplied recipe and bytes, not their publisher or declared version.
The external installer may access the network or make partial changes; network isolation, installed
version and usability are not established here, and Local AI cannot automatically roll changes back.

Installer exits 0, 3010 and 1641 produce `state=completed`; the latter two set `rebootRequired=true`.
Other failures return a nonzero CLI exit with `ok=false` while preserving result details. Cancellation
and timeout produce `cancelled` and `timed-out`. If termination cannot be confirmed, the state is
`termination-unconfirmed`; `retainedDirectory` identifies preserved installer files. Resolve the
process state before retrying or removing those files. The command does not elevate privileges.

Model components must list the complete Ollama schema-2 manifest/blob graph, rooted at
`models/manifests/<host>/<namespace>/<model>/<tag>` and `models/blobs/sha256-<digest>`. Config and layer
digests, sizes and inventory hashes must agree; extra files such as residency proofs are rejected.
Only enabled catalog tags are accepted. Short names default to `registry.ollama.ai/library` and
`latest`. This Windows path subset permits 1–80 ASCII letters, digits, underscores, dots and hyphens
per segment, starting with a letter, digit or underscore; host ports and digest-qualified tags are
unsupported. Action documents and individual model manifests are limited to 1 MiB each.

Import requires a new local output folder outside the bundle, with an existing parent. Verified
files are copied to staging and published by a directory move; existing stores are never merged
or replaced. `OLLAMA_MODELS`, the active runtime and policy are unchanged. Configure the Ollama
model root separately, then run the existing broker preflight on the target machine; backend/model
startup and residency proof are separate steps. The Windows wizard's dependency flow is unchanged.
Clean-machine installation without network access or a preinstalled SDK remains an acceptance task.

### Execute selected offline steps with a durable journal

```powershell
lai localai offline execute --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --request C:\prepared\request.json --out C:\prepared\run-01 --json
```

The default `--mode plan` validates the selection without creating the run folder or applying
changes. Add `--mode apply` to execute it on Windows. Example `request.json` (at most 1 MiB):

```json
{
  "schemaVersion": 1,
  "dependencies": ["dependency.ollama"],
  "installRuntime": true,
  "modelsOutput": "C:\\localai-models"
}
```

Choose only required EXE/MSI catalog dependencies. Schema 1 requests keep the original behavior;
use schema 2 and an explicit `npm` array to include npm components in the same journaled run.
For example, a request selecting only SCIP TypeScript is:

```json
{
  "schemaVersion": 2,
  "dependencies": [],
  "installRuntime": false,
  "modelsOutput": null,
  "npm": [{
    "component": "dependency.scip-typescript",
    "node": "C:\\Program Files\\nodejs\\node.exe",
    "npmCli": "C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js",
    "output": "C:\\localai-scip-typescript"
  }]
}
```

Schema 2 makes older executors refuse the request instead of silently ignoring npm selections.
Node/npm must already exist when planning; this does not bootstrap a missing toolchain. Each npm
selection follows the pinned cache rules below and requires its own new prefix. Duplicate component
selections and overlapping run/model/npm destinations are refused before effects. The plan reports
`dependencyOrder`: selected EXE/MSI and npm components use catalog order regardless of array order,
then runtime and model steps follow. npm intent and outcomes use the same durable journal; failure
or uncertain termination prevents later steps, and retained files appear in `steps[].retainedDirectory`.
PATH setup is optional below; toolchain/functional compatibility remains a separate requirement.

To register the selected tools, use `"schemaVersion": 3` and add `"registerPaths": true` to the
same request. At least one runtime or npm selection is required; older schemas reject this option.
The default is false, preserving existing requests. `userPath` in the plan shows the original
and proposed user PATH and the selected directories: the runtime launcher directory, each npm
selection's Node directory and its `node_modules/.bin`. The Node executable must be named
`node.exe`. Literal directory names containing PATH delimiters, quotes, percent signs or line
breaks are refused. Existing entries keep their order; duplicate directories are not added.

The `configuration:user-path` step runs after all selected installations/imports succeed, with
durable intent before its effect. It checks that the directories and launcher/npm command shims
exist without links, checks that user PATH still equals the execution plan's snapshot, appends
the missing entries and reads the stored value back. A detected concurrent change stops the step;
Windows registry updates are not an atomic compare-and-set. Failed writes/readback are reported
in `steps[].detail`, leave the step pending and never trigger a retry or rollback. PATH may already
have changed after a failed write/readback. Earlier installed files remain. Machine PATH and
existing process environments are unchanged. Start a new session before using the commands;
earlier PATH entries can still resolve a different Node/SCIP version. Compatibility and broker
preflight remain required, and `readyForUse=false` remains explicit.

To bind imported models to broker-managed Ollama launches, use `"schemaVersion": 4` and
`"configureModels": true`, with a non-null `modelsOutput`. Runtime installation must be selected
or its destination root must already exist. The default is false; schemas 1–3 refuse this option.
The plan's `modelSetup` names the imported directory, `ollama-launch.json` and the prior file hash.
After import, `configuration:ollama-model-root` discovers Ollama through the installer's verified
installed-application probe (registry entry, approved directory and executable identity), never
through PATH. Its journal intent precedes the probe and the write. Missing/unverified Ollama,
a detected concurrent record change, a write error or a failed readback stops later steps with
the configuration still pending. Imported files remain. The record is published by a flushed
temporary-file rename; the before/after checks are not an atomic compare-and-set with other writers.

Schema 2 of `ollama-launch.json` stores the explicit model directory. On a future broker-managed
launch, the broker validates that this local directory exists without links and sets `OLLAMA_MODELS`
only in its child process. An unavailable directory prevents launch instead of falling back to the
default store. A later installer update preserves the configured directory. User/system
`OLLAMA_MODELS` and an already listening backend are unchanged. Use a runtime that supports this
record schema; older runtimes refuse it. Stop an existing Ollama backend deliberately before using
the configured launch, then run the existing model preflight through the target broker. This option
does not stop/start a backend, run inference or transfer residency proofs. A configured directory
does not establish model compatibility or readiness.

To add the Windows uninstall entry for the current user, use `"schemaVersion": 4`,
`"registerUninstall": true` and `"installRuntime": true`. The bundle's runtime component must
explicitly include the release's self-contained `LocalAi.Installer.exe` at the bundle root.
Its bytes and SHA-256 are covered by the independently trusted bundle pin. Before any dependency
runs, the file is copied into protected staging, rehashed and retained; applicable release
Authenticode requirements still apply. The plan's `uninstall` section shows its hash, destination,
registry subkey and previous entry. An existing entry for another installation root is refused.

After runtime activation, `configuration:uninstall` records intent, copies the retained installer
to `installer/uninstall/LocalAi.Installer.exe` and registers Local AI Toolkit in the current user's Windows
installed-app list. It opens no installer UI. Registry snapshot checks and registry/file readback
detect changes and failures; they are not an atomic transaction. Failure stops later steps, and
partial runtime, file or registry changes may remain without automatic rollback. Functional
uninstall acceptance is still required. The disk plan reserves both installer copies and retained
npm project/lock/cache files; installed npm trees, generated files and vendor temporary space remain
unknown, so it never claims complete disk readiness.

To check selected imported models on the target installation, use `"schemaVersion": 5` and
`"preflight": [{ "model": "<bundled-model-tag>", "contextTokens": 8192 }]`, replacing the placeholder
and context with a pair present in both the current catalog and signed release manifest. This option
requires `installRuntime=true`, `configureModels=true` and import of every selected model. Older request
schemas refuse it. Planning lists the selections without starting a process.

After import and model-root setup, each `preflight:<model>:<context>` step records intent, holds the
verified launcher and active-version lease, checks model status and requests broker preflight. A missing
model or a pending pull stops the sequence; this path never downloads a model. The target root is passed
explicitly to the shared broker, and the target residency policy is read without changing it. `model status`
and `model preflight` accept a final `--runtime-root <dir>` for this purpose; it must be an existing local
directory without links, given as an absolute path. The option is refused by `model pull`.

Successful steps record `proof` (model, context, catalog, model/VRAM bytes and verification time) and
`residencyPolicy`; rejected or invalid responses never become accepted proofs. Partial/CPU placement
is accepted only under the target policy. Failure stops later selections and PATH setup. The original
plan retains its pre-execution requirements; step proofs report what completed. Other model/context
pairs remain unchecked. An already running Ollama is not restarted: successful residency checks do not
prove which model source or directory it used. Source/directory verification, complete dependency/disk
evidence and clean-machine offline acceptance remain required; `readyForUse=false` is unchanged.

The command does not add optional components. An empty
dependency array, `installRuntime=false` or `modelsOutput=null` omits the corresponding steps, but
at least one step (including an npm selection) must remain. Dependencies run in catalog order, followed by runtime activation
and import of all included, enabled models. The dependency recipe and model rules above still apply.
Every supplied path must be local. Model and run outputs require new, separate folders with existing
parents, outside the bundle and runtime installation. Existing run folders are never resumed.

Before any dependency executes, a selected runtime passes the normal full signed-package, metadata
and applicable Authenticode verification in protected staging. Its verified files remain retained
through execution. Runtime activation uses the current user's default Local AI installation and may
stop running tools and change the active version. PATH is changed only by the explicit schema-3
option above. Windows uninstall registration is optional as described above; other wizard settings remain separate.
Model-root setup is optional as described above; it does not change user/system `OLLAMA_MODELS`.

The run folder contains `plan.json`, `journal-*.json` and, when recording succeeds, `result.json`.
Each step's intent is flushed before its effect. Failure, cancellation, timeout, reboot request,
unconfirmed process termination or a journal write failure prevents subsequent steps. Reboot and
uncertain outcomes leave an incomplete journal; uncertain effects remain Running. The result
preserves completed steps, pending identifiers, runtime outcome and retained dependency files in
`steps[].retainedDirectory`. Confirm process/installer state before retrying or deleting retained
files. There is no automatic replay, resume or rollback of the whole run; partial changes can remain.
`recordingComplete=false` means available records may be incomplete.

CLI exits are 0 for a plan or completed selected steps, 130 for cancellation, 75 for timeout or reboot,
74 for recording failure and 70 for failed/uncertain execution. Such nonzero results use `ok=false`
and retain the result details. Invalid requests use 2 or 65; an existing output uses 73.
Even `state=applied` reports `readyForUse=false`: dependency versions/prerequisites, complete disk
requirements and offline usability remain unverified. Select model-root setup and target preflight
explicitly or complete them separately. Only the selected preflight step invokes models; no residency
proof is transferred from the preparation machine. The command adds no network fallback or privilege elevation. External vendor installers can
still use the network. Cloud INPUT/OUTPUT accounting is administrative (not applicable).

### Install pinned SCIP packages from an offline npm cache

```powershell
lai localai offline install-npm --bundle E:\localai-offline --manifest-sha256 <trusted-hash> --component dependency.scip-typescript --node "C:\Program Files\nodejs\node.exe" --npm-cli "C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js" --out C:\localai-scip-typescript --json
```

The default is `--mode plan`; `--mode apply` installs on Windows into a new local prefix with an
existing parent, outside the bundle. Existing prefixes, global packages and PATH are preserved.
Supply existing local Node/npm paths explicitly; their provenance and versions are not verified
by this command. Node must be compatible with the selected package. This command invokes no models
and reports administrative cloud INPUT/OUTPUT as not applicable.

Only the current catalog's pinned `dependency.scip-typescript` and `dependency.scip-python` packages
are supported. For each component, declare these files under its dependency inventory entry:

```text
npm/<component-id>/package.json
npm/<component-id>/package-lock.json
npm/<component-id>/cache/_cacache/<every prepared cache file>
```

List each cache file explicitly; the inventory does not expand globs. Do not include `node_modules`,
logs or `.npmrc`. The component version must equal the catalog version. `package.json` allows only
name, version, private and dependencies; dependencies must contain exactly the selected package
at that exact version, without a range. For TypeScript the dependencies object is
`{"@sourcegraph/scip-typescript":"0.4.0"}`; for Python it is
`{"@sourcegraph/scip-python":"0.6.6"}`. Use a version-3 lockfile with matching root dependencies
and package version. Registry package entries require HTTPS `registry.npmjs.org` URLs and SHA-512
integrity values. Git/file links, workspaces and arbitrary package sets are unsupported.

Prepare the project and cache on a connected machine in a new folder, running Node's `npm-cli.js`
with `install --ignore-scripts --global=false --no-audit --no-fund --logs-max=0 --cache ./cache
--registry=https://registry.npmjs.org` from that folder. Use the working directory, not `--prefix`,
for this preparation command. Include only the declared project, lock and cache files in the transferred bundle.

Application re-verifies the complete pinned bundle, copies selected materials into protected
staging, and runs `npm ci --offline --ignore-scripts --global=false --no-audit --no-fund` through
Node with a private cache, separate empty user/global configs and an explicit working directory.
Missing cache entries fail without an online fallback. On success, installed package name/version
are checked; Python receives the existing hash-pinned Windows compatibility patch. The completed
stage is published by moving it to the new prefix. Package commands are under `node_modules/.bin`;
PATH configuration and functional checks remain separate. The prefix also retains its private cache.

Failed/cancelled/timed-out npm runs retain staging for diagnosis. `result.retainedDirectory` names
it. `termination-unconfirmed` means npm may still be running; establish its state before retrying
or deleting files. No failure publishes the destination. `readyForUse=false` remains explicit even
after success. Offline flags are not a firewall test, cache availability is not functional acceptance,
and installed/temporary disk requirements remain incomplete. `offline plan` now recognizes these
materials and their staging bytes, while reporting the separate toolchain/prefix requirement.
The same operation can be selected in a schema-2 `offline execute` request and recorded in its journal.

### When something refuses


A refusal is a message, not a breakage. Three common ones:

| What you see | What it means | What to do |
| --- | --- | --- |
| `No overlay matches this worktree` | edits are saved, the index is behind | `lai localai sync --root <repo>` |
| `NOT_CONFIGURED` | the repository is not connected | `lai localai sync --root <repo>`, then `hooks install` |
| `The model backend is unavailable` | the model backend refused a batch; the build waits for it and goes on by itself | nothing, usually — if the build did stop, run `lai localai sync --root <repo>` again; what was computed is kept in a checkpoint |

---

← [Using it with a cloud agent](using-it-with-a-cloud-agent.md) · [Contents](README.md#contents) · [What language it answers in](what-language-it-answers-in.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
