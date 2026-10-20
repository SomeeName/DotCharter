# Session Health, accessibility, RTL, global UI scale and the command palette — right-to-left; global UI scale

## Right-to-left (RTL)

- The shell root grid in `MainWindow.axaml` binds `FlowDirection` to
  `LocalizationService.Instance.IsRightToLeft` via `BoolConverters.ToFlowDirection`,
  so an RTL community language pack (e.g. `ar.json`) flips the whole layout.
- **…and since 0.61 so do the app's other two top-level windows**, Session
  Health and About, which bound nothing: an RTL pack mirrored the shell and left
  those two the only surfaces on screen facing the other way. Same binding, on
  the same `BoolConverters.ToFlowDirection`, inside the same `RootScaler`
  wrapper the scale uses. `UI/WindowUiScaleTests
  .EveryWindow_CarriesTheScalerAndFollowsTheFlowDirection` walks every
  `<Window` under `Views/` and fails on a new one that carries neither.
  Built-in languages are LTR, so this is a no-op for them.
- Directionality logic is covered by `RtlSupportTests`; full-shell mirroring
  can't be asserted headless (Avalonia mirrors via a render transform, not
  child `Bounds`). **Manual RTL check:** drop an `ar.json`/`he.json` pack into
  `<ConfigRoot>/languages`, select it in Settings, restart, and confirm the
  sidebar/menus/inspector mirror without clipping. Caveat: the native GL
  timeline and Vulkan 3D panels mirror with the shell under RTL — verify those
  visually if you ship an RTL pack.

## Global UI scale (accessibility)

- `MainWindow.axaml` wraps the shell in a `LayoutTransformControl` (`RootScaler`).
  `MainWindow.ApplyUiScale` sets its `LayoutTransform` from `UiScaleController`
  (a static-with-event channel like `ChartTheme`), so Settings → Appearance can
  live-preview the scale and the whole shell reflows. Persisted as
  `AppConfig.UiScale`; **1.0 leaves the transform null (true no-op)**. Avalonia
  has no per-control `LayoutTransform` — the `LayoutTransformControl` wrapper is
  required and it constrains star-sizing correctly. Native GL/Vulkan panels
  mirror the scale via their layout slot (verify visually if you change this).
- **That wrapper is the WHOLE mechanism, so a window without one is simply not
  scaled — and until 0.61 two of the three had none.** Session Health and About
  rendered at 100 % for a user who had set 150 % because they cannot read 100 %.
  `Views/Shell/WindowUiScale.Attach(window)` is the shared attachment: it finds
  the `RootScaler`, applies `TransformFor(scale)`, follows
  `UiScaleController.ScaleChanged`, and **unhooks on `Closed`** — that event is
  static, so a per-open window that stayed subscribed would be rooted for the
  process.
  - **The two window SHAPES need different answers.** A resizable window
    (Session Health) keeps its size and reflows scaled content. A window with a
    pinned `Width` and `CanResize="False"` (About, 640 px + `SizeToContent`)
    cannot: the transform makes the content wider and the pinned width just
    CLIPS it, so `Attach` scales its declared `Width`/`MinWidth` too — from
    values read ONCE, because after the first apply those properties hold
    scaled numbers and re-reading them compounds. A `SizeToContent` dimension is
    `NaN` and is left alone.
  - `UI/WindowUiScaleTests` pins the identity-is-null rule, the length scaling,
    the `NaN` guard, that every `<Window` under `Views/` carries the wrapper,
    and that the two non-shell windows call `Attach` (the shell keeps its own
    `ApplyUiScale`, which also drives full-screen and the title bar).

## Interface scale: the AUTO arm

**The setting used to be a bare slider that defaulted to 100 %, and that is why
it never helped anyone.** The user it exists for is the one who does not know
the app can be scaled at all — the owner's report was a 2560 x 1440 full-screen
window whose Play Mode start screen occupied roughly 350 x 300 px, with the
follow-up that it is *worse* at 1920 x 1080. The row is now **Auto + five fixed
percentages** (`Settings → Appearance → UI Scale`, an `ItemsSource`/
`SelectedIndex` ComboBox), and **Auto is the default**.

