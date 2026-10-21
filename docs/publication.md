# Publication and signing checklist

[Русская версия](publication.ru.md)

This is the maintainer's runbook for opening the repository and publishing documentation.
An unchecked step is not evidence of completion. The existing signing follow-up is
[#210](https://github.com/MrAliev/local-ai-toolkit/issues/210).

## Before changing repository visibility

1. Audit the actual commit and every remote branch/tag that will be exposed. Scan secrets
   with a redacting scanner that first detects a synthetic positive control. Inspect findings
   locally; record rule, path, revision and disposition, never the secret value.
2. Audit disclosure surfaces outside Git: retained Actions logs/artifacts, issue and PR
   bodies/comments/reviews, release notes/assets and uploaded attachments. Record unavailable
   or uninspected surfaces; a scan without findings is not a guarantee that no secret exists.
3. Confirm ownership and MIT licensing of project code. Retain third-party copyright and
   license notices. Model weights and separately downloaded prerequisites have their own
   licenses; they are not relicensed as MIT by this repository.
4. Verify paired documentation, full tests and CI on the exact publication commit. Check
   that privacy statements describe actual connections and that signing is not overstated.
5. Resolve findings before making the existing history, logs and discussions public.
   A current-file fix does not remove a credential from history. Rotate an exposed
   credential before considering history cleanup; do not silently rewrite shared history.

## Documentation publication

The portal is `docs/index.html`; the reference is generated from the paired READMEs.
The **Documentation** workflow builds the Jekyll site on PRs. Its deployment job only
runs from public `main`; private or PR builds must not publish it.

After the disclosure review, enable GitHub Pages with **GitHub Actions** as the source,
enable the Wiki, and publish the versioned Wiki sources with
`scripts/publish-wiki.ps1`. The Wiki Git repository must first be initialized by creating
its Home page on GitHub. Keep editing restricted to repository collaborators.

```powershell
./scripts/publish-wiki.ps1 -PreviewDirectory C:\work\localai-wiki-preview
# Review the generated pages before publishing the exact reviewed commit:
./scripts/publish-wiki.ps1 -Publish
```

Update the repository homepage to `https://mraliev.github.io/local-ai-toolkit/` after the
deployment succeeds. Test both languages, mobile layout, navigation, diagrams, calculator
edge cases and keyboard access. Enable private vulnerability reporting before the public
security policy directs users to it; enable GitHub secret scanning/push protection and
dependency alerts where available. Confirm actual settings through the API/UI.

## Free Authenticode through SignPath Foundation

The [official application](https://signpath.org/apply.html) requires a public, maintained,
released OSS project and review under the [current conditions](https://signpath.org/terms).
Acceptance is not guaranteed. The certificate belongs to SignPath Foundation and its key
is held by the service; it is not a downloadable private key issued to this maintainer.

Prepare these fields from verified public information:

| Field | Project value |
| --- | --- |
| Project | Local AI Toolkit |
| Repository | https://github.com/MrAliev/local-ai-toolkit |
| Homepage | https://mraliev.github.io/local-ai-toolkit/ after deployment |
| Downloads | https://github.com/MrAliev/local-ai-toolkit/releases |
| Privacy | the published Privacy page |
| Category | Developer tools, if offered by the form |
| Maintainer | Individual project, maintained by @MrAliev |
| Build system | GitHub Actions; distinguish CI candidates from earlier local release builds |
| Reputation | Link real release history, CI and reviewed changes; do not invent users or adoption |
| Contact | The owner's confirmed name and contact email, supplied privately |

The owner must confirm the application's personal-data processing and binding terms.
Do not infer MFA enrollment, account approval, a certificate subject or a key fingerprint.
Keep application correspondence and API tokens outside this repository.

## After SignPath approval

1. Configure trusted GitHub builds, an artifact configuration limited to project-owned PE
   files, product/version metadata restrictions, and a release approver with MFA.
2. Pin reviewed actions; use GitHub-hosted runners throughout the trusted build chain.
   Never expose signing credentials to untrusted PR code. A build candidate is not a release.
3. Build and upload the artifact in that workflow; request signing by that artifact ID.
   Every signing request requires manual approval. Merging green CI does not approve signing.
4. Verify each returned signature and timestamp against the actual certificate. Replace
   the placeholder subject and SPKI consistently in every verifier only after these are known.
5. Sign project-owned inner binaries **before** creating the final package. Do not stamp
   upstream DLLs with this project's certificate. Hash the final ZIP, sign its release manifest,
   then verify the complete distribution. No package changes after hashing/signing.
6. Test old installed client → new signed release, publisher mismatch, modified payload,
   revocation/timestamp behavior and certificate renewal. A single-SPKI policy requires an
   explicit rotation plan; a second signature alone does not establish compatibility.
7. Enable `RequiresAuthenticode` only with the real policy and passing integration tests.
   Publish signer identity, verification instructions and independent digest/provenance
   evidence. Close #210 only when the actual release and key-custody evidence support it.

The existing release-asset attestation records uploaded bytes. It must not be described as
CI build provenance until a build-to-source attestation is produced by the build itself.

[Unsigned release candidate](unsigned-release-candidate.md) describes the CI groundwork
and its limits. Its output is not installable or signed release distribution.
