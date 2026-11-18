# First paired evaluation

[Русская версия](README.ru.md)

The baseline and candidate returned identical canonical hit sequences for all **60 queries**
across three source repositories. Ranking, model selection and the `0.43` floor were unchanged.
This validates harness parity, not a quality improvement.

| Source language | Strict recall@10 | Positive intents: both paraphrases succeed | No-answer query false positives | Negative intents: any hit |
| --- | ---: | ---: | ---: | ---: |
| C# | 100% | 5/5 | 10/10 | 5/5 |
| TypeScript | 0% | 0/5 | 10/10 | 5/5 |
| Python | 100% | 5/5 | 9/10 | 5/5 |

The primary grouped results are 10/15 positive intents with both paraphrases successful,
and 15/15 negative intents with at least one hit. Descriptive Wilson 95% ranges are
41.7–84.8% and 79.6–100%, respectively. These synthetic templates are correlated; the
independence assumption is a reference, not a population guarantee. The interval method
is described by [NIST](https://www.itl.nist.gov/div898/handbook/prc/section2/prc241.htm).

TypeScript's strict symbol score remains zero. A separate **post-measurement diagnostic**
found the expected path in top-10 for all ten positive queries, but returned chunks were
named after files rather than the annotated `format`/`validate` declarations. That path
count does not replace the frozen metric or relabel cases. The observed symbol/chunk
representation needs separate investigation. A no-answer hit is an operational false
positive for absent direct implementation, not a hallucination or vulnerability finding.

References were AI-authored and blind-reviewed by an independent agent before measurement.
Human validation and novelty relative to model training are not established. This suite is
now a known regression corpus. Historical v1 figures use different inputs.

Baseline source: `86a54e04c3e77a94f41e63dc4834dfe73639edf6`. Measured candidate source:
`98366969b05d4ae42508520723aba5e219e3fcca`, retained in a private local Git bundle. The public
history excludes its noncanonical timestamp representation; later changes affect offline
verification, tests and reports, not retrieval implementation. Both CLIs ran from copied,
fingerprinted directories. The three prepared source/index generations were reused.

The first offline comparison rejected a serialization difference in the profile calibration
date. All six records denote the same instant and have identical other profile fields.
The comparator was corrected to validate explicit-offset ISO dates and compare that one
field by instant; malformed, missing or different dates still fail. Synthetic tests passed
and saved results were compared again **without replaying retrieval**. Original private
protocol/plan bytes and the failed comparison remain retained. The public protocol is a
marked canonical copy made after measurement, not the original frozen bytes; old/new hashes
are linked in [summary.json](summary.json).

The requested/installed model tag was `qwen3-embedding:8b-q8_0`; model weight digest and
per-request routing proof are unavailable. Candidate usage notices report no model call.
Shared load, caching and cold/warm state were uncontrolled; timings are descriptive and
queue-wait evidence is unknown. TypeScript/Python adapter imports succeeded. C# emitted a
Roslyn snapshot Git diagnostic; one precise definition was confirmed, not full navigation health.

[Evidence archive](evidence.zip) contains exact evaluation outputs, original fingerprint plan,
comparison report, marked public protocol copy and derived runtime fingerprint subsets.
It excludes the private original protocol, full runtime records, account/profile paths and local date
representations; it is not a complete independently reproducible provenance package.
[SHA-256 manifest](evidence.sha256).
