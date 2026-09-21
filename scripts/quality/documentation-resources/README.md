# Shared documentation resources — #623 increment

[Русская версия](README.ru.md)

Baseline: `2e27375de42a7a6466b8e6ad128db75faac3b532`. This increment shares the
two HTML fonts across the repository's retained artifact/package graph, which already uses
cross-package artifact links. A single SHA-256-named CSS file embeds both fonts as data URLs,
rather than copying them into each page. Apply uses the same layout under `docs/generated/_assets`.
The standalone option embeds both fonts, the diagram and notices in each page. Its other-language
navigation still needs the companion page. The public renderer returns standalone HTML.

## Retained measurement

The probe builds 1, 5 and 12 synthetic types with a deterministic bilingual draft, then
changes only the presentation snapshot twice. It applies each version into a private
worktree. No broker/model, source/index capture, network or existing runtime is used.
This is a storage measurement, not a generation quality, latency or cloud-token benchmark.
The baseline and candidate use the same inputs. Source hashes and every measured row
are in [evidence](evidence/comparison.json).

| Types / retained revisions | Before, bytes | Shared, bytes | Standalone, bytes |
| --- | ---: | ---: | ---: |
| 1 / 1 | 3,128,910 | 1,686,586 | 3,462,180 |
| 5 / 1 | 15,634,568 | 2,650,976 | 17,299,982 |
| 12 / 1 | 37,523,634 | 4,342,821 | 41,521,948 |
| 12 / 3 | 112,525,864 | 10,097,441 | 124,520,806 |

These are sums of logical file lengths in the private documentation store, including
checkpoints, artifact metadata, review pages, pointers and apply baselines. They are not
allocated disk clusters. Elapsed-time fields in manifests can vary by a few bytes.
The shared stylesheet totals **1,442,848 bytes** for every shared-mode row. With 12 types,
the first applied tree occupies **4,289,082 bytes**, versus **37,471,946** before.
The standalone result is deliberately larger: the old HTML embedded fonts but still needed
the diagram and license files. No old artifact or pack is removed by this increment.

All 18 candidate rows match the corresponding baseline content digest. The comparison:

- checks the shared CSS content address, inlines it for comparison, and checks both font byte
  arrays against the bundled resources before normalizing their CSS URLs;
- normalizes the exact embedded PNG and notices back to their original references;
- normalizes only PDF random six-letter font subset prefixes and the two XMP UUIDs;
- compares every other rendered byte, including Markdown, SVG, PNG and licenses, exactly.

The first build replays one draft per type; subsequent presentation changes replay zero.
Runtime and applied stylesheet links resolve to files. A live browser check is **not established**:
the browser tool's URL policy rejected the local `file://` page. Browser font-loading behavior
and visual acceptance remain outside this evidence. No alternate browser route was used.
The shared CSS deliberately embeds fonts as data URLs: external font files opened through
`file://` can be blocked by browser CORS rules ([MDN](https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/CORS/Errors/CORSRequestNotHttp)).

## Reproduce

Use fresh directories outside the repository. Set `-SourceRepository` to the candidate tree
for the second probe; omit it for a clean baseline archive.

```powershell
$probeRoot = Join-Path $env:TEMP ('documentation-resources-' + [guid]::NewGuid().ToString('N'))
$baseline = '2e27375de42a7a6466b8e6ad128db75faac3b532'
./Prepare-Probe.ps1 -Repository R:/LocalAi -Revision $baseline -Destination "$probeRoot/before"
./Prepare-Probe.ps1 -Repository R:/LocalAi -Revision $baseline -SourceRepository R:/LocalAi -Destination "$probeRoot/after"
& "$probeRoot/before/probe/bin/Release/net10.0/DocumentationResourcesProbe.exe" "$probeRoot/before-runs" "$probeRoot/before.json" "$probeRoot/before/source-hashes.json"
& "$probeRoot/after/probe/bin/Release/net10.0/DocumentationResourcesProbe.exe" "$probeRoot/after-runs" "$probeRoot/after.json" "$probeRoot/after/source-hashes.json"
./Save-Evidence.ps1 -Before "$probeRoot/before.json" -After "$probeRoot/after.json"
```

The initial red test was `Package_pages_share_font_files_and_apply_preserves_their_relative_links`:
it failed because the baseline HTML still contained `data:font/ttf;base64,`.
Regression coverage includes mode switching without generation, old package links, legacy
metadata, missing/corrupt CSS, resource traversal and missing metadata, manual destination
edits, nested junction rejection, interrupted builds and failed generation fallback.

The `Resources` artifact field is optional for old standalone artifacts. Shared artifacts
must declare one content-addressed stylesheet; apply validates it before destination writes.
Changing HTML mode invalidates the render key only. Bump the renderer cache version when
bundled resources or rendering rules change. Public report JSON fields remain unchanged;
apply's copied/unchanged counts now include shared resources, counted once.

**#623 remains open.** Retention planning/dry run, current/latest/applied/open-pack protection,
active-build coordination and previous-version protection are the next increment. It must
also retain every font referenced by retained artifacts and resumable text checkpoints.
