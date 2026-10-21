# Open-source disclosure review · 2026-10-21

[Русская версия](2026-10-21-publication.ru.md)

## Scope and result

No confirmed credential finding remained in the inspected snapshot. This is a bounded
disclosure review, not a guarantee that arbitrary content is safe to publish.

| Surface | Inspected |
| --- | --- |
| Git history across available refs | 901 commits with diffs processed by the scanner |
| Actions | 764 available log archives; 2 retained artifacts |
| Issues and PRs | 867 bodies; 140 comments; 629 review lists (no review bodies returned) |
| Releases | 3 releases; 12 assets, 1,216,705,494 bytes |
| Release asset metadata | 12/12 sizes and GitHub SHA-256 digests matched downloaded bytes |
| Mentioned GitHub attachments | No matching attachment URLs in the collected bodies |

## Method and dispositions

The scanner was tested against a synthetic GitHub-token positive control before use.
Gitleaks 8.30.1 failed that control; its negative result was rejected. Official Gitleaks
8.18.4, verified against the upstream download checksum, detected it and was used with
redaction enabled. This older ruleset detects known patterns; it is not comprehensive.

Two Git findings in historical performance evidence were 64-character SHA-256 checksums
for `LocalAi.Broker.Client.dll`, not authentication credentials. Classification was checked
against the exact reported historical revision, without publishing values.

The expanded non-Git scan reported 51 generic-key matches. All mapped to public
`performed_via_github_app.client_id` fields for the Codex Connector and Dependabot apps.
Their identifiers were checked for exact equality with the unauthenticated GitHub Apps API.
No secret values, raw logs, application contacts or private audit files are published here.

Archives were bounded during extraction, with traversal/symlink/duplicate protections.
No downloads or extracted files were executed. No remote history, logs or artifacts were
deleted as part of the review.

## Limits and follow-up

Text-pattern scanning does not prove that every binary payload was decoded or is free of
secrets/PII. Hidden edit/deletion history, unmentioned attachments and data created after
the snapshot are outside this claim. Future CI logs and releases require their own checks.
Raw local evidence is retained separately with restricted ACLs.

Signing remains [#210](https://github.com/MrAliev/local-ai-toolkit/issues/210).
Publication controls and deployment are tracked separately in
[#872](https://github.com/MrAliev/local-ai-toolkit/issues/872).
