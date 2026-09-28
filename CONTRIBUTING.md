# Contributing

[Русская версия](CONTRIBUTING.ru.md)

Thank you for helping. This page is the short version of how work gets into `main`.

## Building and testing

You need Windows x64 and the .NET SDK that `global.json` names. Building and testing never
start Ollama, so no GPU is needed.

```powershell
dotnet build LocalAi.slnx --configuration Release
dotnet test LocalAi.slnx --configuration Release --max-parallel-test-modules 1 --timeout 20m
```

Run the whole suite before you open a pull request, and put its counts in the description.
[Building and testing](docs/building-and-testing.md) has the rest.

## One subject per pull request

- Branch from `main`, one subject per branch. Name the branch by the kind of work, then say
  what it is: `fix/`, `feature/`, `test/`, `docs/`, `refactor/` or `chore/` — for example
  `fix/version-line-holds`.
- Write the failing test first, and see it fail for the reason you expect. Then make it pass.
- A test never asserts how fast the machine is: wait for the condition, not for a duration.
- Commit messages, pull requests and issues are written in English.

## Text the product prints

- Every string the software prints follows the reader's Windows language: English by default,
  Russian on a Russian system. The strings live in `.resx` pairs under `Resources/` in the
  assembly that prints them, and a test refuses a language that has only some of the keys.
- The installer is the exception: both languages sit side by side in one
  `InstallerCulture.Pick(english, russian)` call.
- Never translated: commands, option names, identifiers, status tokens, paths, and the MCP
  `[Description]` attributes. Numbers, durations and dates stay invariant.

## Documentation

- `README.md` and `README.ru.md` are the documentation. Change both together: a sentence that
  stopped being true is a defect.
- The pages in `docs/` that end with a *Generated from the README* comment are built from the
  READMEs. Do not edit them; edit the README, then rebuild them:

  ```powershell
  $env:LOCALAI_WRITE_DOCUMENTATION_SITE = '1'
  dotnet test tests/LocalAi.Installer.Core.Tests/LocalAi.Installer.Core.Tests.csproj --configuration Release
  ```

- Every other document has its translation beside it, `name.md` and `name.ru.md`, and each
  links to the other. A test checks the pairs, the links, and that no link leads nowhere.

## Licence

By contributing you agree that your work is published under the [MIT License](LICENSE).
