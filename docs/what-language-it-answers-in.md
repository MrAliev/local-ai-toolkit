# What language it answers in

[Contents](README.md#contents) · [Русская версия](what-language-it-answers-in.ru.md)

There are **two: English and Russian**. English is not one of two equals but the neutral one:
a locale with no translation is answered in English rather than refused.

**By default it follows the system.** A `LOCALAI_LANGUAGE` set for the process is used instead
of the saved language; when it is not set, the saved language is used. If the language in
effect is not one of the two, the operating system's language is used, then English — an
unsupported `LOCALAI_LANGUAGE` does not fall back to the saved one.
Regional names resolve to their language — `ru-RU`, `ru-KZ` and `ru` all read the same strings.

**How to change it.** Three ways, for three situations:

```powershell
lai localai policy set --language ru        # remembered: ru, en or system
$env:LOCALAI_LANGUAGE = "ru"                # for one session, over the remembered choice
lai localai ask "…" file.cs --language en   # one call, over both
```

The same choice decides what the local model answers in: a Russian machine gets a Russian triage
of an English log, with paths, line numbers, error codes and quoted lines untouched.

The saved language applies to new CLI and MCP processes. `LOCALAI_LANGUAGE` overrides it for
an individual process. Restart running MCP servers after changing the saved language.
CodeSearch help follows this choice; its other command-line prose currently remains in English.

The installer offers the choice on its first screen, lets you switch on any screen after that,
and remembers it — removal opens in that same language, even months later.

**What is never translated.** Commands, option names, identifiers, enum values, paths, wire
formats, numbers and dates: the same in every language, because they are quoted verbatim and
parsed by programs. For the same reason `--json` **answers in English whatever the
machine speaks** — output meant for a program must not depend on whose machine produced it. Only
`--language` on the same command line changes that, because then the language came from the caller
rather than from the machine.

---

← [Using it from the command line](using-it-from-the-command-line.md) · [Contents](README.md#contents) · [Updating and removing](updating-and-removing.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
