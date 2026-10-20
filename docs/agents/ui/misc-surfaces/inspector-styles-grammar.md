# `InspectorStyles.axaml` — the FOURTH converted grammar

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../misc-surfaces.md).


- **`Views/Editor/InspectorStyles.axaml` is the FOURTH converted grammar (0.50)** — the same Fire-ladders-only lint as `DialogStyles`/`SettingsStyles`/`ShellChromeStyles`, and re-pointing it converted all eight tabs at once. It is included by `InspectorView`, `MixerInspectorView`, `PhraseCatalogView` and `StemMixerView`. Two differences from the other three: it may carry a **`Cursor`** setter (nothing constructs an inspector view platform-free — the settings-search ban does not reach here, and `FireStyleClassTests` asserts BOTH halves so the difference stays deliberate), and it deliberately **restates the app classes this pane leans on** (`Button.Subtle`, `ToggleButton.Seg`/`Chip`, `TextBlock.Label`/`Caption`/`HelpText`, `PathIcon.SectionIcon`, `Border.PaneHeader`) because ~150 controls in these tabs wear them. The tab strip is the shared `ToggleButton.FireSegmentItem` component; its captions go through the new `{localization:Loc Key, Upper=True}` flag on `LocExtension`, because the same keys also title the tab they open.
- **The Mixer tab's EQ is its OWN view (0.56):
  `Views/Editor/Inspector/EqModuleView` +
  `ViewModels/Editor/Inspector/EqModuleViewModel`, over the six-band ABI v29
  section.** It replaced a bound `Polyline`, a high-pass badge and six freq/Q
  sliders driving the three FIXED filters; those filters still exist on
  `IAudioPlaybackService` and still run in the engine, but the inspector no
  longer surfaces them — showing both would give the user two EQs over one
  signal with no way to tell which one they were hearing. Five things are
  load-bearing:
  - **It is a separate VIEW, not more markup inside `MixerInspectorView`** —
    the v0.43 pedalboard rule. Markup nested in another screen is not a type
    anything can name, so it could not be rendered standalone and the
    width/height sweep that verifies it would not exist.
  - **`EqSection` is the single source of truth.** The engine push, the drawn
    curve, the handles, the chip row and the "n/6" badge all read ONE object;
    `Controls/EqPlotControl` binds that section directly (never a projection)
    and owns no band state and no maths — every mapping goes through the pure
    `EqCurve`, which is the only reason the DRAG VERB has tests at all.
  - **The badge's number comes from the ENGINE**
    (`IAudioPlaybackService.EngineEqActiveCount`), never from a managed count —
    and −1 there means "no engine has been given this section", which is NOT
    the same as the engine reporting zero. See `DotCharter.Services/AGENTS.md`.
  - **`IsLive` is the honesty gate, and it CHANGES.** With no parametric EQ in
    the engine every control is disabled, the reason is PRINTED, and nothing is
    ever pushed. The mockup's expand-to-advanced-editor affordance is
    deliberately ABSENT for the same reason: that editor does not exist, and a
    button that opens nothing is the control that lies.
    `UI/EqModuleReachabilityTests` lints all of it — including that no resx key
    promises one. **The engine is built lazily and this view model is
    constructed with the transport, before the window exists**, so on a cold
    start `IsLive` is false here and true a second later;
    `EqModuleViewModel.RefreshLiveState` (called from the analyser's poll tick)
    is the one place that flip is noticed, and it PUSHES on the transition —
    without which the first engine of the session runs an EQ it was never told
    about.
  - **The analyser falls back to a MARKED preview.** It used to draw nothing
    without a live frame, so a stopped transport gave a bare grid and the zone
    tints and string ticks had nothing to sit on. `EqSpectrum.Preview` is the
    mockup's own average curve, drawn dimmer and TAGGED, and
    `EqPlotControl.SpectrumKind` makes which one is up a value rather than an
    inference. Honesty lives in the marking, not in the absence.
  - **HEIGHT: the plot has to ASK for one.** A ScrollViewer measures its
    content with INFINITE height, so the plot's Grid `*` row is worth exactly
    the plot's DESIRED size — a star row there is not "the flexible one", it is
    "whatever the child asked for". Asking for nothing collapsed the row to the
    well's `MinHeight` and shipped a 58 px curve against the mockup's 104
    ("so squished together in height that it isn't even readable").
    `EqPlotControl.DefaultPreferredHeight` (102 px inside the well's 1 px
    frame) is that ask, and it is a preference rather than a floor — a taller
    pane still grows the curve, and below the module's natural height the
    `ScrollViewer` scrolls (without it a 250 px dock drew the plot straight
    through the header and the chip row, and every bounds assertion passed).
    `Perf/EqModulePixelUiPerfSmokeTests` renders 420 / 600 / 800 / 1200 wide at
    the module's NATURAL height — the host the product actually has — plus a
    250 px constrained case and the four states no mockup draws (no engine,
    silent, bypassed, gainless band), and writes a contact sheet to compare
    against `EQ and Filters.dc.html`. **A fixed-height capture is worthless
    here**: it shows the plot absorbing the leftover space, which is the one
    size the real inspector never renders.
  - **Double-click-to-reset is `Controls/ResetOnDoubleClick`, and every slider
    in the inspector declares one.** It used to be a code-behind
    `DoubleTapped` handler on `MixerInspectorView` keyed off the slider's NAME
    — which worked, and which the EQ module (its own view, added later) and the
    stem faders simply never got. That is the shape a per-view handler
    produces. Two sinks: `DefaultValue` (a number the view model owns — 0 dB,
    Butterworth Q, the band type's parked frequency, unity stem gain) and
    `Command` + `CommandParameter` (defaulting to the control's `Name`, so the
    mixer's thirteen defaults stay in one switch on `TransportViewModel`).
    **`DoubleTapped` really does survive `Thumb.OnPointerPressed` marking the
    press handled** — `Gestures` runs off `RouteFinished` — and that was
    measured headless on the STYLED control, on the thumb and on the track,
    before the behaviour was written. Guards: the source lint
    `UI/EqModuleReachabilityTests.EverySliderInTheInspector_DeclaresWhatADoubleClickResetsItTo`
    (default gate; it found the stem fader) plus
    `Perf/ResetOnDoubleClickUiPerfSmokeTests`, which synthesises the clicks.


