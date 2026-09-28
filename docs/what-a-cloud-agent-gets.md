# What a cloud agent gets

[Contents](README.md#contents) · [Русская версия](what-a-cloud-agent-gets.ru.md)

Twenty-three tools over MCP: twelve from `codesearch` and eleven from `locallm`. The agent
calls them itself, whenever a question is cheaper to ask locally.

| Tool | What it does |
| --- | --- |
| `search_code` | search by meaning and by exact name at once |
| `get_code_chunk` | pull a match in full |
| `go_to_definition` | where the symbol under the cursor is declared |
| `find_references` | who calls it and where it is used |
| `find_implementations` | who implements or overrides it |
| `find_relationships` | the relationship graph: implementation, override, type |
| `analyze_impact` | direct C# impact evidence with source coordinates, hashes and snapshot |
| `lsp_open_document` | hand navigation the text of an unsaved file |
| `lsp_close_document` | give navigation back to the indexed snapshot |
| `ask_local` | mechanical work over files you name |
| `triage_log` | make sense of machine output of any length |
| `read_image` | read images from disk or one PDF page by page |
| `index_documents` / `search_documents` | index scanned PDFs and find OCR phrases with file versions and page references |
| `translate_local` | translate, structure intact |
| `index_status` | whether an index exists, what built it, whether the mainline branch has moved since |
| `index_refresh` | refresh the index after a commit |
| `index_unload` | free the memory a loaded index holds |
| `local_models_status` | what is installed, what is resident, what is missing |
| `local_model_preflight` | prove where a model loads: entirely in video memory under the default policy |
| `local_models_sync` | fetch the recommended models that are missing |
| `local_model_experiment_report` | statistics for one task-and-model pair |
| `local_model_feedback` | decide a model's fate: promote, keep as fallback, disable |

---

← [Two ways to use it](two-ways-to-use-it.md) · [Contents](README.md#contents) · [What the command line gets](what-the-command-line-gets.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
