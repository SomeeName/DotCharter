# Shell chrome — menu bar, toolbar, status rail, popovers (UI deep reference) — the popover shell (FirePopover)

## The popover shell (`Controls/FirePopover`)

- **The popover shell** (`Controls/FirePopover`) — the ONE chrome every
  dropdown that hangs off the transport bar or the status rail is drawn on
  (`Play Menus.dc.html`, 0.54). Its SHELL RULES are the spec and are pinned by
  `Perf/PopoverShellUiPerfSmokeTests`: **head 40 px · rows 34 px · radius 14/9
  · label left, control right, value in mono · never wider than 420 px**
  (`FirePopoverHeadHeight` / `FirePopoverRowHeight` / `FirePopoverMaxWidth`,
  `FireRadiusCardLg` / `FireRadiusField`). Three things to know:
  - **The template is TYPE-KEYED in `Styles/ControlThemes.axaml`**, not in a
    view's `UserControl.Styles`. A popover is realized inside a
    `FlyoutPresenter`, i.e. in a popup's own namescope — the one place the
    `KeybindCaptureBox` fault (0.39.1) is guaranteed to reproduce.
  - **The BODY grammar (`FirePopover*` classes) is in `Styles/Fire.axaml`, not
    in `ShellChromeStyles.axaml`.** The profile switcher is a bare
    `DataTemplate` shared by the status rail AND the Play Mode front door, so
    anything defined in the editor's chrome grammar silently matches nothing
    in the second host. App-level marks only, in that file.
  - **`FlyoutPresenter` is now a BARE host on these rails** (zero padding, no
    border): the popover draws its own head, hairlines, footer and 14 px edge,
    so a presenter that kept the card inset framed that shell in a second,
    differently-rounded one.
  - Its ground is the mockup's gradient (`FirePopoverFill`), not the flat
    `FireSurfaceMenu` a dropdown panel uses; the head wash is
    `FirePopoverHeadFill` (.07, a step under the hover tint — a 40 px band at
    .13 reads as a selected row). **An Avalonia `Border` cannot draw a dashed
    edge**, so the empty state's dashed outline is a sibling
    `Rectangle.FirePopoverDash` in the same `Panel`.
  - **A slider thumb is clipped by THREE nested boxes, and un-clipping one
    just reveals the next.** Avalonia sets `ClipToBounds="True"` on the
    `Thumb`, on the `Slider` itself, and on the `DataValidationErrors` its
    template wraps the content in — the last two are exactly the slider's own
    height, so a 14 px thumb painting 38 px (its drop shadow, and the
    1.15/1.25 hover/press scales) was cut on every slider in the app,
    including the stem mixer's vertical faders. Fixing only the Thumb changed
    nothing visible and shipped. **All three setters live together in
    `Styles/Inputs.axaml`, as ONE `Slider` type rule** — the four thumb
    templates (`Slider`, `Slider.MixerSlider` view-scoped in
    `InspectorStyles.axaml`, `Slider.FireSlider`, `Slider.FirePopoverSlider`)
    are all Sliders, so one rule reaches every one of them and four copies
    would drift apart. The card/presenter clips ABOVE the slider are
    legitimate and stay. It is NOT more row height and NOT a smaller thumb: a
    shadow is meant to fall outside its box, and 14 px is what the mockup
    draws.
    - **A comma-joined selector mixing a plain and a `/template/` part
      (`Slider, Slider /template/ Foo`) applies to NEITHER.** Split it into
      two Styles. It fails silently, exactly like the popup-chrome rules.
    - `Perf/SliderThumbHeadroomUiPerfSmokeTests` walks to the ROOT (excluding
      any clip bigger than the paint, so a new host needs no list entry) and
      DERIVES its cases from the style files. Both halves matter: its first
      version stopped at the Slider and used a hand list, so it passed while
      every mixer row drew a partial arc. It also may not assign the
      CROSS-AXIS size — `Height = NaN` is a local value that beats the style's
      own height, so the tight box never exists and the bug cannot reproduce.
  - **Every popover is its OWN VIEW under `Views/Shell/Popovers/`** (0.54) —
    `Mix`, `Tempo`, `Tools`, `Tracks`, `Undo`, `Redo`, `Profile`, and since
    0.59 `Overflow` (P2-16): the toolbar's » panel, hosted off
    `ToolbarOverflowButton`, which is VISIBLE only while the core rail's
    ScrollViewer extent exceeds its viewport (`ToolbarView.RailOverflows`,
    pinned by `UI/ToolbarOverflowTests`) — on a narrow window Save / Zoom /
    the grouped flyouts used to scroll out of reach with no affordance. Its
    rows are SECOND DOORS onto the rail's own commands; the four group rows
    route through `MainWindowViewModel.RequestPopover` (`TransportPopover`
    gained `Tempo` for it, chordless), and `ToolbarView.OnPopoverRequested`
    closes the overflow before opening the target so two popups never stack. That is the
    v0.43 pedalboard lesson applied: markup nested inside `ToolbarView` was
    not a type anything could name, so it could not be hosted twice, could not
    be constructed by a test, and **could not be rendered to a picture and
    compared against the drawing it came from** — which is exactly how a first
    pass shipped that measured perfectly and did not look like the mockup.
    `ToolbarView` hosts one element per flyout; `ProfileSwitcherFlyout.axaml`
    is now a one-line `DataTemplate` over `ProfilePopoverView`, so the status
    rail and the Play Mode front door share a real type.
    - **A popover card is a FIXED width, so every cell inside it is too — and
      German/Japanese do not fit an English division of it** (2026-08-28). Two
      shipped that way: `OverflowPopoverView`'s Edit group was three-across at
      the card's 320, i.e. 83 px a cell, and `"Projekt speichern"` wants 97.4
      (`"プロジェクトを保存"` 89.7) — it is 2 + 1 now, the grammar the Groups
      section below it already used, because `Button.FirePopoverGridAction` is a
      FIXED 32 px high and a label cannot wrap its way out of one. And
      `TracksPopoverView`'s vocals `ComboBox` was 150 px, leaving its
      PLACEHOLDER a 98 px slot for a 114.9 px `"Keine Gesangsspur"` — a ComboBox
      placeholder has no ellipsis of its own, so it was simply sliced; the card
      is 376 and the label beside it is one word, so it is 188 px now.
      **Do not reach for a template-wide `TextTrimming` on the placeholder** —
      the sweep excludes trimmed text by design, so that would silence the check
      for every ComboBox in the app.
    - **`Perf/PopoverPixelUiPerfSmokeTests` renders each one to a PNG through
      a real `FlyoutPresenter`** — the host the app uses. It set
      `Window.Content = popover` until 0.54, and a capture from a SURROGATE
      host proves nothing about the real one: that is how it stayed green
      while the product clipped a thumb, and how Tempo rendering 414 px wide
      instead of 344 went unseen (its wrapping paragraphs were choosing the
      card width, and the plain window clamped it). **Every popover states its
      exact drawn width now**; the 420 ceiling stays as the shell rule.
      Rendered at the mockup's width on the mockup's `#08070e` ground, under the sweep's own
      `DOTCHARTER_UI_SWEEP_DUMP=1` switch (`TestResults/popover-pixels/`).
      Without the variable the pictures are skipped and the assertions still
      run — construct, attach, lay out, and the mockup's exact width and 40 px
      head. **Use it. The comparison loop is the point**: colour, gradient,
      tracking and "a 26 px chip where a 34 px select belongs" all measure
      fine.
    - Its data comes from `UiSweepDataContexts.Build` **per surface, not
      once**: the app's own startup replaces the project on a later dispatcher
      turn and the transport's VST scan restores the empty persisted chain, so
      a single up-front seeding is silently undone before the second capture.
  - **Everything the mockup draws is now WIRED, and each has a test in the
    default gate** (`UI/PopoverFeatureTests`): the Mix input-level meter
    (`TransportViewModel.PollInputMeter` over `ReadInputCaptureWindow`, the
    multi-consumer tap, clocked by `MixPopoverView` itself), the Tools `IN TUNE`
    readout (`Presentation/Shared/TunerReadout.Begin`, bound as
    `ToneRig.TunerReadout` off `ViewModels/Shell/ToneRigViewModel`; a lease on the shared
    `Services/Audio/Tuning/LiveTuner` — leases are reference counted, because
    the popover and the Tuner dialog can both want the poll),
    "Rescan plugin folders" (`RescanVstPluginsCommand`), drag-to-reorder
    (`VstChainDragController` → `IVstRack.MoveSlot` →
    `TransportViewModel.MoveGuitarSlot`) and
    Tracks' Solo (`Editing/Tracks/TimelineSolo`, a reversible timeline solo
    over the ghost + stacked services).
    - **Tracks carries the editor's ONLY difficulty-level control (#395):** the
      `LevelRow` (N / M, Easier, Harder, Hardest), typed to
      `ViewModels/Editor/DifficultyLevelSelector` (`ChartEditorViewModel.Levels`).
      The toolbar's Tracks button shows "Level N/M" while the editor is below the
      hardest level, because a Go to (Inspector, Chart quality report) can land
      there. Pinned by `DifficultyLevelSelectorTests` and
      `Perf/TracksPopoverLevelRowUiPerfSmokeTests`.
    - **One deliberate honesty call, and it may not be "fixed" by drawing
      it.** Tempo's "Keep pitch when slowing down" is a DISABLED On with the
      reason printed under it: playback speed goes to `dca_source_set_tempo` →
      `RubberBandAudioSource::setTimeRatio`, which never touches the pitch
      scale, so pitch is preserved at every speed UNCONDITIONALLY and there is
      no varispeed source or ABI export that would turn it off.
    - **Every footer cap is LIVE, because the chords behind them are real**
      (0.54). `ALT+M`/`ALT+T`/`ALT+A` are `KeybindConfig.OpenMixPopover` /
      `OpenToolsPopover` / `OpenTracksPopover` — rebindable, with their
      Settings rows AND their command-palette entries (`CommandPaletteCoverage
      Tests` fails a rebindable action `Ctrl+Shift+P` cannot find) — routed
      `MainWindowViewModel.RequestPopover` → `PopoverRequested` →
      `ToolbarView`, which owns the buttons the flyouts hang off. They needed
      no `NormalizeKeybinds` migration (NEW rows; a migration moves a binding
      whose DEFAULT changed) and collide with nothing, but they DO sit beside
      the "Alt+&lt;letter&gt; is the group technique family" convention — a
      deliberate exception, written down at the declaration. Tempo's cap is
      the pair of REAL speed keys (`-` / `=`), not the mockup's `[  ]`: this
      app binds the brackets to sustain and zoom.
    - **A chord bound to a TEXT target must go through
      `KeyChordToGestureConverter`, which hands back `KeyChord.ToString()`
      rather than the `KeyGesture`** — a gesture prints Avalonia's ENUM NAME,
      so a footer read `OemMinus OemPlus` where the keyboard says `-` and `=`.
    - **Tracks' `Mute` is the MIDI instrument bus**, not the arrangement: an
      `Arrangement` carries no volume, mute, solo or bus anywhere in this app,
      so the chip is bound to the thing that actually sounds those notes and
      its tooltip says so.
  - **The VST3 chain reorders by DRAG on EVERY surface that shows it** — the
    Tools popover, `Views/Shell/AmpFxPanel` and RigLab's Plugins tab — because
    since 2026-09-27 they all host ONE view, the shared plugin rack
    (`DotCharter.Tone.UI/Views/PlayMode/VstRackView`, over `IVstRack`), which
    owns the one `VstChainDragController` (moved beside it into
    `Views/PlayMode/`). The ▲▼ pair and `VstChainSlotViewModel`'s
    `MoveUp`/`MoveDownCommand` are DELETED (they could only move a slot ±1,
    and they cost the row 52 px of its 400, which is why it had no room for
    the mockup's index and state pill). Two gestures over one collection is
    how two surfaces end up disagreeing about the signal path;
    `PopoverFeatureTests.EveryChainSurface_HostsTheOneSharedRack` pins it (and
    that no other view builds a drag controller); the layout of a full two-seat
    chain in all four hosts is `Perf/AmpFxRackUiPerfSmokeTests`.
    The slot's printed number is a WRITTEN `DisplayIndex`, not a derived one:
    an `ObservableCollection.Move` raises no property change on the items it
    moved.
