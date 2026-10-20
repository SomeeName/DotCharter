---
paths:
  - "DotCharter.UI/**"
  - "DotCharter.UI.Gallery/**"
  - "DotCharter.Localization/**"
---

# Avalonia UI

The hard rules and routing table are `DotCharter.UI/AGENTS.md` (the gallery's
are `DotCharter.UI.Gallery/AGENTS.md`); they win over anything generic.

- Avalonia API, XAML, styles, bindings, templates, compiled bindings,
  `DynamicResource` behaviour: ask the `avalonia-docs` MCP server
  (`search_avalonia_docs` first, `get_avalonia_expert_rules`;
  `lookup_avalonia_api` returned "No results" even for `Button` on
  2026-09-19, so fall back to search) before writing from memory. It knows
  Avalonia 12; memory mostly knows 11.
- Finding a view's code-behind, view model, command or binding source: `LSP`
  (csharp-ls). Finding an `x:Name`, a style class or a resx key: Grep, then Read
  ±150 lines — never Read a large `.axaml` or resx whole.
- Visual design of a new or reshaped screen: `frontend-design` principles,
  built gallery-first on fake view models, using the shared tokens in
  `DotCharter.UI/Styles/ColorTokens.axaml`.
- A new or edited view needs the construction + attach + layout smoke test
  (`DotCharter.Tests/Perf/*ConstructionUiPerfSmokeTests.cs`) and a run of the
  headless-UI suite (`tools/agent/Invoke-Gate.ps1`).
- Strings: all three resx files (EN, de-DE, ja-JP), same key, same change.
