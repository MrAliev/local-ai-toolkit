# Your first repository

[Русская версия](Getting-started.ru.md) · [Home](Home.md)

## Install or update

For a clean installation, follow the [requirements and installation guide](../installing.md). Verify the release package as described in its release notes and review the dependency and model choices. For an existing installation, follow the [update guide](../updating-and-removing.md). The default residency policy requires the selected model to fit entirely in GPU memory (VRAM).

## Connect a repository and run your first search

Open PowerShell and replace `C:\work\myapp` with the path to the Git worktree you want to index. The `lai` function calls the installed launcher and is available in this PowerShell session.

```powershell
function lai { & "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run @args }
$repo = "C:\work\myapp"
lai localai repo status --root $repo
```

If the status is `NOT_CONFIGURED`, start the initial build. Indexing is opt-in for each repository.

```powershell
lai localai sync --root $repo
```

If a sync is already running, use `lai localai repo status --root $repo --follow` in another PowerShell session with the same `lai` definition and `$repo` value. Follow the reported phase and counts.

Once the build completes, check the index status and search from that worktree. Replace the example query with a question about your code.

```powershell
Set-Location -LiteralPath $repo
lai codesearch status --root $repo
lai codesearch search --query "where are background jobs scheduled" --top 3
```

A matching search returns up to three ranked results with file paths and source line ranges. If the query finds no relevant code, try a symbol or feature name you know exists.

### Keep the index current

Refresh the worktree index after saved edits, a merge or a branch change. MCP clients can check `index_status` and run `index_refresh` when needed. To automate updates for Git events such as a commit or checkout, you can install Git hooks separately.

```powershell
lai localai hooks install --root $repo
```

See the [command-line guide](../using-it-from-the-command-line.md) and the installed LocalAI skill for the full set of options.

## Diagnose a queue or an error

Check the operation ID, phase and counts in `repo status --follow`. Sync targets the requested worktree by default. `--all-worktrees` includes the other worktrees. The admission estimate covers the scheduled work, and live chunk counts, rate and ETA describe the current pass when available.

To request cancellation, use `lai localai sync cancel --root $repo --operation <GUID>` with the operation ID shown at startup. Check the resulting status. The operation may continue after a client timeout.

Copy the exact error and the reported status when requesting help. Check whether another project is using the shared broker before changing its configuration. See the [support checklist](../support.md) for recovery steps.
