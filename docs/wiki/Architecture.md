# Architecture · one shared inference boundary

[Русская версия](Architecture.ru.md) · [Home](Home.md)

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
flowchart TB
  subgraph clients["Clients"]
    agent["Codex / Claude Code"]
    cli["Command line"]
  end
  agent --> launcher["Stable launcher"]
  cli --> launcher
  launcher --> search["CodeSearch"]
  launcher --> local["LocalLm"]
  search --> index["Repository base + worktree overlay"]
  search --> queue["Durable shared queue"]
  local --> queue
  queue --> broker["Broker: scheduling and residency"]
  broker --> models["Configured model backend"]
```

| Boundary | Responsibility |
| --- | --- |
| Launcher | Stable executable path across installed runtime versions |
| CodeSearch | Locate matching code and navigate indexed symbols |
| LocalLm | Bounded tasks over files, logs, images and translation |
| Index | Base generation plus overlay matching the worktree state |
| Broker | Shared scheduling, backend access and residency checks |
| Calling client | Decide what results it forwards to a cloud service |

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
sequenceDiagram
  participant C as Client
  participant S as CodeSearch
  participant B as Broker
  C->>S: Search a repository
  S->>S: Check matching base and overlay
  S->>B: Embed the query
  B-->>S: Query embedding
  S-->>C: Ranked fragments and source locations
```

See [the full architecture](../how-it-works.md) and [responsibility map](../architecture/responsibility-map.md).
The drawings explain boundaries; they are not live telemetry.
