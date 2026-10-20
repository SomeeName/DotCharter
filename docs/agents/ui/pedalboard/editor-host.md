# Hosting the rig from the chart editor

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

## DSP pedalboard from the editor

- The chart editor opens the SAME tone rig Play Mode uses. **Since v0.43 that is
  `AmpFxRigView`, not the deleted `PedalboardView`** — `MainWindow.axaml` names
  the type directly (`<playModeViews:AmpFxRigView DataContext="{Binding ToneRig.AmpFxRig}"/>`
  inside `<Panel x:Name="PedalboardOverlay" ZIndex="550">`, gated
  `IsVisible="{Binding ToneRig.IsPedalboardOpen}"`), which is the whole point of the
  extraction: markup nested inside `PlayModeView` was not a type a second host
  could name, so the overlay went on resolving the retired view through the
  ViewLocator and the app looked like it shipped two pedalboards. The `Panel`
  carries the gate and the rig carries its own `FireScreen` frame, its own
  18 px inset and its own header ✕ (which raises `CloseRequested` — the host
  closes itself), so there is **no card chrome, no scrim grid and no outer
  `ScrollViewer` around it**. `MainWindow.axaml`'s comment block above that
  `Panel` is the authority; read it rather than this paragraph if they disagree.
  The `IsPedalboardOpen` bool, `OpenPedalboardCommand`, `Pedalboard` and
  `AmpFxRig` live on `ViewModels/Shell/ToneRigViewModel`, exposed as
  `MainWindowViewModel.ToneRig`; the chain VM, the NAM library, the input-jack
  picker and the re-amp transport reach it as `Func<T>` factories, so nothing
  builds the rig during shell DI (`UI/ToneRigInjectionTests`). The shell keeps
  the Esc walk and feeds the ✕ rule through `ShellChromeVisibleSource` /
  `SyncRigCloseButton`. **`AmpFxRig` is GATED, not lazy** (2026-09-02): the
  binding under the hidden panel reads the getter at `MainWindow`'s bind, so a
  `??=` there built the chain VM and the screen on every boot (~50 ms of the
  `bind=` phase). `ViewModels/PlayMode/DeferredRigScreen` answers null until the
  overlay opens or the prewarm's `EnsureRigScreen()` asks; see
  [rendering-perf/startup-and-prewarm.md](../rendering-perf/startup-and-prewarm.md)
  and `RigScreenDeferralTests`.

  *The paragraph below describes the pre-v0.43 host — the `#D9000000` scrim, the
  `4*/92*/4*` star grid and the stretched 1100×760 card — and is kept only
  because the SIZING rationale still explains why the rig is given the whole
  window rather than a small centered card.* It hosted
  `<ContentControl Content="{Binding Pedalboard}"/>` DIRECTLY — **no outer
  `ScrollViewer`**: `PedalboardView` owned its own rack scroll/zoom, and wrapping it
  gave it unbounded height that crushed the layout and let the save-dialog scrim
  float mid-content. `Pedalboard` was `App.Services.GetService<PedalboardViewModel>()`
  — the **DI singleton**, so the editor and Play Mode drive one `IDspChainService`
  chain (a tone dialled in one surface is live in the other).
  **FOUR things now take that chain over and put it back, and they must never
  overlap**: a Play Mode run's in-game tones, the Tone Designer's Preview toggle,
  (0.56) the chart editor's tone preview, and Play Mode's song-tone edit (a Get
  Ready tone chip). All but the Tone Designer share ONE
  `RsTonePlaybackCoordinator` and one manual-chain snapshot, arbitrated by
  `RsToneDriver` — do not add another owner without going through it, or the new
  one will snapshot somebody else's tone rig as the charter's own board.
  `PedalboardView` is
  resolved from the VM by the app `ViewLocator` (each `ContentControl` builds its own
  view instance; the shared VM as DataContext is fine — do NOT put a control instance
  in a binding). **No airspace:** the 3D preview is composed-only
  (`Preview3DComposedCanvas`, no child HWND), so the rig overlay — and any
  dialog — draws above the LIVE preview with nothing hidden or captured. A new
  full-window editor overlay needs no workaround either; do not add a
  panel-hide or a freeze-frame (`UI/Preview3DComposedOnlyTests`).
