# Rendering / perf — startup work and hidden-surface prewarm — deferred screen bodies: the case prewarm cannot help with

## Deferred SCREEN BODIES — the case prewarm cannot help with, because the cost is CONSTRUCTION

The four cases above are all about the first LAYOUT of something that already
exists. `PlayModeView` had the other kind: `MainWindow`'s `DataContextChanged`
fills `PlayModeHost` synchronously, before `Window.Show()` — it has to, or the
app's first frame is an empty screen — so all ~19 of its screens are
CONSTRUCTED in front of the first frame whether or not anything ever measures
them. That is the `bind=` phase of the `StartupPhases:` line, and it was the
largest phase of a published `r2r=yes` cold start (median 5,113 ms of an
8,941 ms `windowConstructed`; A/B'd headless at 6,039 ms against 666 ms for a
boot into the chart editor, which skips the fill entirely). No prewarm can
touch it: the work happens before the first frame by construction.

**Five of them are `ContentTemplate` bodies now** —
`GameplaySettingsOverlayHost`, `TuningCheckScreenRootHost`,
`PauseScreenRootHost`, and since #648 `SongSelectScreenRootHost` and
`ResultsScreenRootHost`
(`PlayModeView.DeferredScreenHostNames`; the practice planner, co-op setup,
the LAN multiplayer modal, the Network screen and the Progress hub
(`ProgressHubRootHost`, History + Trends) were removed on 2026-09-19 — restore
from tag `pre-cut-2026-09`; the measurement below is from when there were
eight, 1,822 of the view's 2,720 elements). A `DataTemplate` body is deferred content: it is built
when a layout pass reaches the presenter, and a hidden host never runs one. The
markup did not move file and kept its `x:Name`s — only the wrapper is new.
**Since #294 each body is a screen VIEW** (`<screens:GetReadyView />`,
`PauseMenuView`, `GameplaySettingsView`, in `Views/PlayMode/Screens/`): the
deferral is unchanged — the view is constructed when the template is built, not
before — and the screen's markup, names included, lives in the view's file.
Measured headless: the view builds **334 controls for the first frame, 881
after the prewarm listing**; `bind` median 5,113 → 1,363 ms.

Five rules, all pinned by `UI/PlayModeDeferredScreenTests` (default gate) and
`Perf/PlayModeDeferredScreenUiPerfSmokeTests` (behavioural):

- **The gate moves to the HOST and may not be on both.** `Realize` measures a
  hidden root's VISIBLE children, so a hidden body under a hidden host is
  skipped: the drain would build the content and then lay out none of it, and
  the first navigation would pay the whole first layout this file exists to
  remove.
- **A deferred body generates no fields.** A template body has its own
  namescope, so `PlayModeView` reaches the screens it drives through lookups
  that build the body on demand — `SongSelect` (the search keys) and `Results`
  (the reveal), #648 — and the reveal's skip, which runs on every key and click,
  through `ResultsIfBuilt`, which never builds. A body that cannot be built yet
  (no template: the view is not in a styled tree) is not marked built, so the
  next lookup or the drain tries again. Measured on a published `r2r=yes` build
  (three interleaved launches each, hidden desktop, 2026-10-11): `bind` median
  134 → 98 ms, `windowConstructed` 712 → 672 ms, `interactive` 1,185 → 1,130 ms.
- **Song Select drains right behind Home** on a Play Mode boot
  (`MainWindowPrewarm.HomeAndItsDestinations`): its body is built in a turn of
  its own, then its gated HOST is realized. From #294 to #648 that lookup named
  the screen ROOT, which carries no gate, so it found a visible part and
  realized nothing — the shell drain logged 14/14 surfaces instead of 15/15 and
  Song Select waited for the Play Mode listing. Pinned in
  `UI/HiddenSubtreePrewarmerTests`.
- **The BOOT surfaces stay inline** — `StartScreenRoot` and
  `MenuBackdropClip`. Deferring what the first frame SHOWS trades the bind
  phase for an empty window.
