# Privacy

[Русская версия](privacy.ru.md)

Local AI runs its models on your computer. Your code, prompts, logs, images and the answers
about them go to Ollama on this computer and nowhere else. The program collects nothing
about you and sends nothing about you anywhere.

This page lists every connection Local AI makes to another computer, what starts it, and what
it sends.

## What stays on this computer

- **Everything a model reads or writes.** The broker is the only part that talks to a model,
  and it talks to Ollama at `http://127.0.0.1:11434/`, on this computer. The one exception is
  a broker you start by hand with another address.
- **The code index.** It is built and kept under `%LOCALAPPDATA%\LocalAi\repositories`.
- **Usage reports and model telemetry.** They are written under
  `%LOCALAPPDATA%\LocalAi\telemetry` and read only by `localai telemetry`. Model telemetry
  refuses any field named for a prompt, an answer, content, an image, a path or a secret.
  Nothing uploads either of them.

## Connections to other computers

| What | Where to | When | What is sent |
| --- | --- | --- | --- |
| Finding and downloading a release | `github.com`, then `api.github.com` if the first answer does not name a release | when the installer opens, unless you pointed it at a folder; again before it installs; on `localai update` | anonymous HTTPS requests for the release's files |
| Model sizes | `registry.ollama.ai` | when the installer checks the computer | the name and tag of each model it can offer |
| Update check | `github.com` | **off by default**; when you turn it on, about once a day | anonymous requests for the newest release manifest and its signature |
| Prerequisites | the sources of `winget` and `npm` | only for the prerequisites you tick in the installer | whatever `winget` and `npm` send |
| Models | the model registry Ollama uses | when you choose models in the installer, run `localai model pull`, or approve the `local_models_sync` tool | whatever Ollama sends |
| GitHub CLI fallback | `github.com`, signed in as you | only when the anonymous download failed and the GitHub CLI is installed | whatever `gh` sends |

The update check sends no identifier, no account and nothing about how you use Local AI. It
never downloads or installs anything by itself.

## Your choices

- Turn the update check on or off in the installer, or with
  `localai policy set --update-check on` and `--update-check off`.
- Install from a release folder instead of GitHub: the installer then reads the release from
  the folder.
- Skip prerequisites and models in the installer and install them yourself.
- Remove expired local data with `localai prune`; use `--dry-run` to preview the retention
  cleanup. This keeps current indexes of active repositories and usage reports and telemetry
  still within their retention period. To remove retained data as well, open the Local AI
  uninstaller from **Apps & features** and select the data categories to delete.

## Changes

A change to any of this changes this page in the same pull request.
