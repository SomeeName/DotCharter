# Rendering / perf — tooltips and hover-created popups

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).

## Tooltips and hover-created popups

- **Nothing may create a POPUP on a bare pointer-enter — a hover has to earn it
  with dwell.** Every ComboBox dropdown, flyout, menu **and tooltip** is its own
  Win32 popup HWND here (`OverlayPopups` is off, and it has to be — the airspace
  rule above), so one hover = one native window + one ANGLE/D3D render target
  created and destroyed. `Program.cs`'s `OverlayPopups` comment records the
  earlier measurement: "the per-open HWND + GPU-surface creation measurably
  hitched the 2D timeline". The 2D timeline is paced by
  `TopLevel.RequestAnimationFrame` **on the UI thread**, so it is hit directly;
  the native 3D loop has its own thread and is hit through the GPU/DWM instead.
  - The trap is Avalonia's own: **once ONE tooltip has been shown, every further
    tipped control the pointer crosses opens with `showDelay = 0`** —
    `ToolTipService.OnTipControlChanged` takes that branch whenever
    `closedPreviousTip` is true, i.e. for the whole of a sweep, and
    `ToolTip.Close()` DISPOSES the popup host so each open rebuilds it. That is
    the owner's "hover long enough that a tooltip showed once, then fast move
    left and right over the toolbar and it stutters the whole 2D timeline and 3D
    view": 30 tipped controls in the toolbar alone, measured headless (Debug,
    `r2r=no`, on the cheap `OverlayPopupHost`) at **4.0-8.7 ms and 391 KB per
    open/close cycle**.
  - **`Platform/ToolTipHoverPolicy` closes it app-wide**, from `App.Initialize`
    and before any markup is parsed: one subscription to `ToolTip.TipProperty`
    (the same signal Avalonia's service keys on) writes
    `ToolTip.BetweenShowDelay = -1` on every control that gains a tip. **The
    value must stay NEGATIVE — `0` does not work**, because `closedPreviousTip`
    short-circuits the time comparison. The property does not inherit and its
    default cannot be overridden (`RegisterAttached<ToolTip, Control, int>`
    already claims the metadata for `Control`, so
    `OverrideDefaultValue<Control>` throws), which is why it is a subscription
    and not a setter at ~575 markup sites. Guards: `UI/ToolTipHoverPolicyTests`
    (default gate — the pure decision model, the guard, and a
    comments-stripped lint that `App.Initialize` applies it before
    `AvaloniaXamlLoader.Load`) and `Perf/ToolTipChurnUiPerfSmokeTests` (the
    real toolbar / menu bar / status rail).
    **A/B it in the real app with `DOTCHARTER_NO_TOOLTIP_HOVER_GUARD=1`**, same
    family as `DOTCHARTER_NO_SURFACE_PREWARM` — hover a toolbar button until the
    tooltip shows, then sweep left/right across the rail.
  - **This cannot be reproduced under `Avalonia.Headless`**: that platform has
    no popup impl, so every popup falls back to the in-window
    `OverlayPopupHost`, and the open tooltip's own overlay is then what the next
    hit test lands on. 180 real `MouseMove`s over the toolbar produce 2 opens
    headless where the windowed path produces one per control crossed. Assert
    the property, not the storm.
  - **"Why not just host every popup in-window and be done?" — it IS
    in-window since 0.49.3** (`OverlayPopups = true` in `Program.cs`; the 3D
    preview is composed-only). That removed the per-open popup HWND, not this defect: an
    overlay-hosted tooltip has its own measured problems. Headless IS that world, so it is
    measurable, and there: (a) an overlay-hosted tooltip does not track a
    sweep — hovering seven tipped controls opened the first and then **2 of the
    next 6**, because the tip's own overlay swallows the next hit test, so the
    service alternately drops and re-latches it; (b) while one is open each
    pointer move cost **~5 ms and ~230 KB** net of the harness floor, since the
    overlay layer moves that work into the WINDOW's layout — the same UI thread
    the timeline's `RequestAnimationFrame` is queued on. So the delay guard
    stays, overlay or not.
  - The same shape on `ToolbarView`'s undo/redo **history preview flyouts** is
    closed (#300): `HistoryButton_PointerEntered` only ARMS
    `Views/Shell/HoverDwell` and a 350 ms `HistoryPreviewDwell` timer opens the
    preview if the pointer is still there, so a sweep opens nothing. With one
    preview already open, moving to the other button switches at once.
    `UI/HistoryPreviewDwellTests` (pure rule + a lint that the handler never
    shows a flyout itself).

---

