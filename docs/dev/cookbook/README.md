# Cookbook: how do I...

Short recipes for the changes people make most. Each one lists the files to touch,
in order, the test that proves the change, and where in the current code the
pattern already exists, so you can read a real example.

Paths are relative to the repository root. Line numbers drift, so each recipe
names files and types, not lines. If a recipe and the code disagree, trust the
code and fix the recipe in the same pull request.

| I want to... | Recipe |
|---|---|
| add a menu command | [add-a-menu-command.md](add-a-menu-command.md) |
| add a rule to the Issues tab | [add-an-issues-tab-rule.md](add-an-issues-tab-rule.md) |
| add a note technique, end to end | [add-a-note-technique.md](add-a-note-technique.md) |
| add a field to an export format | [add-an-export-format-field.md](add-an-export-format-field.md) |
| add a setting | [add-a-setting.md](add-a-setting.md) |
| add a pedal or amp model to the rig | [add-a-pedal-or-amp.md](add-a-pedal-or-amp.md) |
| add a localized string | [add-a-localized-string.md](add-a-localized-string.md) |
| add a new language | [add-a-language.md](add-a-language.md) |
| add a view with its smoke test | [add-a-view.md](add-a-view.md) |
| add a native C ABI call | [add-a-native-abi-call.md](add-a-native-abi-call.md) |

## Rules that apply to every recipe

- Open an issue first and branch from the current `main` (see
  [CONTRIBUTING.md](../../../CONTRIBUTING.md)).
- Run only the tests you need while you work, then the full gate before you push:

  ```powershell
  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass '*MyFeature*'
  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate
  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless   # after any view change
  ```

- Every user-visible string goes into all three resx files. See
  [add-a-localized-string.md](add-a-localized-string.md).
- Never read the three resx files whole; they are about 600 KB each. Search for
  the key instead.
- Prove your test can fail: break the code on purpose, watch the test go red,
  then restore it. Say so in the pull request.
- The architecture behind these steps is in [docs/ARCHITECTURE.md](../../ARCHITECTURE.md);
  the symbol-to-file index is [docs/agents/MAP.md](../../agents/MAP.md).
