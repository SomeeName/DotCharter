# DotCharter documentation

Start here if you are a person. The repository holds two kinds of documents.

## For users

| Read | For |
|---|---|
| [guide/](guide/README.md) | How to use DotCharter: charting and playing a song, the tone rig and every setting, with annotated screenshots |

## For contributors (start here)

| Read | For |
|---|---|
| [../README.md](../README.md) | What DotCharter is, install, requirements |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Building, the two test runs, what a pull request needs |
| [dev/GETTING-STARTED.md](dev/GETTING-STARTED.md) | Clone to running app and tests, with troubleshooting |
| [ARCHITECTURE.md](ARCHITECTURE.md) | The layers and the two main flows (play a song, save a chart) |
| [adr/](adr/README.md) | Why the big decisions were made: audio engine, rendering, UI layers, formats, licence |
| [dev/cookbook/](dev/cookbook/README.md) | Step-by-step recipes: add a view, a setting, a language, a menu command, a native call |
| [dev/DEBUGGING.md](dev/DEBUGGING.md) | Logs, crash reports, attaching a debugger, what a bug report needs |
| [RSCP-FORMAT-GUARANTEES.md](RSCP-FORMAT-GUARANTEES.md) | What the `.rscp` project format promises |
| [../SECURITY.md](../SECURITY.md), [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) | Reporting vulnerabilities; how we work together |
| [../PRIVACY.md](../PRIVACY.md), [../LEGAL.md](../LEGAL.md) | What the app sends over the network; rights holders and your responsibilities |
| [../ACKNOWLEDGEMENTS.md](../ACKNOWLEDGEMENTS.md), [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | Whose work this builds on, and every licence |

## Reference for AI coding assistants

[../AGENTS.md](../AGENTS.md), the `AGENTS.md` in each project folder, and
everything under [agents/](agents/MAP.md) are the detailed rulebook the
repository's AI coding assistants load automatically (`CLAUDE.md` files only
import them). They hold standing rules, the symbol-to-file index
([agents/MAP.md](agents/MAP.md)), per-area invariants and tool-specific
workflow. They are current and readable by anyone, and they are the place to
look up an exact rule or the file behind a symbol, but you do not need them to
get started. If one disagrees with the code, the code wins; please fix the
guide.

The `.claude/` folder (and `tools/agent/`, the wrapper scripts the assistants
run) configures those assistants and is not documentation.
