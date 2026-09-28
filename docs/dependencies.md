# Dependencies

[Contents](README.md#contents) · [Русская версия](dependencies.ru.md)

The installer checks each one. Git, Ollama, the GitHub CLI, the .NET SDK, Node.js and Python
it offers to install through WinGet, and the two SCIP indexers through npm once Node.js is
there; where WinGet is missing, it names each one's official download page instead.

**Without these it does not work at all:**

| | Why |
| --- | --- |
| **Windows** | Windows 10 or 11 on x64; the installer reports anything else as unsupported |
| **Git** | a repository is identified by its git directory, and the hooks live there |
| **Ollama** | every model runs on it; Local AI only ever reaches it through the broker |
| **A GPU the model fits in** | under the default policy, `RequireFullVram`, a model that does not fit entirely in video memory is refused rather than run slowly. The policy can be relaxed — see below |

**Needed per language — search works without them, but coarser:**

| | Why |
| --- | --- |
| **.NET SDK 10** | the C# semantic index behind precise navigation is built by loading the solution through the installed SDK. Cutting C# into types and members does not need it: Roslyn does that inside Local AI |
| **Node.js 20** + **SCIP TypeScript** | declaration-level chunking for TypeScript and JavaScript |
| **Python 3.10+** + **SCIP Python** | declaration-level chunking for Python |

**Optional:**

| | Why |
| --- | --- |
| **GitHub CLI** | used only when the anonymous HTTPS download of a release fails — a private repository, or a release host that is unreachable while the API is not. The installer tries HTTPS without an account first, and installing from a folder needs neither |

The residency policy can be relaxed deliberately: `localai policy set --residency
AllowPartialOffload` or `AllowCpu`. A model that then runs partly or wholly outside video
memory is marked `[partial_offload]` or `[cpu]` beside its name in the call's report line.

---

← [How it works](how-it-works.md) · [Contents](README.md#contents) · [Installing](installing.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