- **`HiddenSurfaces()` materializes the bodies FIRST**
  (`BuildDeferredScreenBodies`, `ApplyTemplate` + one
  `ContentPresenter.UpdateChild()`, then the same two on the body VIEW so the
  screen's markup is in the visual tree as an inline body's was — construction
  only, so the expensive half
  stays with the drain's one-per-turn pacing). Without it the named
  panes inside those bodies (the settings modal's seven) do not exist yet,
  every lookup returns null and those surfaces drop out of the drain with no
  error and no log line. It is
  the same move `DialogContainerView.HiddenSurfaces()` makes for its 34 dialog
  views. It never writes `IsVisible`, for the reason this file gives above.
  The shell's drain does that building FIRST, one body per turn, through
  `PlayModeView.ConstructionSteps()` (`IPrewarmConstruction`, #234), so by the
  time `HiddenSurfaces()` runs each build is a no-op; it still builds whatever a
  step has not.
- **`FindControl` cannot cross a template namescope** (nor a screen view's) —
  `PlayModeView.FindByName` falls back to a visual-tree walk, then the logical
  tree (a view nested inside a screen view is not in the visual tree until its
  own template applies), and `UI/PlayModeMarkup` is how the markup lints read
  the screen set: `Text()`/`Document()` inline every declared screen view, and
  `GateOf` asks for a screen's gate wherever it is declared.
  Three of them (`LanMultiplayerModalTests`, `NetworkScreenStructureTests`
  and the planner preview test, all since removed) read the screen root's own
  `IsVisible` and failed as if the gate had been deleted.

**A user who navigates before the drain arrives needs no code path of ours**:
the host turning visible runs its own measure, which builds the body. That is
what the second behavioural case asserts, and it is the difference between a
slow screen and an empty one.

### The rig SCREEN is gated the same way — a binding under a hidden panel still READS

Both Amp & FX rig hosts bind their screen from markup under a hidden overlay
(`Content="{Binding ToneRig.AmpFxRig}"` on `ShellRigScreenHost` in
`MainWindow.axaml`, whose host is `ViewModels/Shell/ToneRigViewModel`, and on
`RigScreenHost` in `PlayModeView.axaml`, whose host is
`ViewModels/PlayMode/PlayModeToneRigViewModel`). `IsVisible=false` skips the
LAYOUT of that subtree, but a binding evaluates its source when it is set up,
so a `??=` getter behind `AmpFxRig` was constructing the `PedalboardViewModel`
singleton AND two `AmpFxRigViewModel`s inside the `bind=` phase on every launch
— sampled at ~50 ms on the R2R build (2026-08-30), for an overlay nothing shows
at boot. Since 2026-09-02 both getters go through
`ViewModels/PlayMode/DeferredRigScreen`: the property answers `null` until
something WANTS the screen — `IsPedalboardOpen` flipping true (the
`OnIsPedalboardOpenChanged` partial runs before the generated setter raises, so
the panel turns visible over a bound screen), or the host's `EnsureRigScreen()`
— and the host raises `PropertyChanged(AmpFxRig)` so the binding re-reads. Both
prewarm lists (`PlayModeView.HiddenSurfaces()`, `MainWindow.HiddenShellSurfaces`)
call `EnsureRigScreen()` before listing the overlay, so the drain still lays the
rig out over a REAL screen — after first paint, at `Background`, where the cost
belongs. Pinned in the default gate by `UI/ViewModels/PlayMode/RigScreenDeferralTests`
(the gate, the Play Mode host's wiring and raise order, and a source lint that
both hosts and both prewarm lists stay on it). **The general rule: a
`{Binding X}` under a hidden subtree costs whatever `X`'s getter costs, at
bind. A lazy getter is not deferral; a gate the host opens is.**

**Since #194 the rig VIEW is deferred too, not only its screen.** Gating the
view model left `AmpFxRigView` itself — ~450 elements with its sub-views, in
BOTH hosts — constructed and styled in `windowCtor=` and `bind=` with a null
DataContext. Each host is now a `ContentControl` whose `ContentTemplate` holds
the view and whose `Content` is the gated screen, so the view is built when a
layout pass reaches the presenter AND the screen exists. Both prewarm lists call
`HiddenSubtreePrewarmer.BuildDeferredBody` on the host right after
`EnsureRigScreen()` and before listing the tone drawer by name. Numbers and
guards: [startup.md](startup.md).
