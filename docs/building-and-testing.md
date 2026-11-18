# Building and testing

[Contents](README.md#contents) · [Русская версия](building-and-testing.ru.md)

**On this page**

- [Independent quality corpora](#independent-quality-corpora)
  - [Paired holdout comparison](#paired-holdout-comparison)

```powershell
dotnet test LocalAi.slnx --configuration Release --max-parallel-test-modules 1 --timeout 20m
```

That is the answer before a pull request. While iterating, a single test project is
faster — `dotnet test tests/<name>/<name>.csproj --configuration Release`.

Windows CI includes Python SCIP in strict fixture checks. It installs the catalog-pinned
`@sourcegraph/scip-python@0.6.6` in a private `scip-python-fixture` npm prefix, then runs
`dotnet run --file scripts/quality/prepare-scip-python-fixture.cs --configuration Release -- <prefix>`.
The helper calls the installer's existing version and complete-bundle SHA-256 checks;
it does not reproduce the patch logic or accept an unknown bundle. Its two fixes cover
Windows path separators and interpreter selection. Other package versions need separate verification.

With Python 3.12 on the runner, the real fixtures check cross-file definitions/references
and definition body ranges. A successful `--version` probe alone is insufficient. Missing
prerequisites or failed indexing fail CI; Python no longer has a named strict-mode exemption.

A test never asserts how fast the machine is. Wait for the condition, not for a duration —
a deadline a correct run can miss proves nothing, and reports itself as a fault in the code
under test.

---

## Independent quality corpora

The developer harness in `scripts/quality/run.ps1` creates separate C#, TypeScript, Python
and mixed Git repositories, with queries and reference answers outside the indexed trees.
Its 70 search cases are split 12/12/12/34; the mixed corpus repeats language-specific
questions, so these are not 70 independent questions.

Run from the repository root on Windows with PowerShell 7.4+, Git with a configured author
identity, the .NET 10 SDK, and a working Local AI broker. The configured embedding model and
requested OCR model must be installed and satisfy the residency policy. Each invocation
needs an output directory outside the repository that is empty or does not exist yet. Avoid concurrent broker work when
comparing timings.

```powershell
dotnet build LocalAi.slnx --configuration Release
pwsh -NoProfile -File .\scripts\quality\test-runner.ps1

$qualityOutput = Join-Path (Split-Path -Parent (Get-Location).Path) ("LocalAi-quality-" + [Guid]::NewGuid().ToString("N"))

pwsh -NoProfile -File .\scripts\quality\run.ps1 `
  -OutputRoot $qualityOutput `
  -Mode All `
  -LocalAi .\src\LocalAi.Cli\bin\Release\net10.0\localai.exe `
  -CodeSearch .\src\CodeSearch.Cli\bin\Release\net10.0\codesearch.exe `
  -OcrModel glm-ocr:bf16
```

`Search` and `Ocr` select individual suites; `Prepare` creates the search repositories
without invoking models and needs neither executable. Every mode requires an output
directory outside the repository that is empty or does not exist yet. Results, raw responses and provenance are retained under that directory.

### Paired holdout comparison

`-SearchSuite heldout-v2` is an additional search suite: three new source repositories,
60 EN/RU queries and 30 paired intents. Each language has five answerable and five
no-answer intents. The references are AI-authored and were checked by a blind independent
agent before measurement; human validation and novelty relative to model training are
not established. After its first measured use, the suite is a known regression corpus.

Prepare once with `-Mode Prepare -SearchSuite heldout-v2`. Index those three source
repositories through the broker, then freeze `heldout-v2/protocol.json` with the actual
source trees, corpus hashes, generation IDs, complete search options and quality profile.
Keep annotations and results outside the source trees. A model tag does not prove a
weight digest or server routing; unavailable evidence stays unknown.

Build the baseline and candidate from clean, recorded commits. Before either search run,
retain an artifact-plan JSON with `schemaVersion: 1`, the frozen protocol's `protocolSha256`,
and `baseline.codeSearch` / `candidate.codeSearch` fingerprint arrays: each executable and
adjacent DLL's `file`, `version`, `recordedProductVersion` and `sha256`. This external plan
avoids a binary/protocol self-hash cycle. Pass the same `-PreparedRoot` and separate empty
`-OutputRoot` directories to both `-Mode Search -SearchSuite heldout-v2` runs. Prepared
reuse does not reindex. The runner checks every corpus before evaluation, captures
streamed output, and checks source/corpus and binary identities again afterwards.

`dotnet run --file scripts/quality/evaluate-holdout.cs --configuration Release -- validate <protocol> <prepared-root>`
validates references without retrieval. The `compare <protocol> <prepared-root> <artifact-plan> <baseline-root> <candidate-root> <report.json>`
mode requires the frozen identities and equal canonical raw hit sequences. This change
checks harness parity; it changes neither ranking nor the `0.43` threshold and makes no
quality-improvement claim. `test-heldout.ps1` checks the data and rejection paths offline;
Windows CI runs it and the process-streaming regression.

The [first paired result](https://github.com/MrAliev/local-ai-toolkit/tree/main/tests/CodeSearch.Tests/Fixtures/SearchEvaluation/heldout-v2/baseline-2026-11-18/README.md)
retains the original strict scores and describes the TypeScript symbol-matching limitation.
The checked-in protocol is a marked canonical copy made after that measurement. For a new
run, capture fresh identities in a new copy and set `status` to `frozen-unmeasured` before
measuring; do not present this already-used suite as a new blind holdout. Only the profile's
calibration date is compared by its explicit-offset instant; all other profile fields remain exact.

Report query metrics by language and locale, plus paired intents where both, one or
neither paraphrase succeeds. A no-answer hit means that no direct implementation was
annotated; it is an operational false positive, not a hallucination measure. Synthetic
templates are correlated: grouped Wilson ranges are descriptive independence references,
not population guarantees. Timing remains descriptive under shared broker load; missing
queue-wait evidence stays unknown. Historical v1 figures below use different inputs.

The four OCR references cover short and long EN/RU text and include repeated lines.
Scoring preserves repetitions and reports character/word error rates (CER/WER), nonempty
line counts, annotated field-label recall and exact field-value accuracy. CLI completion
and wall time are separate from recognition accuracy; queue/run timings and truncation
evidence remain in retained responses when available. Missing diagnostics remain unknown.
Field metrics do not measure general semantic understanding.

The [retained 2026-09-16 baseline](https://github.com/MrAliev/local-ai-toolkit/tree/main/tests/CodeSearch.Tests/Fixtures/SearchEvaluation/independent/baseline-2026-09-16)
used `qwen3-embedding:8b-q8_0`, with successful Roslyn/SCIP indexing and no outside-source hits:

| Corpus | Answerable / no-answer cases | Recall@10 | Precision@5 | No-answer false positives |
| --- | ---: | ---: | ---: | ---: |
| C#, TypeScript, Python (each) | 10 / 2 | 100% | 20% | 1/2 |
| Mixed | 30 / 4 | 96.67% (29/30) | 19.33% | 2/4 |

Precision@5 has a 20% ceiling with one annotated target. These small synthetic corpora
do not establish representative product quality. The search threshold remains `0.43`.

All four OCR cases passed the CLI completion check and reported `truncated=false`.
The requested model was `glm-ocr:bf16`; every retained response records the actual
model as `qwen3-vl:8b-instruct-q8_0`. The reason for that difference has not been established.

| Case | CER | WER | Field-value accuracy | Nonempty lines preserved |
| --- | ---: | ---: | ---: | ---: |
| Short EN | 0% | 0% | 100% | 2/2 |
| Short RU | 0% | 0% | 100% | 2/2 |
| Long EN | 0% | 0% | 100% | 40/40 |
| Long RU | 2.2176% | 10.4918% | 57.14% (4/7) | 40/40 |

A code-point comparison of the long RU result found 32 substitutions in zone designations:
Latin `A/B/C` became Cyrillic `А/В/С` in 10/11/11 positions respectively.

The harness source is commit `40cf87a8ee0f1e631f74f1b9c93783bab71a5b0a`. Runs used installed
broker build `17385b2a6070`, with CLI binary hashes recorded separately. Wall times describe
complete command execution. A model-inventory check overlapped the run; no additional
generative task was started. The original OCR invocation metadata arrays are preserved;
`notes.json` documents their shape before the runner fix. Scores were not rewritten.

---

← [Product contracts and release acceptance](product-contracts-and-release-acceptance.md) · [Contents](README.md#contents) · [Licence](licence.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
