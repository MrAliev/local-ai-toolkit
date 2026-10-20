# Updating and removing

[Contents](README.md#contents) · [Русская версия](updating-and-removing.ru.md)

**On this page**

- [Update](#update)
- [Clear the index, keep the product](#clear-the-index-keep-the-product)
- [Remove it entirely](#remove-it-entirely)

## Update

```powershell
lai localai update
```

It downloads a release, **verifies its signature**, installs a new version directory and
points the launcher at it. The paths written into your clients do not change — they point at
the launcher, not at a version. It refuses while the broker has queued work; `--wait` waits
for the queue to drain (up to 30 minutes), and `--force` installs the newest release even when
it is not newer than the installed one.

The update check is **off** by default, so the installation never goes looking for new
versions on its own. To turn it on and set how often:

```powershell
lai localai policy set --update-check on
lai localai policy set --update-check-interval-hours 24
```

`update` uses the published GitHub releases. To update client instructions as well, download
the installer and package from the same release and run the installer with the relevant
client selected. A runtime-only update preserves the existing client instructions.

## Clear the index, keep the product

```powershell
lai localai prune            # drop stale generations, overlays, queue archive, old telemetry and old versions
lai localai prune --dry-run  # see what would go first
```

`prune` also removes the record of a repository whose Git directory is gone, and of one that
never published a generation and has not been updated for longer than the archive retention.
A deleted worktree loses only its overlays, and a volume that cannot be reached leaves the
record alone.

## Remove it entirely

Local AI registers in **Apps & features** as `Local AI Toolkit` — remove it from
there like any program. A wizard opens, and each part is decided **separately**, not with one
"yes":

| Part | What it is |
| --- | --- |
| Binaries, launcher, version pointer | the uninstall proper |
| Repository indexes and their overlays | hours of GPU work, and the documentation packages stored with them; only worth dropping if you are leaving for good |
| Runtime settings | residency, updates, language servers — what a reinstall would otherwise honour |
| Queue, archive, quarantine, telemetry | transient broker state; nothing to preserve |
| Everything else in the runtime root | anything the installer does not recognise, listed by path |
| Git hooks | per connected repository |
| Claude and Codex integration | separately for each: unregister the MCP servers and remove the instruction block |
| Release signing keys | **always asked** — never removed by default under any preset |

There are ready-made presets: remove everything except the signing keys; remove the binaries,
the transient state and anything unrecognised, keeping the indexes and settings; take Local AI
out of Claude, Codex and the Git hooks without touching the runtime. A preset is just a set of boxes
already ticked, and any of them can be changed.

**Models stay.** They belong to Ollama rather than to Local AI, and you may want them for
something else. Remove those through Ollama.

**The installation record stays too** — deliberately, outside the runtime directory. It is
the log of what installs did to this machine.

---

← [What language it answers in](what-language-it-answers-in.md) · [Contents](README.md#contents) · [Impact evidence: C# pilot](impact-evidence-c-pilot.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
