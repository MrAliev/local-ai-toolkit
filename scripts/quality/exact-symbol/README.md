# Exact-symbol shortcut pilot (#619)

[Русская версия](README.ru.md)

**Decision: do not silently replace hybrid search with a single named chunk.**
An explicit exact-name operation may be useful, but needs a separate result contract
and semantic definition identity. No production behavior changes here. Existing
`EmbeddingsUsed=false` means degraded lexical search, not successful exact navigation.

## Reproduction

Use the clean independent mixed corpus from [the latency experiments](../latency/README.md):
87 files / 227 chunks, source commit `2a9b6285b7a7bbecf2c826ae41d3bdd53aabf94e`,
generation `1364bc2a1d1891bae169bc138d397cc633055e64e824193014fd010383fd364b`.
The 34 original cases are supplemented with 20 qualified and 18 short names derived
from relevance labels, plus one absent name. Qualified names without a dot duplicate
their short spelling: 73 cases, 59 distinct query strings. This derived set is a
bounded diagnostic, not an independent quality corpus or a real query distribution.

```powershell
dotnet build scripts/quality/exact-symbol/ExactSymbolProbe.csproj -c Release
$env:CODESEARCH_LATENCY_REVISION = git rev-parse HEAD
dotnet scripts/quality/exact-symbol/bin/Release/net10.0/ExactSymbolProbe.dll `
  <clean-corpus-root> <independent-mixed.json> <output-outside-corpus.json> <broker.exe>
./scripts/quality/exact-symbol/Save-Evidence.ps1 `
  -InputFile <output-outside-corpus.json> -OutputFile <sanitized-evidence.json>
```

Measured source: `d719062cf8caca6505b34a895b0be2b2819fb6e7` plus this diagnostic.
The installed RC22 broker serves every embedding/metadata request through its shared
queue and is not replaced. The probe checks stable source, generation, model digest,
broker process/path and policy; evidence records corpus and binary hashes. No unrelated
tests, indexing or model jobs were launched during measurement. The exporter validates
146 complete observations, repeated retrieval hashes and location comparisons. It
saves exact/first-hit locations, counts and full-hit hashes instead of hybrid snippets;
fixture query strings are retained. Evidence: `evidence/2026-09-22.json`.

## Boundaries

Each arm checks Git independently. The prototype accepts an identifier-shaped query
only when exactly one non-text chunk has that full display name or final dotted
component, using case-sensitive equality. Multiple chunks cause abstention, including
possible partitions of one definition. The existing lexical engine supplies the
unique match's verified snippet. Hybrid SearchAsync must actually use embeddings.

Prototype time includes Git inspection, cached base-index access, matching, lexical
scoring and accepted snippet construction. Hybrid time is the whole SearchAsync.
The index is preloaded; the prototype always runs first and can warm source/OS caches.
Broker/model/query-cache starting state is uncontrolled; neither pass is forced-cold.
Abstention timing excludes hybrid fallback and is not a speedup. Only a clean base is
supported: production overlays, filters and semantic-navigation rules are not implemented.
This is not an MCP end-to-end measurement or a cloud-token savings claim.

## 2026-09-22 result

Both passes had identical hybrid hashes (including scores/order) and prototype decisions.
Per pass: all 34 original natural-language cases were ineligible, 20 qualified and 16
short names selected one chunk, and the absent name selected none. Short `Quote` and
`FindByKey` were ambiguous, matching 2 and 10 chunks respectively.

Of 36 accepted cases, the first hit matched hybrid in 32. For
`ManifestReader.ValidateAllFields`, `RetrySchedule.GetDelaySeconds`,
`VolumePricing.Quote` and `WeightPricing.Quote`, hybrid put the enclosing class first;
the prototype returned the method. All 36 complete lists differed. This changes answer
scope; it does not by itself establish a quality gain or loss.

Nearest-rank milliseconds, accepted cases only (36 per row):

| Sequence | Prototype p50 | Prototype p90 | Hybrid p50 | Hybrid p90 |
| --- | ---: | ---: | ---: | ---: |
| First | 160.552 | 165.608 | 285.678 | 390.398 |
| Repeated | 164.017 | 171.322 | 281.839 | 298.592 |

This supports considering a separately selected exact-name operation, not a general
search speedup: none of the original natural-language cases qualified, and the answers
differ in scope. A future mode needs precise definition identities, explicit ambiguity,
absent/unsupported states, case and qualification rules, overloads/partial or partitioned
declarations, path/kind filters and fresh branch/dirty-overlay behavior. Exact status
must remain distinct from hybrid and degraded lexical results. This completes the
bounded shortcut evaluation in #619; alternating search/file/OCR retention and overall
acceptance remain open.
