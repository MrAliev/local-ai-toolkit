# Two ways to use it

[Contents](README.md#contents) · [Русская версия](two-ways-to-use-it.ru.md)

**As subagents to a cloud agent.** Claude Code, Codex and any other MCP client can call
Local AI's tools directly. The agent hands them the simple, repetitive work and starts from
prepared results, instead of doing that drudgery itself on cloud tokens.

**Without a cloud agent, from the command line.** The same capabilities, but as helpers for
you: search code by meaning, triage a log, read an image, translate a document. It is the
same index and the same models either way, so nothing is lost by starting here and adding an
agent later.

![How Local AI fits together: you reach its tools through a cloud agent over MCP or straight from the command line, and the tools use a broker for local models and a per-repository code index](images/how-it-works.svg)

**Why there is a broker in the middle.** There is one per runtime directory —
`%LOCALAPPDATA%\LocalAi`, so one per Windows user — and it holds the queue.
That is how Claude, Codex and the command line all work at once without tripping over each
other, and why one model sits in video memory rather than four fighting over it.

**Nothing leaves the machine.** The broker sends your code and the prompts built from it to
Ollama at `http://127.0.0.1:11434/`, on this machine. Which network interfaces Ollama itself
listens on is Ollama's own setting. Left alone, the runtime makes no network calls at
all: the update check is off by default. Models and missing prerequisites are downloaded
when you ask for them.

---

[Contents](README.md#contents) · [What a cloud agent gets](what-a-cloud-agent-gets.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
