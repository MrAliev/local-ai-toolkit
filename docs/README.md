# Local AI Toolkit

![Local AI logo](images/localai-mark.png)

**Local models that do the reading — for you, and for your cloud agent. A real saving in
cloud tokens, and in your own time.**

[Русская версия](README.ru.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/MrAliev/local-ai-toolkit/blob/main/LICENSE)

A cloud agent spends most of its tokens looking rather than thinking: opening candidate
files, searching the code, walking dependencies, scrolling a build log, describing a
screenshot, translating a page. Local AI does that part on your own machine and hands back
the answer, so the agent starts from a result instead of paying to find one.

**A cloud agent that answers a question by reading files pays for every file it opens. A
question answered from the local index brings back only the fragments that matched.**

A subscription to any cloud agent is optional: the same tools answer from the command line,
if you would rather work on your own.

---

## Contents

- [Two ways to use it](two-ways-to-use-it.md)
- [What a cloud agent gets](what-a-cloud-agent-gets.md)
- [What the command line gets](what-the-command-line-gets.md)
- [Code search is not text search](code-search-is-not-text-search.md)
- [With a cloud agent: what stops costing tokens](with-a-cloud-agent-what-stops-costing-tokens.md)
  - [It works by default, and you can see it](with-a-cloud-agent-what-stops-costing-tokens.md#it-works-by-default-and-you-can-see-it)
  - [If something does not work the way you want](with-a-cloud-agent-what-stops-costing-tokens.md#if-something-does-not-work-the-way-you-want)
- [From the command line: not tokens, but time](from-the-command-line-not-tokens-but-time.md)
- [How it works](how-it-works.md)
  - [The pieces](how-it-works.md#the-pieces)
  - [One queue in front of every model](how-it-works.md#one-queue-in-front-of-every-model)
  - [The code index](how-it-works.md#the-code-index)
  - [A search, step by step](how-it-works.md#a-search-step-by-step)
  - [Which model does what](how-it-works.md#which-model-does-what)
  - [How the saving is counted](how-it-works.md#how-the-saving-is-counted)
  - [What the installer does](how-it-works.md#what-the-installer-does)
- [Dependencies](dependencies.md)
- [Installing](installing.md)
  - [What it will compute on](installing.md#what-it-will-compute-on)
- [Using it with a cloud agent](using-it-with-a-cloud-agent.md)
- [Using it from the command line](using-it-from-the-command-line.md)
  - [Step 1. How to run anything](using-it-from-the-command-line.md#step-1-how-to-run-anything)
  - [Step 2. Where to run it from](using-it-from-the-command-line.md#step-2-where-to-run-it-from)
  - [Step 3. Connect a repository](using-it-from-the-command-line.md#step-3-connect-a-repository)
  - [Step 4. Use it](using-it-from-the-command-line.md#step-4-use-it)
- [What language it answers in](what-language-it-answers-in.md)
- [Updating and removing](updating-and-removing.md)
  - [Update](updating-and-removing.md#update)
  - [Clear the index, keep the product](updating-and-removing.md#clear-the-index-keep-the-product)
  - [Remove it entirely](updating-and-removing.md#remove-it-entirely)
- [Impact evidence: C# pilot](impact-evidence-c-pilot.md)
- [Documentation packages](documentation-packages.md)
- [Building and testing](building-and-testing.md)
  - [Independent quality corpora](building-and-testing.md#independent-quality-corpora)
- [Licence](licence.md)

<!-- Generated from the README by DocumentationSiteTests. -->