- **The rig's own body below the routing canvas is ONE host, `RigWorkspace`,
  and its height never depends on what is in it** (W1, 2026-09-16). Both hosts
  get this for free — it is the view's own layout, not the host's — but it
  changes what "open the editor" costs: nothing. It holds the module editor
  ALONE since W4 (the tone drawer left it for the Tones mode), at one persisted
  height (`PedalboardViewModel.Workspace` → `RigWorkspaceViewModel.Height`,
  `AppConfig.RigWorkspaceHeight`), so the canvas above is invariant and the gear
  rail beside it never moves.
- **…and the TONE SURFACE is a MODE that replaces that whole body** (W4,
  2026-09-16). `RigTonesMode` (a `RigTonesView` since 2026-09-27) is a sibling of `RigFlatBody` in the same cell,
  gated on `AmpFxRigViewModel.IsTonesMode` — which IS the `IsBrowserOpen` flag
  both hosts' Esc walk-ups and `TryCloseOverlay` already unstack, so neither
  host needed a change. A host that opens the rig into the mode still answers
  Esc the same way, and picking a block LEAVES the mode into that block's
  editor.
  Details and the three rules in
  [../fire/amp-fx-rig-screen.md](../fire/amp-fx-rig-screen.md) § 2.
  **And since W3 selecting a block in that host always opens its editor** — the
  drawer falls back to its stub and a previous HIDE is undone — so a host that
  opens the rig with the tone drawer up still answers a click on the board.
- **That `Panel` lives in `TitleContentGrid` row 1 — the CONTENT row — and it may
  never span the title bar again.** It was a child of `WindowRootGrid` at
  `Grid.Row="0" Grid.RowSpan="2"` until v0.48.6, i.e. over `TitleBarRow` as well,
  and because the rig paints the fully opaque `FireOverlayFill` it covered
  `TitleBarDragArea` — the only thing that moves a window whose decorations are
  `BorderOnly`. Opening the rig pinned the window to the desktop for as long as
  it was up ("almost like it's fullscreen but it isn't"), while every rig test
  stayed green: the overlay constructed, laid out and rendered perfectly. Play
  Mode's host never had the bug because it has always been inside the content
  area. Full rule, and the two guards that hold it, in
  [../shell-chrome.md](../shell-chrome.md) § "The title bar is CUSTOM".
- Entry points: a sidebar `IconBtn` (`IconAmp` glyph, `Classes.Active` on
  `IsPedalboardOpen`) + Tools → Pedalboard menu item + the command palette
  (`tools.pedalboard` in `CommandCatalog`). **Esc unwinds the rig's THREE layers,
  outermost last** — the browser's "save as new" modal, then the TONE BROWSER
  drawer, then the overlay itself — before falling through to clearing the
  selection (`MainWindowViewModel.HandleEscapeKey`). The rig has two hosts and one
  `PedalboardViewModel` singleton, so this walk-up and
  `PlayModeViewModel.TryCloseOverlay` must handle the SAME layers in the SAME
  order. **The drawer rung was missing here until 0.61**, present only in Play
  Mode: browsing tones in the chart editor and pressing Esc threw away the whole
  overlay, while the identical gesture in Play Mode closed only the drawer.
  `UI/RigEscapeParityTests` is a theory over both walk-ups asserting the three
  layers appear, and appear in that order, in each.
  Loc keys: `Main_TogglePedalboard`, `Menu_Pedalboard`, `Pedalboard_Title`.
- `PedalboardViewModel`/`PedalboardView`/`Controls/Dsp/*` are owned by the
  pedalboard-overhaul area — the editor host only resolves the VM and toggles the
  overlay; it never reaches into the pedalboard internals.
