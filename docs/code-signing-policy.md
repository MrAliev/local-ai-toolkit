# Code signing policy

[Русская версия](code-signing-policy.ru.md)

**Current releases use a signed package manifest. Authenticode is planned in [#210](https://github.com/MrAliev/local-ai-toolkit/issues/210).**

The installer verifies the ECDSA P-256 manifest against its embedded public key and
checks that the package matches the manifest. Authenticode verifies the Windows
publisher of an executable. Check the release notes for its Authenticode status.
[What the installer does](https://mraliev.github.io/local-ai-toolkit/how-it-works.html#what-the-installer-does).

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

## Planned Authenticode requirements

- A signing request is approved by hand, by an approver, for one release at a time.
- Only a build of a commit on `main`, made from source by the release build, is submitted for
  signing. Nothing built on a developer's machine is.
- Every team member uses multi-factor authentication for GitHub and for SignPath.
- A binary that turns out to be signed in breach of this policy is reported to SignPath, and
  the release that carried it is withdrawn.

These rules apply to the planned SignPath integration. The
[publication guide](publication.md) records the remaining certificate and renewal checks.

## Release evidence

The **Release from PR** workflow runs after a release request merges into protected
`releases`. It builds from the tested `main` SHA recorded in the request. The
[publication guide](publication.md) gives the steps for maintainers.

1. The build creates an attested inventory binding the unsigned package and
   installer hashes to the selected source SHA. GitHub attests the workflow run
   on `releases`; the inventory records the `main` SHA used to build the package.
2. The signing job checks the build evidence, signs the manifest and verifies
   the package against the installer trust anchor. The publisher checks the
   uploaded files against the verified files before publishing the release.
3. The published-asset attestation (`release-asset-receipt/v1`) records the hashes
   of the four downloaded files and the release and run identity. Its
   `sourceBuildVerified=false` and `authenticodeVerified=false` fields describe
   this observation step. Check the build inventory for source evidence and the
   manifest signature for package integrity.

Accept the release when all five jobs in its run succeed. Use the run identity
and expected file hashes when verifying its published assets. Use the installed
manifest and active executable version to confirm an update on a particular PC.
[The `0.2.6-rc.1` run](https://github.com/MrAliev/local-ai-toolkit/actions/runs/44485801031)
shows all stages.

The mechanisms follow the upstream [attestation action](https://github.com/actions/attest)
and [GitHub CLI verifier](https://cli.github.com/manual/gh_attestation_verify).

## Privacy

[Privacy](privacy.md) lists every connection Local AI makes to another computer and what
starts it.

## Reporting a problem

Security problems go to the [security policy](https://github.com/MrAliev/local-ai-toolkit/blob/main/SECURITY.md), not to a public issue.
