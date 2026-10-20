# Installing

[Contents](README.md#contents) · [Русская версия](installing.ru.md)

Install a signed package from [GitHub Releases](https://github.com/MrAliev/local-ai-toolkit/releases/latest).
Installing a published release requires neither a .NET SDK nor the private release signing key.

**1. Download the release.** From the same release, download `LocalAi.Installer.exe`,
`release-manifest.json`, `release-manifest.sig` and `localai-package.zip`. Keep the three
package files together and point the installer's package page at that folder. The installer
verifies the manifest against its built-in release key before accepting the package.

**Optional: build from source.** This is for maintainers with the release signing key.
It needs `pwsh` 7 and a .NET 10 SDK of version 10.0.302 or later (`global.json` pins it).
Choose the intended build version in place of this example:

```powershell
pwsh scripts/publish-localai-release-win-x64-self-contained.ps1 -ReleaseVersion 0.2.2 -SignManifest
```

You get the installer at `publish/LocalAi.Installer/LocalAi.Installer.exe` and the release
folder `publish/release`: the package, and the manifest and signature that `-SignManifest`
writes with the release signing key. Without `-SignManifest` there is no manifest, and the
installer will not take the folder. Point the installer's package page at `publish/release`.

Add `-CompressBinaries` to reduce executable size on disk, with a possible startup penalty;
compression is off by default. Both modes remain self-contained: the bundled .NET runtime,
package layout, native libraries and validation rules are preserved. Existing language-tooling
requirements remain unchanged.

The first Windows x64 pilot (2026-09-23, SDK 10.0.401, source `ac6b47fc3dd0`) compared otherwise
identical `0.0.0-size` builds. Runtime artifacts, including the launcher and native libraries,
decreased **573.57 → 287.31 MiB (49.9%)**. The folder-install payload (installer + ZIP, excluding
small manifest/signature files) decreased **386.27 → 308.75 MiB (20.1%)**; the ZIP alone decreased
249.54 → 244.87 MiB. Five alternating launches per variant used a new process, an empty bundle
extraction directory, a PATH containing only System32, and an unavailable global .NET root:

| Startup path | Uncompressed median | Compressed median |
| --- | ---: | ---: |
| `localai --help` | 168.606 ms | 228.417 ms |
| `codesearch --help` | 161.615 ms | 208.985 ms |
| launcher usage refusal | 31.951 ms | 66.372 ms |
| broker usage refusal | 38.803 ms | 78.899 ms |
| CodeSearch MCP initialization | 226.948 ms | 290.966 ms |

Usage output and MCP initialization/tool descriptions matched across variants; these entry
points started without a global SDK/runtime. The OS file cache was **not** cleared, so these
are process/extraction-cache cold starts, not a clean-boot measurement. Installer UI startup
and a clean-machine offline installation remain part of final acceptance (#618); the opt-in
pilot does not replace it. Both ZIPs passed the
production package verifier with an ephemeral test trust key, without modifying the installed
runtime or using the release signing key. Measurements and artifact hashes are retained in
[results.json](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/quality/standalone-size/results.json); reproduce the comparison with
[compare.py](https://github.com/MrAliev/local-ai-toolkit/blob/main/scripts/quality/standalone-size/compare.py) after publishing two distinct output
folders with identical release/version arguments, adding `-CompressBinaries` only to the second.
This is a storage/startup tradeoff, not a cloud-token savings measurement.

## What it will compute on

The product is built for a graphics card but does not demand one. What you get is your
choice at install time — you pick the residency policy — and it can be changed later with
`localai policy set --residency`.

| Policy | Where the model lives | Speed |
| --- | --- | --- |
| `RequireFullVram` — the default | entirely in video memory | measured on the development machine during a full build of this repository: embedding **12.4–12.9 chunks/s** (#389) |
| `AllowPartialOffload` | part in video memory, part in system memory | in the product's own words, **slower**. Needs an adapter that holds at least part of the model: a load is accepted only when Ollama reports some of it in video memory |
| `AllowCpu` | entirely on the processor | **substantially slower**, but it works without a usable adapter |

The first row is the measurement recorded in #389. For the other two I have no numbers, and
did not invent any: those are the words the product itself uses for these modes in the usage
text of `localai policy`, which it prints for an argument it does not recognise.
`localai policy show` names the current policy and, when it is relaxed, warns that responses
may be substantially slower than a fully resident load.

**The ceiling is one card, not the sum.** When Local AI estimates whether a model fits in video
memory — on the installer's models page and in `localai hardware plan` — it counts only the
card with the most dedicated video memory and never adds cards together: two 4 GB cards are
planned as one 4 GB card. After a model loads, the broker compares one figure Ollama reports,
how much of the model is in video memory, with the model's size, and does not look at which
card holds it.

**2. Run the installer.** The wizard walks through:

- the language: it starts in the one you chose last time, otherwise in Russian on a Russian
  system and English on any other, and the English/Русский links on every screen switch it;
- which prerequisites are missing — and installs the ones you pick;
- which models to install — it offers the ones that fit your card;
- which agent clients to register — Claude Code, Codex.

It registers the MCP servers with those clients and adds an instruction block to
`~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`, so the agent knows these tools exist and when
to reach for them.

Check the finish screen before continuing: package installation, model validation and each
client's configuration have separate results. A copied package alone does not establish that
models or clients are ready. After cancellation or a partial failure, the screen explains the
next action and whether recorded changes can be rolled back. Technical logs retain their
original text; save the report, or copy the log if saving failed, before closing the window.

Removal also saves its result outside the runtime directory. If it stops partway through,
the report distinguishes completed, failed, unknown, not-started and retained items from the
execution journal. You can open its folder, save another copy or copy the details. Deleted
data is not automatically restored; continue to a clean installation only after removal succeeds.

**3. Connect a repository.** Indexing is opt-in per repository — nothing is read until you
ask.

```powershell
& "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run localai sync --root <repo>
& "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run localai hooks install --root <repo>
```

The first build reads the whole repository and prints its progress in figures. After that the
hooks keep the index current after a commit, checkout, merge or rebase.

**4. Check it landed.**

```powershell
& "$env:LOCALAPPDATA\LocalAi\bin\launcher\localai-launcher.exe" run localai doctor
```

---

← [Dependencies](dependencies.md) · [Contents](README.md#contents) · [Using it with a cloud agent](using-it-with-a-cloud-agent.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
