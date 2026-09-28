# Impact evidence: C# pilot

[Contents](README.md#contents) · [Русская версия](impact-evidence-c-pilot.ru.md)

```powershell
lai localai semantic impact --root R:\YourRepo --path src/Type.cs --line 12 --column 1 --limit 200 --json
```

The matching MCP tool is `analyze_impact`. The position selects one precise C# symbol
in the current index; refresh with `localai sync` after saved edits. Lines and UTF-16
columns start at 1, with end-exclusive ranges. The report contains the snapshot,
all indexed partial definitions, and direct references plus incoming implementation,
override and base-type relationships. Each evidence item includes its ID, source
coordinate and indexed document SHA-256. Relationship coordinates point to declarations,
not to an invented call site. The default limit is 200 (maximum 10000); `totalEvidence`
and `truncated` disclose omitted results. MCP and human-readable CLI output protect
source-derived data with nonce-bound untrusted-content markers.

This first pilot excludes transitive reachability, unsaved buffers, other languages
and unverified locations. It does not predict runtime breakage, and an absent edge
does not prove that code is unaffected. By default, the tool invokes no model.
Add `--explain`, or set MCP `explain=true`, to request heuristic review questions for
up to 16 returned evidence items. Use `--limit 16` to request an explicitly partial graph
when there are more; `totalEvidence` and `truncated` remain visible. An optional `--model`
(MCP `model`) selects a local model without relaxing the residency policy.

The model selects from fixed review topics; the program renders relationship descriptions,
questions and evidence citations. Model-written claims and source locations are not accepted.
Review questions are not confirmed issues. Missing/unknown/duplicate selections, wrong
relationship topics, extra prose and incomplete JSON reject the explanation (CLI exit 65)
without delivering model text. The source snapshot is rechecked after inference. Empty
graphs make no model call; the explanation prompt is capped at 16000 characters. Model
attempts remain in usage reporting; no measured cloud-token saving is claimed for this pilot.
Without `--explain`, existing graph output and navigation interfaces remain unchanged.

---

← [Updating and removing](updating-and-removing.md) · [Contents](README.md#contents) · [Documentation packages](documentation-packages.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
