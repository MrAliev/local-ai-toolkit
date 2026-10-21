# Code signing policy

[Русская версия](code-signing-policy.ru.md)

**Status: planned onboarding; no SignPath approval or Authenticode certificate is claimed.**

Local AI's Windows programs are intended to be signed through the SignPath Foundation's free code
signing for open-source projects. **Until a release says in its notes that it is signed, its
installer is not Authenticode-signed.** What protects a release today is the release manifest:
it is signed with ECDSA P-256, and the installer accepts a package only when the manifest
verifies against the key built into it and the package matches the manifest. See
[What the installer does](how-it-works.md#what-the-installer-does).

## Attribution

After approval, releases signed through that service will carry this line:

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## What is signed

Only programs built from this repository's source by the project's own release build:

| File | What it is |
| --- | --- |
| `LocalAi.Installer.exe` | the installer, which also removes Local AI |
| `localai-launcher.exe` | the stable entry point every client is registered against |
| `localai.exe` | the command line |
| `codesearch.exe` | the code-search command line |
| `codesearch-mcp.exe` | the code-search MCP server |
| `locallm-mcp.exe` | the local-model MCP server |
| `LocalAi.Broker.exe` | the broker in front of Ollama |

Files the project does not build are never signed with its certificate. That includes the
third-party libraries in the package: `pdfium.dll`, `libSkiaSharp.dll` and
`libHarfBuzzSharp.dll`.

## Team

| Role | Members |
| --- | --- |
| Committers and reviewers | [Anton Aliev (MrAliev)](https://github.com/MrAliev) |
| Approvers | [Anton Aliev (MrAliev)](https://github.com/MrAliev) |

Every change reaches `main` through a pull request.

## Rules

- A signing request is approved by hand, by an approver, for one release at a time.
- Only a build of a commit on `main`, made from source by the release build, is submitted for
  signing. Nothing built on a developer's machine is.
- Every team member uses multi-factor authentication for GitHub and for SignPath.
- A binary that turns out to be signed in breach of this policy is reported to SignPath, and
  the release that carried it is withdrawn.

These are the intended signing requirements, not a statement that onboarding or MFA
verification is already complete. [Publication and signing](publication.md) records the
application, trusted CI build, real certificate policy and renewal checks still needed.

## Privacy

[Privacy](privacy.md) lists every connection Local AI makes to another computer and what
starts it.

## Reporting a problem

Security problems go to the [security policy](https://github.com/MrAliev/local-ai-toolkit/blob/main/SECURITY.md), not to a public issue.
