# Token estimates · evidence before a number

[Русская версия](Token-estimates.ru.md) · [Home](Home.md)

**Necessary request context is the baseline. Whole-file sizes are not a universal substitute.**

```mermaid
---
config:
  theme: base
  themeVariables:
    primaryColor: "#CCE4F7"
    primaryTextColor: "#1A1A1A"
    primaryBorderColor: "#2D7FD3"
    lineColor: "#0067C0"
    secondaryColor: "#FAFAFA"
    tertiaryColor: "#F3F3F3"
---
flowchart TD
  task["Alternative task context known?"] -->|No| unknown["Cannot estimate"]
  task -->|Yes| seen["Already in cloud context?"]
  seen -->|Yes| zero["No source-reading credit"]
  seen -->|No| credit["Credit only declared necessary context"]
  zero --> cost["Subtract complete response and request overhead"]
  credit --> cost
  cost --> result["Conditional saving or additional tokens"]
```

For an explicit text scenario: `S - V - 2R`, where `S` is necessary unseen source context,
`V` the complete visible response and `R` the request. Local generation is not automatically
credited as avoided cloud generation. Unknown provenance or an unknown baseline remains unknown.

<details>
<summary>Example: a 100,000-token file, a 4,000-token task</summary>

If the task needs 4,000 source tokens, the returned response is 600 and the request is 100,
the illustrative comparison is `4,000 - 600 - 200 = 3,200`, not 99,200.
If the source was already read, its credit becomes zero and the same call adds 800 tokens.
This example is not a measured result or an account charge.

</details>

[Try the illustrative calculator](https://mraliev.github.io/local-ai-toolkit/#tokens) ·
[Full methodology and limits](../token-comparison.md).
