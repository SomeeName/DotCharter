# Rendering / perf — the UI responsiveness budget and the `r2r=` rule

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).


## Moved here from the root `AGENTS.md` at v0.43.2

The root guide is loaded for EVERY agent, so the detail lives here and the
root keeps the budget number, the `r2r=` rule and a pointer.

## UI responsiveness budget

**Switching a view, tab, screen or dialog must complete in ≤150 ms, and should
be under 20 ms.** The owner reports anything slower as lag, and it is felt
immediately because it lands on the UI thread between a click and the first
frame of the new surface.

The cost is almost never construction — it is the FIRST layout of a hidden
subtree. `IsVisible=false` skips `MeasureCore`, so styles, `ApplyTemplate`,
resource lookups and text layout are all deferred to the first switch, once per
surface, forever. Measured before this was addressed: editor→Settings 1,245 ms,
the export dialog 137 ms, all 34 dialogs 690 ms of first-open cost between them.

Three mechanisms keep it inside the budget and **all of them must stay**:

1. `Views/Shell/MainWindowPrewarm.PrewarmTopLevelViews` — templates for the top-level surfaces.
2. `Views/Shell/HiddenSubtreePrewarmer` + `IPrewarmableSurfaces` — the hidden
   sub-surfaces (settings tabs, Play Mode screens, dialogs) that (1) cannot
   reach, drained one per `Background` turn after first paint.
