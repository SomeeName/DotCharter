# Shell chrome — menu bar, toolbar, status rail, popovers (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before touching
`Views/Shell/`, the menu bar, the transport bar, the status rail, any popover
that hangs off those two rails, or the activity rail.**

Related: the workspace *between* those rails (the timeline gutter, the ribbon,
the minimap strip, the dock chrome) is [editor-workspace.md](../editor-workspace.md);
the Fire layer these grammars are built on is
[fire-design-system.md](../fire-design-system.md); the prewarm mechanics that make
a dropdown open fast are in
[rendering-perf/startup-and-prewarm.md](../rendering-perf/startup-and-prewarm.md).

## The title bar is CUSTOM, so the drag region is window chrome — nothing may cover it

`MainWindow.axaml` asks for `ExtendClientAreaToDecorationsHint="True"` and
`WindowDecorations="BorderOnly"`: **Windows draws no caption.** The only thing
that moves the window is a left press on the transparent
`Border x:Name="TitleBarDragArea"` in `TitleBarRow`, which calls `BeginMoveDrag`.
Double-tap on the same strip toggles maximize.

**The three rows that take the title bar's row are one view,
`Views/Shell/WindowChromeView`** (#654): `TitleBarRow` (the editor's menu bar,
"Go to anything" and the window buttons), `BackBar` (a player's full-bleed
Library or Settings) and `WindowCaption` (Home and Play Mode). Each carries its
own drag strip (`TitleBarDragArea`, `BackBarDragArea`, `WindowCaptionDragArea`)
and the three caption buttons; the handlers are the view's code-behind, acting
on its `TopLevel`. `MainWindow` hosts it as `WindowChrome` in `TitleContentGrid`
row 0 and calls `WindowChrome.ShowWindowState` on every `WindowState` change,
which swaps the three maximize glyphs. It is a plain `Panel` in that cell with no
`Background`, so it is as tall as the bound row and takes no pointer itself. Its
names are in ITS namescope: reach them as `window.WindowChrome.TitleBarRow` (the
prewarm does), never `window.FindControl("TitleBarRow")`, which now returns null.
**F11 / Alt+Enter (`ToggleFullScreen`) keeps `TitleBarRow` visible.** It
collapsed the row to 0 until 2026-09-08 "so the game owns the whole screen" —
but that row IS the menu bar and the CHART / LIBRARY / SETTINGS / Play Mode
switch, so fullscreen in the chart editor left no File menu and no way to change
mode ("in f11 that bar on top is gone which is an issue").

**A full-window overlay that layers over that strip takes the whole gesture
away, and nothing about the overlay looks wrong.** v0.48.5 shipped it: the chart
editor's Amp & FX rig overlay (`Panel x:Name="PedalboardOverlay"`) was a child of
`WindowRootGrid` at `Grid.Row="0" Grid.RowSpan="2"` — which spans the title-bar
row — and the rig's own root paints the fully opaque `FireOverlayFill`. Opening
the rig put an opaque, hit-testable surface on top of the drag strip and the
window stopped being movable for as long as the rig was up. The owner: *"in
pedalboard view you can't move the window around in Windows anymore — almost like
it's fullscreen but it isn't."* Play Mode's copy of the same view never had it:
`PlayModeHost` (and Play Mode's own `RigScreenRoot` panel inside it) live in the
content row, so the title bar was never underneath.

So: **a shell-wide overlay belongs in `TitleContentGrid` row 1, not over
`WindowRootGrid`.** Row 1 is the content row; row 0 is the title bar and
collapses to 0 in borderless fullscreen, so an overlay in row 1 still owns the
whole screen there. The two layers that legitimately span both rows are the drag-
over `DropOverlay` (`IsHitTestVisible="False"`) and the command palette (a
transient Ctrl+Shift+P modal). Anything else that needs the pointer goes in the
content row.

Two guards, and they cover different halves:

- `Perf/TitleBarDragUiPerfSmokeTests` is the real one — it builds the shell,
  commits a frame, and hit-tests the centre of `TitleBarDragArea` with the rig
  closed AND open, then presses it for real, then checks the rig still owns its
  own header. **Hit testing goes through the compositor, not the layout tree**:
  a window that has only been measured and arranged answers `InputHitTest` with
  `null` everywhere, so the test lays out and then forces a render tick. It is
  ONE test method on purpose — headless pointer input has a single input root, so
  a second `MainWindow` swallows every simulated press, and a window cached
  across dispatches answers from the previous application's stale composition
  tree. Both were measured; each case passed alone and failed in company.
- `UI/WindowDragRegionTests` is the cheap structural half **in the default
  gate** (the headless suite only blocks at release): it reads `MainWindow.axaml`
  with the chrome view spliced in (`DotCharter.Tests/Infrastructure/MainWindowSource.cs`)
  as XML and fails if the rig overlay leaves the content row or grows a
  `Grid.RowSpan`, or if the drag strip loses its `Background="Transparent"` or
  its handler.

## `Views/Shell/ShellStyles.axaml` — the shell's own vocabulary

- **The SHELL has one visual vocabulary too, in
  `Views/Shell/ShellStyles.axaml`** — the third and last copy of the grammar
  Settings (`SettingsStyles.axaml`) and the Play Mode content screens
  (`Views/PlayMode/PlayModeStyles.axaml`, which `PlayModeView` includes) already speak. Include it
  with a `StyleInclude` and use its classes rather than re-inventing a header or
  a card: `TextBlock.Eyebrow` / `Classes="Title"` / `Border.HeaderRule` /
  `TextBlock.ScreenSubtitle` is the header lockup, `Border.ShellPanel` and
  `Border.ShellCard` are the two lit surfaces (each expects a
  `Border.TopLight` hairline as its first child), `Border.ShellBadge` +
  `PathIcon.ShellBadgeIcon` is the accent icon badge, `Border.ShellChip`
  (`.Accent`/`.Warn`/`.Danger`) is an inline fact, `TextBlock.FieldCaption`
  labels a filter/search field and `Border.SectionRule` separates sections.
  Consumers: the song browser, the toolbar flyouts, the status bar, the
  stacked-track rail, the amp/FX rack, the command palette and the diagnostics
  window. It carries **no `Cursor` and no `Transitions`** for the same reasons
  `SettingsStyles.axaml` does not, and it mints **no new `Color`** — the lit
  fills are the existing `PlayPanelFill`/`PlayTileFill`/`PlayTopLight`.
  - `Views/Shell/ProfileSwitcherFlyout.axaml` is a bare `ResourceDictionary`
    with no styles of its own, hosted by BOTH the status bar and the Play Mode
    front door. It may only use class names both hosts define — today exactly
    `Eyebrow` and `HeaderRule`. `ShellConstructionUiPerfSmokeTests` builds it
    under the status bar's style scope to pin that.

