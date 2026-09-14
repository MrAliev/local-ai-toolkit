# Documentation diagram experiment

[Русская версия](README.ru.md)

Measured on 2026-09-14 against twenty dependency graphs from the PDF reader, broker and
installer at commit a75796e. `results.json` preserves every answer, the requested and
returned model name, receipt durations and structural validation result. Each model
ran once per graph through the shared broker, without routed fallback. No model or
residency policy was installed or changed by the experiment.

| Model | Structurally valid | Mean execution | Total queue time |
|---|---:|---:|---:|
| qwen3.5:9b | 5 / 20 | 14.8 s | 100.8 s |
| gpt-oss:20b | 14 / 20 | 29.9 s | 96.2 s |

This is a small format-following experiment, not a visual-quality or prose benchmark.
The check parses bounded XML with DTDs prohibited, rejects active/external content,
and requires every supplied node, exact label and directed edge. Arrowhead polygons
are not counted as extra edges. The initial validator counted them twice; the figures
above were recomputed from the saved answers after fixing that error. Font sizes,
overlap and arrow placement are not measured here. The production renderer lays out
the verified topology again and uses bundled font outlines, so it does not trust model
coordinates or system fonts.

The result changes the initial recommendation: gpt-oss is first for the combined
overview/diagram document, followed by one repair attempt and then qwen. Translation
uses the existing `PlainTranslation` route. Three invalid documents fail the unit;
syntax and evidence checks do not establish semantic correctness of natural language.

To repeat on a configured Windows installation with both models already available:

```powershell
dotnet build benchmarks/documentation-diagrams/DiagramBenchmark.csproj --configuration Release
./benchmarks/documentation-diagrams/Run.ps1 -OutputDirectory "$env:TEMP/documentation-diagrams-repeat"
```

The script makes forty model calls and can take twenty minutes. Choose an output
directory outside the repository. It preserves prompts, answers and receipts; a broker
refusal stops the run without changing policy or switching to a different model.

The invalid earlier experiment using a routed model override was discarded: a routed
override permits fallback and cannot establish which requested model produced an answer.
