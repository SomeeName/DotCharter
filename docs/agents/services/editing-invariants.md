# `Editing/` — the chart-model invariants (deep reference)

One file per subject under `editing-invariants/`. Read the one you need:

- [chord-templates.md](editing-invariants/chord-templates.md) — chord templates, shapes and arpeggios
- [undo.md](editing-invariants/undo.md) — undo: a gesture is one node
- [linked-measures.md](editing-invariants/linked-measures.md) — linked measures: link groups, propagation in `HistoryService`, the per-action link policy, the load check
- [anchors-and-capo.md](editing-invariants/anchors-and-capo.md) — a chord, its hand shape and its anchor as one decision; anchors (FHP); capo
- [validation-and-fixing.md](editing-invariants/validation-and-fixing.md) — `Editing/Validation/` + `Editing/Fixing/`, the inspector and auto-fix
- [select-alike-and-transposition.md](editing-invariants/select-alike-and-transposition.md) — select-alike, patterns and transposition
- [chart-spacing-enforcer.md](editing-invariants/chart-spacing-enforcer.md) — `ChartSpacingEnforcer`
- [facades.md](editing-invariants/facades.md) — the editing façades: Highway, Playback, Targets, Pen
- [structure-improvement-refret.md](editing-invariants/structure-improvement-refret.md) — `Editing/Structure/`, `Editing/Improvement/` (dynamic difficulty), `Editing/Refret/`
- [dlc-builder-fixes.md](editing-invariants/dlc-builder-fixes.md) — the chart's "Apply DLC Builder fixes" (one undo step; replaces what it touches) and `Editing/Preparation/`: the export findings' Fix
- [catalog-and-tempo.md](editing-invariants/catalog-and-tempo.md) — `Editing/Catalog/` (the phrase catalog) and `Editing/Tempo/` (the beat grid)
