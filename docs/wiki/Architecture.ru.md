# Архитектура · общая граница модельных вычислений

[English version](Architecture.md) · [Главная](Home.ru.md)

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
  subgraph clients["Клиенты"]
    agent["Codex / Claude Code"]
    cli["Командная строка"]
  end
  agent --> launcher["Постоянный launcher"]
  cli --> launcher
  launcher --> search["CodeSearch"]
  launcher --> local["LocalLm"]
  search --> index["База репозитория + overlay worktree"]
  search --> queue["Общая долговечная очередь"]
  local --> queue
  queue --> broker["Брокер: планирование и размещение"]
  broker --> models["Настроенный модельный backend"]
```

| Граница | Ответственность |
| --- | --- |
| Launcher | Постоянный путь запуска при обновлениях runtime |
| CodeSearch | Поиск кода и навигация по индексированным символам |
| LocalLm | Ограниченные задачи по файлам, журналам, изображениям и переводу |
| Индекс | Базовое поколение и overlay состояния рабочего дерева |
| Брокер | Общее планирование, доступ к backend и проверки размещения |
| Вызывающий клиент | Решение о передаче результатов облачному сервису |

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
  participant C as Клиент
  participant S as CodeSearch
  participant B as Брокер
  C->>S: Поиск по репозиторию
  S->>S: Проверка базы и overlay
  S->>B: Embedding запроса
  B-->>S: Вектор запроса
  S-->>C: Фрагменты с рейтингом и координатами
```

См. [полную архитектуру](../how-it-works.ru.md) и [карту ответственности](../architecture/responsibility-map.ru.md).
Схемы объясняют границы, а не показывают текущую телеметрию.