3. `Views/Shell/MenuFlyoutPrewarmer` — everything that lives inside a closed
   `Popup`, which neither of the others reaches. Two shapes:
   - The menu bar's DROPDOWNS: a `MenuItem`'s rows sit outside the visual tree,
     so the first click on each menu paid its whole realization (26-104 ms per
     menu, against 2-6 ms for every open after it). It realizes the ROWS, never
     the popup child, and never opens anything.
   - The transport bar's and status rail's POPOVERS (`Button.Flyout` /
     `FlyoutBase.AttachedFlyout`). Worse than a menu: until the first open there
     is no `Popup` and no `FlyoutPresenter` at all, so the content has no
     parent, no style scope and no templates, and a detached measure buys
     nothing. `Views/Shell/PrewarmableFlyout` builds that structure by
     CANCELLING its own `OnOpening` — Avalonia builds the popup before it asks
     whether the open may proceed — and the realization then STICKS because the
     content stays parented. Nine popovers, first open: **535 ms → 78 ms**
     (toolbar, six panels) and **68 ms → 8 ms** (status rail), `r2r=no`.
     - **…and a CLOSE takes the popup out of the logical tree, so the close
       puts it back (#176).** The next `ShowAt` re-parents the popup BEFORE
       `OnOpening` and re-styles the whole popover inside the click: 25-90 ms
       per repeat open on the real window, 2-5 ms in the `Popover … opened in`
       line, which starts at Opening. `PrewarmableFlyout.OnClosed` posts
       `RestoreClosedStructure` at `Background` — re-parent into the target's
       live style scope, THEN `RealizeBuiltContent`; repeat opens ~4-7 ms
       headless. A close-time re-measure WITHOUT the re-parent shipped once and
       drew every dropdown UNSTYLED (a detached subtree caches the
       `DynamicResource` fallbacks); `UI/PrewarmableFlyoutTests
       .A_close_re_parents_before_it_re_realizes` pins the order and
       `Perf/PopoverRestyleUiPerfSmokeTests` the brushes. Detail:
       [hidden-surface-prewarm.md](startup-and-prewarm/hidden-surface-prewarm.md).
       Menus never had the residual — their repeats are 0.6-1.4 ms.

**Order is part of the mechanism, not a preference.** The drain pays one surface
per `Background` turn, and `Background` is BELOW `Normal` — so the boot-time
PSARC library scan, whose `IProgress` callbacks marshal every batch onto the UI
thread, sits in front of every step of it while it runs (7.9 s / 2,160 songs,
cold). The chrome's dropdowns are the only surfaces one click from the window
the instant it appears, and until v0.55 they drained LAST, behind ~70 dialogs,
settings tabs and Play Mode screens: a click in the first seconds still paid the
full cold open (46-175 ms per popover, 8-88 ms per menu, `r2r=no`). The chain is
now shell chrome → menu dropdowns → rail popovers → everything else, pinned by
`UI/HiddenSubtreePrewarmerTests.TheChromesDropdowns_DrainBeforeTheDialogsAndScreens`.

`IPrewarmableSurfaces` only reaches the CONTENT of the five deferred hosts. The
surfaces declared straight in `MainWindow.axaml` — above all the **chart editor**
(`MainSplitGrid`), which `AppConfig.StartInPlayMode` leaves unmeasured for the
whole boot — go through `MainWindow.HiddenShellSurfaces()` instead. Adding it
took the first editor switch from 130 ms to 37 ms (second switch: 1.2 ms). A
Play Mode boot drains HOME ahead of them: the Start screen leads to Home, not to
the editor.

**A new hidden surface must be exposed through `IPrewarmableSurfaces`, or it
reintroduces the lag for itself.** Realize it by measuring its VISIBLE children —
never by writing `IsVisible`, which grabs keyboard focus in five dialogs,
re-attaches the airspace host, and fights the binding that owns the property.
Prewarm work is always `DispatcherPriority.Background` and always AFTER first
paint; nothing may delay the first frame.

Measure with `DotCharter.Tests/LocalOnly_ViewInflationProbe.cs` and
`LocalOnly_ShellStartupProbe.cs` (both git-ignored; build with
`-p:IncludeLocalOnlyHarnesses=true`), and A/B with
`DOTCHARTER_NO_SURFACE_PREWARM=1`. Note `Window.Show()` **is** the initial layout
pass — timing `UpdateLayout()` after it measures nothing.

### Measuring a shell switch on the real window — `ShellSwitchProbe` (#234)

`Views/Shell/ShellSwitchProbe` walks Home, Chart, Library, Play, Settings and the
rig three rounds on the REAL window (perf autopilot: `DOTCHARTER_UI_AUTOPILOT=perf`
plus `DOTCHARTER_AUTOPILOT_SWITCHES`) and logs one `[SwitchProbe]` line per step:
`frame` (call → second animation frame) and `idle` (→ first `Background` tick),
split into `call` (the switch's own handlers), `layout` (an explicit
`UpdateLayout()` right after the call — the pass the render tick would run) and
`queued` (how late the step itself ran: whatever held the UI thread when it was
due, which the frame time cannot see). Run a Release R2R publish (`r2r=yes`),
null audio, on the second monitor (on a hidden desktop when the owner is at the
machine).

**Every autopilot or measurement launch sets `DOTCHARTER_DATA_ROOT` to a FRESH
temp folder (#439)** — config, logs and every `%LOCALAPPDATA%` cache (PSARC
audio, stems, waveforms, album art, models) then live under `<root>\Roaming` and
`<root>\Local`, and the user's real folders are never read or written. Under the
override a portable `appconfig.json` beside the exe is IGNORED: seed the launch's
settings (null audio, the library folder for `DOTCHARTER_AUTOPILOT_SONG`) as
`<root>\Roaming\DotCharter\appconfig.json`. Check the log for the
`Data root override:` line before trusting a run, and never "clean up" by
deleting what changed in the user's real `%APPDATA%\DotCharter` or
`%LOCALAPPDATA%\DotCharter` — delete the temp root instead.
`tools/agent/screen/Capture-App.ps1` and the release launch smoke set it.

- `DOTCHARTER_AUTOPILOT_SONG=<a .psarc in the configured library>` opens that
  library song in the editor through the browser's own load path, and
  `DOTCHARTER_AUTOPILOT_HOLD=1` stops after the load — nothing plays, the window
  stays open — which is the state the #412 memory measurement takes its gcdump in.
  It waits up to 60 s for the song to be LISTED (the startup scan of the same
  folder may still be running). `DOTCHARTER_AUTOPILOT_SONG_REPEAT=N` opens it N
  times: the first open pays the JIT and the cold caches, the rest are repeat
  opens. Each open's INFO `PSARC song loaded` line ends with its split
  (`ms: load= prepare= apply= audio=`, #436); with `MinimumLogLevel: Debug` the
  `PSARC chart parsed` line adds the parse's per-phase split. Measure a published
  build (`r2r=yes`): on the dev build the first open is mostly JIT.
- `DOTCHARTER_AUTOPILOT_STYLE_CENSUS=1` waits for the drain, then logs
  `[StyleCensus]`: the style value frames every realized control carries, per
  rule and scope (`Views/Shell/StyleFrameCensus`, public value-store
  diagnostics, so it works on the release build). It is how #428 found which
  rules to cut; `Perf/StyleIncludeScopeUiPerfSmokeTests` writes the same table
  headless to `DOTCHARTER_STYLE_CENSUS_OUT`, and an effective-value dump of every
  control to `DOTCHARTER_STYLE_VALUES_OUT` (`Perf/EffectiveValueDump`) — diff two
  dumps to prove a style move changed no look, including on prewarmed surfaces no
  screenshot shows.
- `DOTCHARTER_AUTOPILOT_SWITCHES=1` waits for the startup drain to finish
  (`MainWindow.HiddenSurfacesDrained`) — a first entry is then the switch's own
  cost.
- `DOTCHARTER_AUTOPILOT_SWITCHES=early` starts while the drain runs and notes each
  step as input (`PrewarmPacer.NoteInput`), as a click does — the user who clicks
  in the first seconds.

**What the first numbers were (Release R2R, 1920x1080, 2026-10-05).** The
probe's first version ran its first round while the drain was still realizing
surfaces, and every slow FIRST entry it reported was a drain step, not the
switch: Home → Chart 109 ms, Chart → Library 122 ms to idle, Library → Play 74 ms
were 2-5 ms each once the drain had finished. Two mechanisms put a drain step
between a click and its frame, and both are fixed (see
[hidden-surface-prewarm.md](startup-and-prewarm/hidden-surface-prewarm.md)): a
step already queued at `Background` ran in the idle gap before the render tick
(`PrewarmPacer` now looks again when the step's turn comes), and the drain's
listing was ONE 0.7-1.9 s turn (now produced lazily, one build per turn). First
entries with early clicks, medians of three interleaved runs each: Home → Chart
121 → 6 ms, Chart → Library 314 → 6, Library → Play 192 → 8, Settings → Chart
407 → 16, Chart → Home 283 → 78. Every return to Home after the drain: 19-31 →
2-5 ms (Home's read moved off the UI thread, see target-architecture.md). What
got slower, after the drain: the FIRST Home show (17 → 30 ms: its re-read now
lands a frame later), and Settings (first 33 → 38 ms, repeats 7 → 11; leaving
13 → 23 first, 8 → 13 repeats), because every settings tab is realized now and
the page's load and revert update all of them.

**`Background` is not "after the frame".** A job posted at `Background` from a
click runs in the idle gap BEFORE the next render tick, so it is paid inside the
switch. Only a `RequestAnimationFrame` callback is after a frame.

**The residuals are single drain steps, not switches.** A click that lands while
one surface is being realized waits for it (`queued` in the early run). Every
step of 50 ms or more is logged (`Prewarm: long step …`). The rig's tone browser
(1.1-1.9 s, once per rig instance — the shell's and Play Mode's are two
`AmpFxRigView`s), Keybinds (~600 ms), Appearance (~400 ms) and the Mixer tab
(~250 ms) are realized a chunk per turn since #253 (see
[hidden-surface-prewarm.md](startup-and-prewarm/hidden-surface-prewarm.md)):
early clicks' worst `queued` went 1.0-1.7 s → 0.3-0.5 s, Release R2R, medians of
three interleaved runs. The largest single steps left (2026-10-05, same build):
the chart editor's first realize (`MainSplitGrid`, 0.4-0.8 s), the construction
turns (`build`, 0.3-0.6 s: the top-level views, the settings tab views, the
dialog views), the two rig overlays (~0.2-0.3 s each) and the settings host
(~0.2-0.3 s).

**Delete or rename a view and you break these harnesses SILENTLY.** The
inflation probe names its surfaces as C# types (`new
DotCharter.UI.Views.PlayMode.PlayModeView()`), it is git-ignored, and the default
gate does not compile it — so nothing goes red, on any machine or in CI. v0.43
deleted `PedalboardView` and the probe stopped building for five releases; the
measuring tool for the responsiveness budget was simply unavailable, and the
break only surfaced when someone tried to measure. The list is now
`Views/PlayMode/AmpFxRigView`, the surface that replaced it. **After deleting or
renaming a view, run `dotnet build DotCharter.Tests/DotCharter.Tests.csproj
-p:IncludeLocalOnlyHarnesses=true` once** — and because the file is untracked,
fix it in every checkout that has one.

### Never read a cold-start number without checking `r2r=`

`PublishReadyToRun` in `DotCharter.UI.csproj` is conditioned on
`'$(RuntimeIdentifier)' == 'win-x64'`, which only holds during
`dotnet publish -r win-x64` — what `tools/release/New-Release.ps1` and CI do.
**`bin/Release/net10.0/DotCharter.exe`, the one you launch while developing, is
NOT precompiled and JIT-compiles its whole startup path.** Same machine, same
code, published-R2R vs built-Release: window ctor 549 → 222 ms, DataContext bind
774 → 298 ms, first layout 358 → 208 ms, first editor switch 42 → 23 ms. Over
half of a dev build's cold start is JIT that no user ever pays.

So the `StartupPhases:` line ends with `r2r=yes|no|?`
(`Diagnostics/ReadyToRunProbe`, mirrored into Session Health). A phase number
from an `r2r=no` run is not a product measurement — it is a JIT measurement, and
"optimising" it by moving real work around buys nothing.

**Not every published assembly is precompiled** (#410): the ones that do not load
before `StartupPhases:` (AI SDK, Guitar Pro, PSARC/Wwise, ONNX, GDI+, the
non-Windows Avalonia backends) are in `PublishReadyToRunExclude` and JIT on first
use, and `Silk.NET.Direct3D11`/`DXGI` are trimmed. `r2r=` reads `DotCharter.dll`
only. **Code you move onto the startup path that lives in an excluded assembly
JITs on every launch** — take that assembly off the list (and out of
`Infrastructure/PublishPayloadSettingsTests`' reasoning) in the same change.
Details and numbers: `tools/release/README.md` § "Payload size".


### Cold start, measured — and `bind=` was the whole story

**Read the phases as a SHAPE, not as absolutes.** Everything below is
`r2r=yes` (published self-contained, `-r win-x64`) against a portable copy of
the owner's real `appconfig.json`, launched by a script that reads the
`StartupPhases:` line off stdout and then kills the process tree. The box was
carrying four other agents' builds and tests throughout, so the absolute
numbers are 3-4× what an idle machine gives — which is exactly why the two
builds were run **alternately, run for run**, and why only medians are
compared.

`di`, `services`, `windowConstructed`, `opened` and `interactive` are absolute
milliseconds since `OnFrameworkInitializationCompleted` began; `vmGraph`,
`keybinds`, `windowCtor` and `bind` are deltas.

| phase | BASE min | BASE med | AFTER min | AFTER med |
|---|---|---|---|---|
| di | 182 | 282 | 145 | 274 |
| services | 272 | 446 | 227 | 412 |
| vmGraph | 442 | 1033 | 422 | 1099 |
| keybinds | 3 | 33 | 4 | 5 |
| windowCtor | 1264 | 1951 | 1139 | 1905 |
| **bind** | **2974** | **5113** | **1101** | **1363** |
| windowConstructed | 6425 | 8941 | 3029 | 5646 |
| opened | 6959 | 9602 | 3519 | 6870 |
| interactive | 7129 | 10266 | 3678 | 8090 |

n=6 interleaved pairs, 2026-08-29, BASE at `9cc50f87` and AFTER at `bbe9c3e9`.
A standalone BASE-only set of 5 taken earlier the same evening on a quieter box
read `bind` 1988-4132 (median 3539) — same phase, same shape, a third of the
wall clock. **Never compare a number here against one taken on a different
day's load.**

Only **`bind`** moved, and that is the point: it is the one phase the change
touches, and it is the largest of them. `windowCtor` is unchanged at ~1.9 s
median on both sides, which is what makes the `bind` delta a signal rather than
noise.

**What `bind` is.** `MainWindow`'s `DataContextChanged` fills `PlayModeHost`
synchronously — it must, or the app's first frame is an empty screen — so
`PlayModeView`'s whole markup is constructed there, before `Window.Show()`.
A/B'd headless (`LocalOnly_ShellStartupProbe` with
`DOTCHARTER_PROBE_BOOT_EDITOR=1`, which boots on the editor tab and skips the
fill entirely): **bind 6,039 ms with it, 666 ms without**, so ~90 % of the
phase IS that one inflation.

**The fix, and the rules it leaves behind.** Eight screens — the Progress hub,
the gameplay settings modal, Get Ready, the planner, Network, the LAN modal,
Pause and co-op setup, **1,822 of the view's 2,720 elements** at the time —
were declared as
the `ContentTemplate` of a `ContentControl` host instead of inline. Template
content is not BUILT until a layout pass reaches the presenter, and a hidden
host never runs one, so the construction moves to whichever comes first: the
prewarm drain after the first frame, or the user's own navigation, which
realizes it for free by making the host visible. Measured in the headless
session: `PlayModeView` builds **334 controls for the first frame and 881 after
the prewarm listing** (before Song Select and Results joined). Five are
deferred — the gameplay settings modal, Get Ready, Pause, and since #648 Song
Select and Results; five others were removed on 2026-09-19 (the Progress hub
among them), restore from tag `pre-cut-2026-09`.

- **The gate belongs on the HOST, and never on both.** `HiddenSubtreePrewarmer
  .Realize` measures a hidden root's VISIBLE children, so a second gate on the
  body would leave the drain building content it then lays out none of.
- **A deferred body generates no fields.** A template body gets its own
  namescope, so `PlayModeView` reaches the two screens it drives — Song Select
  (the search keys) and Results (the reveal) — through `SongSelect` / `Results`,
  lookups that build the body on demand (#648); their own named controls are
  the lifted views' fields. The reveal's skip runs on every key and click, so it
  uses `ResultsIfBuilt`, which never builds.
- **`HiddenSurfaces()` must materialize the bodies before it lists anything** —
  the same move `DialogContainerView.HiddenSurfaces()` makes for its 34 dialog
  views. The thirteen named panes inside them do not exist until it does, and
  `FindControl` cannot cross a template namescope in any case, so
  `PlayModeView.FindByName` falls back to a visual-tree walk.
- `UI/PlayModeDeferredScreenTests` (default gate, including a **ratchet** on the
  deferred share of the markup) and
  `Perf/PlayModeDeferredScreenUiPerfSmokeTests` (behavioural) pin all of it, and
  `UI/PlayModeMarkup.GateOf` is how the other markup lints ask for a screen's
  gate now that it can live one level out.

**Measured and REJECTED, so nobody re-does it:** warming the localization
`ResourceManager` on the worker task `App.Initialize` already runs for
appconfig + keybinds. The cold resource-set load is **0.3 / 0.0 / 0.0 ms** for
en-US / de-DE / ja-JP — 3,813 keys, and not on the cold-start budget at all.
