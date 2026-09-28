# Security policy

[Русская версия](SECURITY.ru.md)

## Reporting a vulnerability

Please do not open a public issue. Report it privately instead: on the repository's
**Security** tab, choose **Report a vulnerability**.

A useful report says:

- which LocalAi version you ran — **Apps & features** shows it beside **Local AI Toolkit**;
- what you did, what happened, and what you expected;
- what an attacker gains, and what they need first.

You will get an answer on the report. A fix goes into the next release, and the report is
published once that release is out, with credit if you want it.

## What is supported

The newest release. Older releases do not get fixes: update instead.

## What is in scope

- the installer and the uninstaller, including how they verify a release;
- the programs LocalAi installs: the launcher, the command lines, the MCP servers and the
  broker;
- anything that sends data off the computer contrary to [Privacy](docs/privacy.md).

Ollama, the models, and the packages `winget` and `npm` install belong to their own projects;
report problems in them there.

## How releases are protected

See [What the installer does](docs/how-it-works.md#what-the-installer-does) and the
[code signing policy](docs/code-signing-policy.md).
