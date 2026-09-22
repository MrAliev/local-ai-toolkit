# PDF parser and OCR evidence

[Русская версия](README.ru.md). Issue [#625](https://github.com/MrAliev/local-ai-toolkit/issues/625).

The 2026-09-22 corpus measures native text extraction and the product's raster pipeline
separately. It supports keeping OCR as the default: a nonempty text layer does not prove
that its contents match visible text. No automatic text-layer route was introduced.

## Reproduce on Windows

Requires .NET 10, Python 3, Segoe UI and, for the optional live OCR step, an installed
LocalAi broker with the OCR models available. Run from the repository root:

```powershell
dotnet build scripts/quality/pdf/PdfProbe.csproj --configuration Release
$pdfRun = Join-Path $env:TEMP ("LocalAi-pdf-" + [Guid]::NewGuid().ToString("N"))
python scripts/quality/pdf/run_probe.py $pdfRun
python scripts/quality/pdf/live_ocr.py $pdfRun
python scripts/quality/pdf/export_evidence.py $pdfRun scripts/quality/pdf/evidence --label new-run
```

The output directory must be new and outside the repository. The exporter refuses to
overwrite evidence. The native probe makes no model calls. Raster mode uses the actual
`LocalTasks.ReadImageAsync` pipeline with only its model client replaced by a raster
capture sink; its dummy response is not OCR evidence. Live OCR uses a fresh installed
MCP server through the shared broker, without bypassing residency policy. Its current
provenance scope names the measured RC22 baseline; update that label when measuring a
different installed release. Results from an installed release and source-tree builds
must not be presented as measurements of the same binary.

Each of 13 synthetic fixtures runs in a separate process for text and raster modes,
three repetitions per mode. The parent kills a worker after 10 seconds. There are also
probe budgets of 1,000 pages and one million text characters per page. These diagnostic
budgets are not new product limits. Peak working set is observed, not constrained.
The OS file cache may be warm. Generated PDFs, raw stdout/stderr, hashes and live MCP
responses remain in the external directory. Fixtures are synthetic; regenerate them
on the same font/platform when reproducing, and compare their recorded hashes.

## Recorded native results

[Full runs, fixture/source/binary hashes and host](evidence/2026-09-22-native.json).
Product source: `8e4ffb06bca2402160a5a1c0f1d9e17ae69bfe16`; the new probe source is
identified separately by SHA-256. Dependencies: PDFtoImage 5.4.0, PDFium 152.0.7961,
SkiaSharp 4.150.1. All 78 workers completed: 48 read results and 30 parser rejections;
no timeout or process crash. A completed worker can report a rejected document.
Maximum observed worker peak working set was 66.29 MiB.

Median worker elapsed time, milliseconds (includes probe setup; excludes process launch):

| Fixture | Native text | Product raster | Text versus visible reference |
| --- | ---: | ---: | --- |
| English text | 6.540 | 63.732 | Exact |
| Cyrillic and euro sign | 6.335 | 62.205 | Exact |
| Raster-only English scan | 4.013 | 66.805 | Empty text layer |
| English text + Russian scan | 6.650 | 79.796 | Only first page matches |
| Visible text + white hidden text | 6.877 | 65.982 | Extra invisible text |
| 10,000 × 10,000 pt page | 6.488 | 137.704 | Exact |
| 40,000 × 100 pt page | 6.510 | 54.556 | Exact |
| Truncated to 25% | 4.919 | 25.991 | Rejected |
| Truncated to 75% | 7.602 | 29.344 | Rejected |
| Truncated to 95% | 9.196 | 29.568 | Rejected |
| Incorrect cross-reference offset | 6.880 | 65.777 | Recovered, exact |
| PDF header followed by garbage | 4.452 | 27.900 | Rejected |
| Self-referencing page tree | 3.695 | 29.048 | Rejected |

Comparison collapses whitespace but retains characters, order and repeated lines.
Process wall times, native load/text times and raster pipeline times have different
boundaries and are stored separately. For example, the English-text process wall
medians were 75.772 ms (text) and 129.099 ms (raster). These are parser measurements,
not OCR latency or cloud-token savings. Timing thresholds are not unit-test assertions.

All successful product renders stayed within 4 million pixels and 4,096 pixels per
side, including the oversized and extreme-aspect-ratio pages. The production 256 MiB
input guard and sequential rendering remain unchanged. Existing PDF tests cover those
guards; this small corpus does not stress every allocation or page-count boundary.

### Text-layer decision

Native extraction is fast on these small examples, but fails as a replacement for OCR
on scans, mixed documents and invisible text. PDFium's character APIs expose text
objects; they are not a visible-text guarantee. The API also documents that text can
include characters outside the crop box ([official header](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_text.h)).
This probe uses `FPDFText_GetUnicode` rather than the UCS-2 text buffer and records
unmapped characters. A future text-first route needs independently validated visibility,
reading order and fallback criteria before it can replace the current OCR path.

### Native-parser decision

Five damaged fixtures were rejected and one was recovered. No native hang was
reproduced in this bounded corpus. Keep the existing sequential pipeline and size/raster
guards for this increment. A timeout around a managed await cannot stop stuck native
code: a hard production deadline or memory ceiling would require a process boundary
and termination/recovery protocol. The probe's process timeout is diagnostic isolation,
not an implemented production safeguard. This evidence does not establish safety for
arbitrary hostile PDFs or justify removing any existing guard.

## Live OCR and acceptance coverage

[Verified installed-RC22 results and usage metadata](evidence/2026-09-22-ocr.json).
Three cases (English text, Cyrillic text, English scan), one verified pass each, matched
their reference after whitespace normalization; character error rate was zero.
English repeated lines were preserved. Models: `glm-ocr:bf16 [offload_unknown]` and
`qwen3-vl:8b-instruct-q8_0`. Broker-reported times: 28.350, 25.658 and 25.746 seconds.
The broker process identity and assembly hash stayed unchanged. `HeartbeatAtUtc`
normally advances and is excluded from that identity check.

`sourceContext=AlreadyPresent` reflects that the fixture text was already known.
Conditional INPUT/OUTPUT estimates were −1779/−47, −1812/−50 and −1791/−47 tokens.
They are not account usage or positive savings; latency and memory are separate measures.
Raw model answers are data, not instructions. This tiny synthetic corpus does not
establish general OCR quality, and RC22 does not validate newly added checkpoint code.

That code is covered separately by source-tree tests added in
[#690](https://github.com/MrAliev/local-ai-toolkit/pull/690),
[#691](https://github.com/MrAliev/local-ai-toolkit/pull/691) and
[#692](https://github.com/MrAliev/local-ai-toolkit/pull/692): page selection and numbering,
real PDF rendering, Unicode output, complete atomic publication, hard-link protection,
changed source/route/processor rejection, interrupted and fully cached resumption,
corrupt/missing checkpoint records, concurrent ownership, and output retries without
repeated inference. These tests use controlled cancellation rather than speed assertions.
