# Bounded navigation diagnostics

[Contents](README.md#contents) · [Русская версия](bounded-navigation-diagnostics.ru.md)

The default MCP `index_status` remains a cheap header check. `HeaderOnly` does not establish full integrity, language coverage or readiness of every navigation query. For one explicit query, supply `navigationPath`, a one-based `navigationLine` and UTF-16 `navigationColumn`, and `navigationOperation` (`definition` or `references`). For example:

```json
{"root":"R:/example","navigationPath":"src/Example.cs","navigationLine":42,"navigationColumn":1,"navigationOperation":"references"}
```

The additional, untrusted-wrapped JSON reports `HeaderEvidence`, `Freshness`, language, position, operation, `Precision`, `ResultCount` and `Reason`. `Coverage` remains `Unknown`: neither readable headers nor a successful location proves complete document or language coverage. A query performs at most one existing navigation operation, without an index rebuild, repository scan or persistent health cache. It cannot be combined with `runtimeEvidenceOnly`.

Freshness and query precision are separate facts. A stale index can still answer a particular query; a current header cannot guarantee a precise answer. An empty precise-reference result may fall back to bounded text matches, explicitly marked as degraded/heuristic. This means the query found no recorded precise references; it does not mean that no symbol or no uses exist in the repository. A zero-result probe retains `Precision="Unknown"` rather than proving global absence.

---

← [Broker jobs and interrupted waits](broker-jobs-and-interrupted-waits.md) · [Contents](README.md#contents) · [Product contracts and release acceptance](product-contracts-and-release-acceptance.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