- `Services/AutoUiScale` is the whole policy, pure and static: client height in
  DIPs / **1080** (the height the fixed pixel sizes were authored against),
  **clamped 1.0–2.0**, **quantised to 0.05**, with a **hysteresis band of half a
  step + 0.02**. Table: 1080 ⇒ 1.00, 1200 ⇒ 1.10, **1440 ⇒ 1.35**, 1600 ⇒ 1.50,
  2160 ⇒ 2.00. `AutoUiScaleTests` is the contract.
  - **The floor is 1.0 and not `UiScaleController.MinScale`** — Auto may only
    GROW. And **1080p stays exactly 1.0 by policy** (owner, 2026-09-12): the
    fixed layouts were authored there and a global scale above 1.0 risks
    overflowing them, so 125 % at 1080p is a MANUAL choice.
  - **The input is DIPs, so OS scaling is not compounded.** A user already at
    150 % Windows scaling on a 4K panel sees ~1440 DIPs, not 2160.
  - **The hysteresis is not decoration.** Quantisation alone flaps when a
    window height lands on a step boundary, and each flap is a full shell
    re-layout. The band must stay strictly narrower than a step or Auto latches
    on its first answer for ever — pinned by
    `AutoUiScaleTests.TheBand_IsNarrowerThanAStep`.
- **`UiScaleController` now has two arms and they are different methods.**
  `Set` applies WITHOUT touching the mode (the load paths use it — a load must
  not cancel the mode its value was persisted under); `SetManual` applies AND
  leaves Auto (a slider drag or a percentage pick). Collapsing them is a real
  bug in either direction and `UiScaleControllerTests` pins both.
  `ApplyAutoForHeight` is a no-op unless `IsAuto`.
- **The shell is the only thing that knows the client height**, so `MainWindow`
  answers: `SizeChanged` and `AutoModeChanged` both route to
  `ReapplyAutoUiScale`, and `Loaded` calls it **BEFORE `_layout.RestoreOnLoad`**
  — `DockRowStars` derives the dock split from `UiScaleController.Scale`, so
  deriving after it would restore every Auto user's split against the wrong
  scale on first launch. **This cannot feed back**: the factor moves the shell's
  layout transform, i.e. the content inside the window, never the client size
  that is read.
- Persisted as **`AppConfig.UiScaleAuto` (default true)** beside `UiScale`.
  `UiScale` is the MANUAL arm's keepsake and is deliberately NOT overwritten
  while Auto is on, so switching back to a percentage restores the user's own
  choice rather than the window's.
- **The applied factor is a READOUT, never a tracked setting**
  (`AppearanceSettingsViewModel.UiScaleReadout`, a formatted string). Under
  Auto the shell re-derives the factor on every window RESIZE, so tracking it
  made dragging the window edge with Settings open report unsaved changes no
  edit had made. The tracked setting is `UiScaleChoiceIndex`
  (`SettingsUnsavedCounterHonestyTests.TheCounterIsNotItselfASetting`).
  `InterfaceScaleChoices` is the one index ⇔ (mode, factor) map, shared with
  Play Mode's gameplay modal so the two doors cannot disagree about what
  "125 %" is; Settings reaches it through `IInterfaceScale`
  (`Platform/InterfaceScale`).

### What the transform does not reach by itself

- **Natively-composed surfaces sized their swapchain from `RenderScaling`
  ALONE, which is wrong under any layout transform — and that shipped with the
  setting.** `RootScaler` wraps the whole shell, so a descendant's `Bounds` is
  in the transform's INNER units while the pixels it occupies are
  `Bounds x uiScale x RenderScaling`. The 3D highway
  (`Preview3DComposedCanvas.UpdateSizes`) and the 2D timeline
  (`ChartRenderer` → `VulkanTimelineBackend.BeginFrame`, which also feeds the
  glyph atlas) therefore rendered at **1/uiScale** of their composited
  resolution and the compositor upscaled the difference — a soft highway and a
  soft timeline, no exception, no log line. It was invisible only because the
  setting defaulted to 1.0; **Auto puts every 1440p user on it.** Both now go
  through **`Controls/VisualPixelScale.For(this)`**, which folds
  `RenderScaling` with the accumulated matrix to the top level — the matrix and
  not `UiScaleController.Scale`, so it is also right under the rig's zoom host,
  `RigScaleToFit` and the launch hero, and cannot drift from what the
  compositor does. `_visual.Size` stays in inner DIPs: only the PIXEL count
  knows about the extra factor. Pinned by
  `Perf/InterfaceScaleUiPerfSmokeTests.At150Percent_AComposedSurfaceReportsScaledPixels`
  (reverting the seam makes it report 1280 where 1920 is needed).
