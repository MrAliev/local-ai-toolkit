# Documentation text and render caches

[Русская версия](README.ru.md)

Part two of #622. The [source collection probe](../documentation-capture/README.md) measures
Capture on 1, 5 and all 1,269 public types of a frozen repository/index. This separate probe
uses a bounded, synthetic corpus of 12 documentation inputs to measure package generation,
validation and rendering at 1, 5 and all 12 types. These different corpora are not an end-to-end
latency comparison and their phase timings must not be added together.

## Cache contract

The generated bilingual draft is stored atomically in `documentation/texts/<text-key>.json`.
Its schema, key and SHA-256 are checked before use, and its claims, examples and graph are
validated again before rendering. Missing, corrupt or invalid text is regenerated. A valid
unchanged rendered artifact needs no text-cache read. Validated text survives cancellation
or a later rendering failure; only a complete artifact replaces `latest`, and only a complete
build publishes the current package. Failed units retain their previous verified artifact.
Apply's manual-edit and snapshot safeguards are unchanged.

The text key includes source, reference and history hashes, the symbol/name/facts/graph,
the legacy optional generator-version override, and the following explicit versions:

| Version | Defined in | Change rule | Effect |
| --- | --- | --- | --- |
| Prompt | `DocumentationGenerator.PromptVersion` | Change overview/system/translation prompts, evidence selection, output schema, repair instructions, or graph template supplied to the model. | New text and rendered files. |
| Validator | `DocumentationValidator.CacheVersion` | Change accepted claims, examples, SVG, translation validation or evidence rules. | New text and rendered files; old output cannot bypass the new checks. |
| Route | `DocumentationCacheVersions` | Bump the route version for routing-policy behavior changes. The pinned overview sequence and canonical validated routing catalogue are also hashed automatically. Catalogue content changes invalidate even without a `catalogVersion` bump; formatting alone does not. | New text and rendered files. |
| Renderer | `DocumentationRenderer.CacheVersion` | Change output layout, escaping, fonts, translations of renderer labels, diagram rendering, PDF/HTML/Markdown encoding or output dependencies. | Rerender checked text without inference. |

The full `DocumentationInput` is also hashed for presentation. Main source coordinates and
snapshot labels therefore update even when the text inputs stay the same. Coordinates inside
cited facts remain text inputs and still invalidate text when changed. Source/reference/history
invalidation is conservative: an unchanged prompt alone does not override those hashes.
The production generator consumes only symbol/name/facts/graph; custom generator callbacks
must honor that boundary or provide a generator-version override covering additional inputs.
For custom model routes, provide the corresponding `DocumentationCacheVersions` explicitly.
Replaced model weights under an unchanged tag need a route-version change; no live model-digest
lookup is performed by the cache. A detected catalogue change during generation refuses that
unit instead of saving its text under the old route key.

Old artifact metadata remains readable for apply/failure recovery. It lacks the new cache
identity, so the next build regenerates once. Public report fields stay the same: `reused`
counts exact artifact reuse, not text reuse followed by rendering. New reason codes are
`presentation` and `renderer`, with EN/RU review and CLI messages.

## Measurement boundaries

`measure` replays a deterministic validated draft. It invokes **no model**. Generation replay
time measures the callback and draft construction, not inference. The actual validator and
renderer are timed separately by instrumentation in temporary source copies. Build time also
includes cache checks, hashing, checkpoint/package writes and flushes. There is one warmup,
then three independent fixtures for each limit. Run baseline then candidate, without other
build/index/model work. Tests impose no speed thresholds.

Each fixture runs cold, unchanged, presentation-only, renderer-version and dependency-fact
changes. The old unified generator-version override represents a renderer change in the
baseline; the candidate uses its dedicated renderer version. Generator callback counts must
drop from N to zero for that case. The baseline must reproduce N stale presentation labels;
the candidate must keep all labels current. Non-PDF files must match byte for byte between
versions for the other cases. PDFsharp emits random six-letter font-subset names and XMP
DocumentID/InstanceID UUIDs even on repeated baseline renders. PDF comparison normalizes
only those dictionary names and two XMP fields, preserving all other bytes; raw and normalized
digests are retained. This hash comparison happens outside the timed build. Capturing source
files is outside this probe.

`live` separately runs 1, 5 and all 12 types through the real `DocumentationGenerator` and `BrokerDocumentationModel`
through the installed shared broker, using its existing policy and routing catalogue. It
records request counts, queue/execution durations and residency receipt fields. A cold build
must succeed; presentation and renderer changes must produce current files with zero further
model requests. It does not install models or change policy. Its cache is private to the
probe. `GenerationMs` includes model queues/execution and generator orchestration but subtracts
the nested `DocumentationValidator` time; `ValidationMs` includes those checks plus the store's
final validation. `RenderMs` is measured independently. The private cache uses an explicit
version snapshot of the installed routing catalogue and checks it again after each phase.
Neither timing, cache hits nor local request counts are reported as cloud-token savings.

