# Privacy

[Русская версия](privacy.ru.md)

Local AI runs model inference through Ollama on your computer by default. Local tools return
results to the client that called them, including model answers and selected source excerpts.
A cloud agent can include those results in requests to its cloud provider. Local inference
does not guarantee that returned content stays on this computer; the client's configuration
and data policy govern that transfer.

This page lists the connections Local AI itself makes to another computer, what starts them,
and what they send. Connections made by a cloud client are controlled by that client.

## Local processing and storage

- **Model requests.** The broker is the only part that talks to a model, and by default it
  talks to Ollama at `http://127.0.0.1:11434/`, on this computer. A broker you start by hand
  with another address sends model requests to that address. Results returned to a client
  can then be forwarded by that client, as described above.
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
| Update check | `github.com` | **off by default**; when you turn it on, about once a day | anonymous manifest/signature requests by default; the separately selected GitHub CLI authentication mode uses the signed-in account |
| Prerequisites | the sources of `winget` and `npm` | only for the prerequisites you tick in the installer | whatever `winget` and `npm` send |
| Models | the model registry Ollama uses | when you choose models in the installer, run `localai model pull`, or approve the `local_models_sync` tool | whatever Ollama sends |
| GitHub CLI access | `github.com`, signed in as you | installer release access requires explicit selection/consent; update checks require their own explicit GitHub CLI authentication setting | whatever `gh` sends |

Anonymous update checks send no account authentication or Local AI usage identifier.
Explicit GitHub CLI mode authenticates as the signed-in GitHub account. Neither mode sends
project files or Local AI usage reports. The check reads the manifest and signature; it
does not download the release package or install an update by itself.

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

## Documentation website

The interactive landing page calculates in your browser without analytics or model calls.
Reference pages load Mermaid 11.12.1 from jsDelivr to render diagrams; this sends a normal
web request (including your IP address and browser metadata) to that CDN. GitHub hosts the
site and Wiki under its own privacy policy. These website requests are separate from the
installed runtime.
