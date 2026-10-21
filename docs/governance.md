# Project governance

[Русская версия](governance.ru.md)

[Anton Aliev (@MrAliev)](https://github.com/MrAliev) maintains Local AI Toolkit and is the
release approver. The [MIT license](https://github.com/MrAliev/local-ai-toolkit/blob/main/LICENSE) governs use and redistribution; this page
describes how the maintained project makes decisions.

## Changes and decisions

Propose a reproducible problem in an issue. Keep implementation changes in focused pull
requests with tests and paired English/Russian documentation where behavior changes.
The maintainer decides scope, compatibility and release readiness. CI is required for the
current PR head; a green build is evidence, not a substitute for security review.

Architectural proposals should explain the user problem, alternatives, migration and
validation. Existing behavior is not a permanent compatibility promise; breaking changes
must be identified in release notes with a migration path.

## Releases and signing

Release approval and code-signing approval are separate from merging a PR. Each future
SignPath signing request needs the human approval described in the
[code signing policy](code-signing-policy.md). Credentials and signing material never go
in issues, PRs, Wiki or CI logs.

## Community

Follow the [code of conduct](https://github.com/MrAliev/local-ai-toolkit/blob/main/CODE_OF_CONDUCT.md). Volunteers can report bugs, improve
documentation, review changes or propose code. There is no response-time SLA. Security
reports use [private vulnerability reporting](https://github.com/MrAliev/local-ai-toolkit/blob/main/SECURITY.md), not public issues.
