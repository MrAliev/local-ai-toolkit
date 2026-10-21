# Getting help

[Русская версия](support.ru.md)

Use [GitHub issues](https://github.com/MrAliev/local-ai-toolkit/issues) for reproducible
bugs and feature proposals. Search existing issues first. For a usage question, include
the command and expected result; do not post source files, credentials or entire logs.

## A useful diagnostic report

Include the release version, Windows architecture, client, the exact failing command,
the error text, expected behavior and minimal reproduction. Add relevant output from
`localai doctor`, `localai repo status --root <repository>` and `localai model status`
through the installed launcher. Redact user paths, repository names, secrets and unrelated
projects. An excerpt is usually more useful than an entire log.

## Common paths

| Symptom | Check |
| --- | --- |
| Repository not configured | [Repository setup](using-it-from-the-command-line.md) — connection is opt-in |
| Index is stale or missing an overlay | Status and a refresh for the current worktree; do not start duplicate builds |
| Model cannot fit | [Residency policy](installing.md); do not unload another session's model |
| No savings or unknown | [Token comparison](token-comparison.md); inspect the runtime version and evidence |
| Release cannot be discovered | Public release URL, network access and [installation](installing.md) |

For a suspected vulnerability use the [security policy](https://github.com/MrAliev/local-ai-toolkit/blob/main/SECURITY.md). Community
support is best effort, without a response-time guarantee.
