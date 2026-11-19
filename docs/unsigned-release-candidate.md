# Unsigned release candidate

[Русская версия](unsigned-release-candidate.ru.md)

This page explains artifacts from the retired **Build unsigned candidate** GitHub
Actions workflow. For current builds, follow the
[release pull request instructions](publication.md).

An archived unsigned candidate is evidence of a build for review. Install updates
from GitHub Releases with a signed manifest.

## Inspect a historical candidate

The workflow accepted a manual dispatch on `refs/heads/main` in
`MrAliev/local-ai-toolkit`. It checked the repository, event, ref and immutable
`github.sha` selected at dispatch before compiling. The version had three numeric
parts, each no greater than 65534, with an optional SemVer prerelease.

All compilation runs on a GitHub-hosted `windows-2025` runner with the SDK from
`global.json`. The workflow has only `contents: read`, disables checkout credential
persistence, and pins its three actions to commit IDs. It has no signing credentials,
release publication permissions, caller-supplied commands, or post-build callbacks.
The script refuses to overwrite an existing candidate output directory.

A successful run uploads one artifact, named using the source SHA, run ID and run
attempt. The run summary records its GitHub artifact ID and digest. Artifacts expire
after seven days. A failed build or validation cannot upload a partial candidate.
The archive contains only these own binaries under `own-pe/`:

- `codesearch.exe`
- `codesearch-mcp.exe`
- `locallm-mcp.exe`
- `localai.exe`
- `localai-launcher.exe`
- `LocalAi.Broker.exe`
- `LocalAi.Installer.exe`

Each binary must have Authenticode status `NotSigned`. Loose upstream libraries and
unexpected extra executables remain outside the uploaded artifact. The eighth file,
`unsigned-candidate.json`, records `signed: false`, the version, source SHA, workflow
run and each binary's length and SHA-256. These checksums describe the bytes; the
metadata is not a signature or independently verified build provenance. Inspect the
GitHub run and its source commit as well as the downloaded files.

## What must happen before signing and publishing

SignPath's GitHub integration checks build origin and the uploaded GitHub artifact;
for its OSS service, every job before the signing request must use a GitHub-hosted
runner. Submission must later be added to the trusted workflow using the actual
upload step's artifact ID. Downloading files and uploading them through an unrelated
workflow is not a substitute for that integration. The GitHub upload is a ZIP archive,
so the future SignPath artifact configuration must match that archive and explicitly
select the seven own PE paths. Do not sign upstream DLLs with the project's identity.
[SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[SignPath Foundation terms](https://signpath.org/terms).

The remaining release work is deliberately gated:

1. Obtain acceptance for the OSS project, configure actual project and policy IDs,
   and agree the certificate identity and required binary metadata with SignPath.
   Configure origin constraints and manual signing approval; the Foundation requires
   approval for every release. Do not accept terms or invent an issued certificate
   on a maintainer's behalf. [SignPath Foundation terms](https://signpath.org/terms).
2. Extend the reviewed workflow with the real signing integration, retaining the
   complete runtime payload from the same build separately. Keep upstream files
   unchanged and outside the signing selection. After approval, verify the returned
   signatures on all seven own PE files and replace only those files in that payload.
3. Configure the installer and verifier with the actual approved certificate subject
   and public-key pin. Exercise first install, update, tamper rejection and certificate
   compatibility; a nonexistent identity cannot pass this step.
4. Package the payload after Authenticode signing. Then generate its final hashes and
   ECDSA-signed release manifest using the separately protected release key. Do not put
   that private key into this candidate workflow. Publish only after package and
   installer verification succeed. Repacking or modifying PE files afterward invalidates
   earlier hashes or signatures.

The existing published-asset attestation workflow records downloaded release assets;
it does not establish that those assets came from this candidate build. Neither that
workflow nor this unsigned artifact closes the outstanding Authenticode requirement.
