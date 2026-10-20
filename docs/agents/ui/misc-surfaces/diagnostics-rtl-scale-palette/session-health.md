# Session Health, accessibility, RTL, global UI scale and the command palette — Session Health dashboard

## Session Health dashboard (diagnostics)

- Read-only diagnostics window: `Views/Diagnostics/DiagnosticsWindow` +
  `ViewModels/Diagnostics/DiagnosticsViewModel` (transient — snapshots on open).
  Opened non-modally from Help → "Session Health…"
  (`HelpMenuViewModel.ShowDiagnosticsCommand` → `IDiagnosticsDialogService`,
  mirrors `IAboutDialogService`; the service builds a fresh VM through the injected
  `Func<DiagnosticsViewModel>` its registration in `App.EditingServices` hands it).
  It only READS existing diagnostics — never mutates engine state.
- Renderers publish their 1 Hz `FrameTimeSummary` into the static
  `Diagnostics/RenderDiagnostics` channel (`ChartRenderer` 2D +
  `Preview3DRenderDriver` 3D, one line each in their existing per-second HUD block);
  `Diagnostics/StartupDiagnostics` captures the same phase numbers `App` logs as
  `StartupPhases:` — as the formatted `PhaseSummary` (which the exported bundle
  embeds verbatim) **and** as the unformatted `StartupPhases` record the
  dashboard prints per phase. Both are read-only static channels (like
  `ChartTheme`).
- The "Export diagnostics bundle" button calls the pure
  `DotCharter.Services.Diagnostics.DiagnosticsBundleExporter` — logs + config +
  system-info, secret-redacted, credential files refused.
- **Since 0.51 it is CARDS on the dialog grammar, not six rectangles of
  monospace dump** (`Editor Dialogs.dc.html`, the `health()` surface): four cards
  each with a status pill, metrics as `TextBlock.FireStatLabel` over
  `SelectableTextBlock.Mono Value` in a two-column grid, a stacked startup bar, a
  severity-filtered log and a footer stating the ONE thing worth looking at. It
  carries `Views/Dialogs/DialogStyles.axaml` itself — the **fourth** sanctioned
  re-include, named in `DialogConstructionUiPerfSmokeTests`, whose file sweep now
  covers `Views/Diagnostics` too so a fifth cannot arrive unnoticed. Rules:
  - **Every value is a REAL channel and the thresholds are the only judgement.**
    They live as named constants on the pure `ViewModels/Diagnostics/
    SessionHealthRules` (`WatchOutputLatencyMs` 40, `WatchWorstFrameMs` 250,
    `WatchWorkingSetBytes` 2 GB) with `UI/SessionHealthTests` over them. Two
    things the mockup draws are deliberately NOT drawn: a memory GAUGE (there is
    no denominator — the app has no memory budget to be a fraction of) and an
    attribution for what caused a stall.
  - **Startup's tone is the ReadyToRun rule, not "the numbers are large".**
    `SessionHealthRules.Startup` is Good only when the probe CONFIRMED a
    precompiled image; `?` counts as not confirmed. A run that is not R2R
    JIT-compiles its whole startup path and measures roughly twice the shipped
    build, so the card prints `Diagnostics_StartupNotPrecompiled` WITH the
    numbers. Never surface a phase without it.
  - **The stacked bar's widths are assigned in code-behind**
    (`DiagnosticsWindow.ApplyStartupBar`), because `Grid.ColumnDefinitions` is a
    plain CLR property and not an `AvaloniaProperty` — a `{Binding}` onto it
    compiles and does nothing, leaving seven equal segments and every startup
    looking identical. The seven segment `Border`s stay in the markup and address
    their columns by INDEX, so the code assigns width-by-width rather than
    replacing the collection. Its last segment is the time the six named phases
    do not account for and is never dropped.
  - **A log row is parsed from the sink's own output template**
    (`DiagnosticsLogParser`, pure): a line that does not match the shape is a
    CONTINUATION (a stack trace) and folds into the entry above it — dropping
    them hides the one thing anyone opens the panel for, promoting them makes one
    exception look like forty events. An unknown level token reads as
    information, never as a row the filter swallows.

