# Token estimates · evidence before a number

[Русская версия](Token-estimates.ru.md) · [Home](Home.md)

See the token accounting row in the [product contracts](https://github.com/MrAliev/local-ai-toolkit/blob/main/README.md#product-contracts-and-release-acceptance) for the implementation and its checks.

**Necessary request context is the baseline. Whole-file sizes are not a universal substitute.**

Start with the context the alternative cloud request would have needed. Credit only context the cloud has not already seen, then subtract the response and request overhead. An unknown baseline produces no estimate.

```mermaid
flowchart TB
  task{"Known cloud context?"} -->|Yes| credit["Count needed unseen context"]
  task -->|No| unknown["No estimate"]
  credit --> cost["Subtract response and overhead"]
  cost --> result["Net token estimate"]
```

For an explicit text scenario: `S - V - 2R`, where `S` is necessary unseen source context,
`V` the complete visible response and `R` the request. Local generation is not automatically
credited as avoided cloud generation. Unknown provenance or an unknown baseline remains unknown.

For log triage with a custom question, the comparison baseline is `UNKNOWN`; the entire
log is not credited as avoided context. In compiler-log triage, the model-generated answer
to that question is reported separately from the strictly verified diagnostic inventory.

<details>
<summary>Example: a 100,000-token file, a 4,000-token task</summary>

If the task needs 4,000 source tokens, the returned response is 600 and the request is 100,
the illustrative comparison is `4,000 - 600 - 200 = 3,200`, not 99,200.
If the source was already read, its credit becomes zero and the same call adds 800 tokens.
This example is not a measured result or an account charge.

</details>

- [Illustrative calculator](https://mraliev.github.io/local-ai-toolkit/#tokens)
- [Full methodology and limits](../token-comparison.md)