## Reproduce

Recorded on September 21, 2026. For all 12 synthetic types, three-sample median build time
and generator callback counts were:

| Case | Build before → after (ms) | Generator callbacks before → after | Stale presentations before → after |
| --- | ---: | ---: | ---: |
| Cold | 1,581.37 → 1,586.21 | 12 → 12 | 0 → 0 |
| Unchanged | 61.43 → 56.92 | 0 → 0 | 0 → 0 |
| Presentation | 62.40 → 1,615.82 | 0 → 0 | 12 → 0 |
| Renderer version | 1,682.92 → 1,623.78 | 12 → 0 | 0 → 0 |
| Dependency fact | 1,653.05 → 1,625.18 | 12 → 12 | 0 → 0 |

Presentation updates do more work because they now produce current files instead of returning
stale ones. Replay timings cannot quantify inference time avoided. For the renderer-version
case, candidate validation/render medians were 0.521/1,111.262 ms; generation replay was zero.
The full 1/5/12 matrix retains each phase separately in `evidence/summary.json`.

The separate [live broker run](evidence/live.json), using installed broker `f10ff0ec19a5`,
made three requests on the cold build (22,818.10 ms): `gpt-oss:20b` plus two `qwen3.5:9b`
translations. Presentation and renderer changes then took 256.08 and 256.52 ms with zero
requests and current source/snapshot labels. GPT's receipt had `offload_unknown`; Qwen's
receipts reported 100% VRAM and no shortfall. These are one-run observations, not a paired
inference-speed benchmark or evidence of semantic prose quality.

The subsequent [complete live matrix](evidence/live-matrix.json) covers 1/5/all 12 types:

| Types | First build (ms) | Generation excluding validator (ms) | Validation (ms) | Rendering (ms) | Model requests: first / presentation / renderer |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | 25,654.59 | 24,871.69 | 6.714 | 662.297 | 3 / 0 / 0 |
| 5 | 137,028.68 | 136,002.31 | 1.344 | 743.923 | 15 / 0 / 0 |
| 12 | 343,907.84 | 341,749.23 | 5.884 | 1,524.458 | 38 / 0 / 0 |

All nine cases passed with current presentation metadata and unchanged routing configuration.
"Cold" means an empty documentation cache, not a cold model backend; model warmness was not
controlled. There were 19 GPT requests (including repair) and 37 Qwen translation requests.
All GPT receipts had `offload_unknown`; Qwen receipts reported no shortfall. These timings
measure the candidate pipeline and are not a paired baseline/candidate inference comparison.

Use a new task-owned directory in PowerShell. The baseline source archive and the current
checkout supply the two implementations; source hashes are recorded before instrumentation.

```powershell
$revision = 'cf42e6c26ef8820f19e6d9e8755b70bc9916b51e'
$run = Join-Path $env:TEMP ('documentation-cache-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
git archive --format=zip --output "$run/baseline.zip" $revision
Expand-Archive "$run/baseline.zip" "$run/baseline-source"
& ./scripts/quality/documentation-cache/Prepare-Probe.ps1 -Repository $PWD -Revision $revision -SourceRepository "$run/baseline-source" -Destination "$run/before"
& ./scripts/quality/documentation-cache/Prepare-Probe.ps1 -Repository $PWD -Revision $revision -Destination "$run/after"
$before = "$run/before/probe/bin/Release/net10.0/DocumentationCacheProbe.exe"
$after = "$run/after/probe/bin/Release/net10.0/DocumentationCacheProbe.exe"
& $before measure "$run/before/runs" "$run/before/results.json" "$run/before/source-hashes.json"
& $after measure "$run/after/runs" "$run/after/results.json" "$run/after/source-hashes.json"
& ./scripts/quality/documentation-cache/Save-Evidence.ps1 -InputDirectory $run -OutputDirectory "$run/evidence"
# Set $brokerRuntime to the existing LocalAi runtime; the default Windows location is below.
$brokerRuntime = Join-Path $env:LOCALAPPDATA 'LocalAi'
$installedVersion = (Get-Content (Join-Path $brokerRuntime 'bin/current.json') -Raw | ConvertFrom-Json).version
if ($installedVersion -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid installed version name' }
$brokerExecutable = Join-Path $brokerRuntime "bin/versions/$installedVersion/LocalAi.Broker.exe"
& $after live "$run/live" $brokerRuntime "$run/live.json" $brokerExecutable
```

Raw samples, source identities and checked summaries are retained under [evidence](evidence/summary.json).
The live probe needs the installed broker executable explicitly because it is not packaged
beside this temporary development build. The command above resolves the active installed
version; it does not depend on permission to inspect a running process's executable path.
