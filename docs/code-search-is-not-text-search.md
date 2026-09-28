# Code search is not text search

[Contents](README.md#contents) · [Русская версия](code-search-is-not-text-search.ru.md)

A file is not sliced into equal slabs of text. It is **taken apart by declaration** — class,
method, function — and each of those gets its own chunk, with its name, its signature and its
exact lines. A declaration too long for one chunk — 6,000 characters with its header — is cut
into overlapping parts numbered like `[1/3]`. That is why a hit reads `OrderCloser.CloseAsync`,
or one numbered part of it, rather than a line that happened to match in the middle of
somebody else's method.

- **C#** is parsed by **Roslyn**, the same compiler that builds the project. It walks a real
  syntax tree. A type gets one chunk: its declaration and a line for each member — fields as
  written, everything else by its signature. Each method, constructor, destructor, operator,
  conversion operator and indexer gets a chunk of its own, and so does a property with real
  logic in it: an accessor with a body, or an expression body longer than 80 characters. A
  file that declares no type becomes one whole-file chunk.
- **TypeScript and Python** are mapped by the **SCIP** indexers, `scip-typescript` and
  `scip-python`. Where an indexer reports a declaration's extent, the chunk follows it; where
  it reports only the name, the chunk runs from that declaration to the line before the next
  one. Nothing is guessed from indentation.
- **Everything else** — markup, configuration, documentation, any language with no indexer —
  cannot be taken apart that way, so it gets an honest sliding window: 60 lines with 12 lines
  of overlap, so a thought is not torn in half at a seam.

One thing the index cannot see: a file you have edited but not saved. Navigation can still
answer for it, but only when an agent hands over the buffer's text — there is no such path
from the command line, and language servers stay off until you enable them.

---

← [What the command line gets](what-the-command-line-gets.md) · [Contents](README.md#contents) · [With a cloud agent: what stops costing tokens](with-a-cloud-agent-what-stops-costing-tokens.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