- **Popups scale, and it takes TWO mechanisms because Avalonia only supplies
  one of them.** The overlay popup host is not inside `RootScaler` — it lives in
  the window's `PopupOverlayLayer`, a sibling of the content presenter — but
  `OverlayPopupHost` carries a styled `Transform` property and Avalonia assigns
  it from the anchor's accumulated transform. Measured headlessly on Avalonia
  **12.1.2** at a 1.5x shell scale (2026-09-12):
  - a **ComboBox dropdown** already got `Transform = {M11:1.5 M22:1.5}`, content
    measuring 1.5, anchored exactly (host top-left 391,88 against the anchor's
    bottom-left 391,88) — Avalonia's own doing, nothing of ours;
  - a **`Flyout`, a `MenuFlyout` and a `ToolTip`** got **no transform at all**
    and measured **1.0**. That is a 100 % menu beside 135 % content on the
    owner's 1440p monitor, i.e. what Auto would have shipped.

  `Views/Shell/PopupUiScale` fills in the second case. **A `Style` setter on
  `OverlayPopupHost.Transform` does NOT work and was tried first, at window and
  app scope**: Avalonia ASSIGNS that property — including assigning nothing — as
  a LOCAL value while positioning the host, and a local value beats every style.
  So the hook is an attached `PopupUiScale.FollowShellScale` set on every
  `Popup` by one style in `Styles/Chrome.axaml`; on each open it walks up from
  `popup.Child` to the host and sets the transform **only when the host has
  none**. That ordering is the point — the ComboBox keeps the exact factor
  Avalonia computed from its own anchor, and only the hosts handed nothing fall
  back to the shell factor.
  - **ONE shared instance, MUTATED in place.** The scale changes while popups
    exist (Auto re-derives on a window resize; a maximise from a small window is
    exactly that) and `MenuFlyoutPrewarmer` realizes flyout content ahead of
    time. A fresh `ScaleTransform` per change would leave an already-assigned
    host on the old factor for ever.
  - **The prewarmer needs no change and must not get one.** It never OPENS a
    popup (nor does the close-time re-realize, #176), so no host
    exists while it runs and the factor is filled in on the first real open —
    which is also why a prewarmed flyout cannot be stuck at the scale it was
    warmed at. `InterfaceScaleUiPerfSmokeTests.ThePrewarmer_NeverOpensAPopup`
    pins that it stays that way.
  - **Do NOT "fix" this by scaling `PopupOverlayLayer`.** It was the obvious
    alternative and it is wrong: the host's position is a `Canvas` coordinate
    already in top-level space, so scaling the layer multiplies the POSITION
    too. Measured — at 1.35x a flyout anchored at x=40 landed at **x = −121**.
    `At135Percent_APopupOpensAtItsAnchor` exists to catch exactly that and does.
  - **Known imprecision, stated rather than hidden:** a popup anchored inside a
    NESTED transform (`RigBoardZoomHost`, `RigScaleToFit`, the launch hero's
    scaler) that falls through to the fallback gets the SHELL's factor, not
    shell x nested — 1.35 where 1.08 would be exact at board zoom 0.8. It was
    1.0 before, so the fallback is strictly better everywhere and exact for the
    shell's own menus, flyouts and tooltips.
  - **`PopupRoot` — the separate-HWND popup path, unused while `OverlayPopups`
    is on (always, since the 3D preview is composed-only) — is not
    covered**: it is a `WindowBase`, not a `ContentControl`, and has no
    `Transform` property. Unverified on a real HWND.
  - In-window scrims and overlays were never affected: every `*Scrim` surface is
    ordinary markup inside the view tree, i.e. inside `RootScaler`, and so is
    Play Mode's whole screen stack (it never opens a Flyout on its own menus).

  **A measurement trap worth naming, because it produced a false claim in this
  very file for one commit:** `OverlayLayer.GetOverlayLayer(control)` returns the
  GENERIC overlay layer, which is **not** where popups go — Avalonia 12 hosts
  them in a separate `PopupOverlayLayer`. Measuring the generic layer with no
  popup open accumulates 1.0 trivially and reads as "popups do not scale". Open
  a real popup and find the `OverlayPopupHost`.
