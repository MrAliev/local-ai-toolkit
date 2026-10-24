# Your first repository

[Русская версия](Getting-started.ru.md) · [Home](Home.md)

## 1 · Install deliberately

Read [requirements and installation](../installing.md). Use the latest release from the
project, verify it as described in the release notes, and review prerequisite/model choices.
The default residency policy requires the selected model to fit fully in VRAM.

## 2 · Connect explicitly

Use the installed launcher. The walkthrough defines the `lai` helper and the commands;
do not invoke the model backend directly.

```powershell
lai localai repo status --root C:\work\myapp
lai localai sync --root C:\work\myapp
lai localai hooks install --root C:\work\myapp
```

Repository setup is opt-in. A connected repository is not necessarily ready: inspect the
build's phase and counts. Do not start duplicate builds while a sync is active.

By default, sync targets the requested worktree; `--all-worktrees` explicitly includes the
others. The admission estimate covers all scheduled work, while live progress describes
the current pass. An unknown overall ETA remains unknown. Keep the operation ID printed
at startup: `lai localai sync cancel --root C:\work\myapp --operation <GUID>` cancels
that exact operation. A client timeout does not establish that the backend has stopped.

## 3 · Keep the right context

After saved edits, a merge or a branch change, refresh the working tree before searching it.
Hooks help when installed; do not assume every repository has them. For MCP clients, use
`index_status`, then `index_refresh` when needed. Follow the current [CLI walkthrough](../using-it-from-the-command-line.md)
and the installed LocalAI skill for exact command options.

<details>
<summary>When something fails</summary>

Report the actual refusal and status. A missing overlay, unavailable model and unsupported
residency have different remedies. Check the shared runtime before changing anything;
another project may be using it. [Support checklist](../support.md).

</details>
