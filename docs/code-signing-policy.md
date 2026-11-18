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

## Release evidence

Two workflows make different, limited claims. Neither uses a production signing key,
publishes a release, installs a candidate, or closes [issue 210](https://github.com/MrAliev/local-ai-toolkit/issues/210).

1. **`Build unsigned candidate`** builds the seven own Windows executables from a clean
   checkout of the selected `main` commit. Its `localai.unsigned-candidate.v2` manifest
   binds source commit/tree, workflow/run/attempt, version, runtime identifier, and every
   file's size and SHA-256. The build job attests that manifest with SLSA build provenance.
   A separate job downloads the exact artifact ID, checks the complete inventory and
   every hash, and verifies the attestation against the expected repository, source SHA,
   signer workflow/SHA, branch, predicate type, and GitHub-hosted runner. Wrong-source
   and wrong-signer controls must fail. Accept the candidate only when **both jobs in
   that exact run succeed**, and preserve its source, run/attempt, artifact ID/digest,
   manifest and verification evidence. An upload by itself is not acceptance.
2. **`Attest release assets`** downloads the four supported assets of an existing
   published release by numeric release/asset IDs. It checks names, sizes and available
   API digests, then reads release metadata again to detect replacement during download.
   Its custom `release-asset-receipt/v1` attestation records the observed hashes and
   release/run identity. It explicitly records `sourceBuildVerified=false` and
   `authenticodeVerified=false`: the workflow downloaded these bytes; it did not compile
   them. Later verification against the expected receipt signer can detect different
   bytes, but the receipt does not authenticate the original publisher or eliminate
   substitution before observation.

Before the next release, confirm that both workflows are **active** in GitHub, and that
the exact-source candidate has passed its verification job. A disabled workflow, absent
run, failed or pending verification blocks this release acceptance step. Run the receipt
workflow after publication and preserve its successful verification for all four assets.
For an older release, dispatch it on `main` with the numeric release ID; do not describe
that later receipt as a historical build attestation. Existing releases are not
retroactively claimed to satisfy the candidate gate.

To verify a candidate downloaded from its recorded artifact, use the verifier from the
same reviewed source commit, with a trusted GitHub CLI installed and authenticated:

```powershell
./scripts/verify-localai-unsigned-candidate.ps1 -ArtifactRoot <downloaded-folder> `
  -ExpectedSource <commit> -ExpectedVersion <version> -ExpectedRunId <run-id> `
  -ExpectedRunAttempt <attempt> -EvidencePath <outside-artifact>/verified-candidate.json
```

Expected values must come from the reviewed run, not from the downloaded manifest.
These checks establish evidence about candidate files or observed release assets.
They do not establish which version is installed or running on a particular machine.
Production signing, key custody, the ECDSA package manifest, and installation/runtime
validation remain separate acceptance requirements. Issue 210 stays open.

The mechanisms follow the upstream [attestation action](https://github.com/actions/attest)
and [GitHub CLI verifier](https://cli.github.com/manual/gh_attestation_verify).

## Privacy

[Privacy](privacy.md) lists every connection Local AI makes to another computer and what
starts it.

## Reporting a problem

Security problems go to the [security policy](https://github.com/MrAliev/local-ai-toolkit/blob/main/SECURITY.md), not to a public issue.
