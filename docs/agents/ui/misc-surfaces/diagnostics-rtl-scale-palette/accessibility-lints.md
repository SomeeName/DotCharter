# Session Health, accessibility, RTL, global UI scale and the command palette

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../../misc-surfaces.md).

## Accessibility: two lint classes, and what each one means

Both live in the DEFAULT gate (`DotCharter.Tests/UI/`), both are pure source
lints over `DotCharter.UI/Views/**`, and both were proved by breaking a real
view and watching the run fail. `AccessibleNameTests` carries two rules: icon
buttons, and value controls.

### `AccessibleNameTests` — an icon button must be NAMEABLE

- **Every `Button`/`ToggleButton`/`RadioButton`/`RepeatButton`/`SplitButton`/
  `DropDownButton`/`CheckBox`/`ListBoxItem` whose face is a `PathIcon`, a bare
  glyph or nothing at all carries an
  `AutomationProperties.Name`.** Without one it reaches a screen reader — and
  every automation tool, and any future UI-automation test — as `Button ""`.
  Nothing warns, nothing throws, it renders perfectly: the same silent shape as
  every other trap in [../layout-traps.md](../../layout-traps.md).
- **A `ToolTip.Tip` is NOT a name, and that is the load-bearing half of the
  lint.** A tooltip is announced on HOVER, the one gesture the user who needs
  the name does not have. **35 of the 37 controls the lint found on its first
  run already had one** — accepting tooltips would have made the whole guard
  vacuous on day one. The fix is to carry BOTH, off the same resource key; that
  is what all 36 fixed sites do.
- **A GLYPH is not text.** `"—"`, `"+"`, `"✕"`, `"★"`, `"››"` and
  `Content="{DynamicResource IconClose}"` all name nothing, so the bar is
  "contains a letter or a digit", not "is not empty". Fifteen of the 37 were
  glyph-faced buttons that a `Content=` check alone would have passed.
- Scope stops at `Views/`: a button inside a `Style`/`ControlTheme`/`Setter` is
  a TEMPLATE body and is named by whatever the template is applied to, so the
  walk does not descend into one.
- **Value controls are NOT in the icon-button lint — they have a lint of their
  own, and since 2026-08-15 it covers the WHOLE of `Views/`.**
  `EveryRowInputControl_CarriesAnAccessibleName` (same file) walks every
  `.axaml` under `DotCharter.UI/Views` and requires an
  `AutomationProperties.Name` or `LabeledBy` on each one. **Measured
  2026-09-04: 411 such controls, 0 unnamed, exemption list EMPTY**, and
  `EveryRowInputExemption_NamesAFileThatStillNeedsIt` deletes any entry the
  moment its file is clean, so the list can only shrink. Floors (>80 views
  scanned, >380 controls inspected) stop it passing by finding nothing.
  - **It knew only three control TYPES until 0.61, and that was the same hole
    one axis over.** `ComboBox`/`Slider`/`ToggleSwitch` were the whole list, so
    it read 0 unnamed of 256 and ran green over 34 files that between them held
    **98** unnamed value controls it could not see: **50 `TextBox`, 23
    `ListBox`, 18 `NumericUpDown`, 5 `AutoCompleteBox`, 2 `ColorPicker`** (the
    editor design pass counted ~195 by including CheckBoxes and RadioButtons
    that DO carry a caption). It now also walks `TextBox`, `NumericUpDown`,
    `MaskedTextBox`, `AutoCompleteBox`, `ListBox`, `DatePicker`, `TimePicker`,
    `CalendarDatePicker`, `ColorPicker` and `ColorView`, and `CheckBox` +
    `ListBoxItem` moved into the icon-button fact — where a caption on the
    control's own face clears the bar, which is why 75 CheckBoxes yielded only
    four findings. **Add a new input type to `RowInputControls` when you first
    use one.**
  - **A search box with no label row is named by its `PlaceholderText` key, and
    only because that watermark is a Loc key** in every such box (the palette,
    Settings, both song lists, the phrase catalogue): the name
    is then the same authored, translated string the sighted user reads in the
    empty field. The one box whose placeholder is a hard-coded example URL —
    `StemModelsSettingsView`'s custom-model field — is named off its section
    title instead. A READ-ONLY readout is not exempt either: the chart-diff
    dialog's compared-file box takes the `Dialog_Section_Source` key its caption
    already uses.
  - Rows that had no label of ANY kind got new keys in all three resx
    files: `CommandPalette_ResultList`, a Songsterr result list (deleted with the import, #820),
    `Recovery_SessionList`, `Lan_SongBoard_List`, `Settings_AsioInChannelRight` — the
    last because two boxes sharing one row label would announce identically.
    (The output channel boxes it also named are now one pair picker.)
  - **This was `Views/Settings` only until then, and the rest was a BACKLOG IN
    PROSE — which is how it reached 125 controls.** That is the number
    re-measured on 2026-08-15 (this file said 136 on 2026-08-11 and several had
    been named since; measure, never carry forward). 56 were in
    `PlayModeView.axaml` alone, the rest spread over 26 files.
  - **The name is the row's own label, off the SAME key** — never an invented
    English literal. Three shapes recur: a resource key where the row has a
    `FireRowTitle`/`DlgLabel`/`SettingTitle`; the same key a `ToolTip.Tip`
    already uses where that is the only text; and `{Binding Label}` /
    `{Binding DisplayName}` inside a `DataTemplate`, where the visible label is
    DATA — that names each instance distinctly, which one shared key would not.
    Seven controls had no label of any kind and got new keys in both resx files
    (`Dialog_Tones_Descriptor1..3`, `Dialog_GpImport_TargetArrangement`,
    `ChordTemplate_Name`, `Online_MetricLabel`, `Nam_GearFilter`,
    `Nam_ArchFilter`).
  - **Two mutually exclusive controls on one row share one key** (the
    WASAPI/ASIO device pickers — exactly one is visible per driver); controls
    visible AT THE SAME TIME never do.
  - **The Amp & FX rig additionally carries a screen-scoped lint**,
    `UI/AmpFxRigScreenTests.EveryValueControlAndMeterOnTheRig_CarriesAnAccessibleName`
    (2026-08-15) — it also covers `TextBox`/`ListBox` **and the two
    custom-drawn meters** (`FireLevelMeter`, `FireTunerMeter`), which no
    repo-wide lint looks at: a custom-drawn `Control` has no content a peer
    could fall back on, and the "IN"/"TUNER" eyebrow beside each is a SIBLING
    `TextBlock`, which names nothing. `FireKnob` names itself from its `Label`
    (see [../fire/custom-controls.md](../../fire/custom-controls.md)).
- The icon-button exemption list is EMPTY (2026-08-11 — the last entry,
  the settings shell's search-clear button, is named off `Action_Clear` now).
  `EveryExemption_NamesAFileThatStillNeedsIt` deletes any entry the moment it
  stops being true.

### `ModalEscapeHatchTests` — a modal must have a way OUT

- **Every element carrying a `*Scrim` class (`DialogScrim`, `FireOverlayScrim`)
  makes the surface a modal, and that surface must contain a focusable control
  that dismisses it** — a `Command` naming Close/Cancel/Dismiss/Back/Done, a
  `Button.FireOverlayClose`, or `IsCancel="True"` so Escape fires it. 45
  scrimmed surfaces, 0 traps.
- **Clicking the scrim does not count.** Six dialogs dismiss on
  `PointerPressed="OnScrimPressed"`; that is a pointer gesture, so a scrim
  handler is what MAKES a surface a modal here, never what discharges it.
- This is the Fire vocabulary's own overlay rule
  ([../fire/vocabulary.md](../../fire/vocabulary.md) — "a layer with no visible way
  out is a trap") finally checked, and generalised: the ~39 `DialogScrim`
  dialogs are the same shape and the same trap.

### Colour is never the only signal

The one case this pass found and fixed: the vocal dialog's four lyric-colour
swatches were filled rectangles with no text, no icon and no name, so the colour
was both the state AND the identity. `LyricColorSwatch` now carries a localized
`Name` (`Dialog_LyricColor_White`/`_Purple`/`_Green`/`_Amber`) that feeds both
`AutomationProperties.Name` and a tooltip; selection already had a non-colour
signal (the `SwatchRing` child), which is the pattern to copy.

### What this pass looked at and did NOT act on

- **`Controls/EqPlotControl` and `Controls/LivePlaySectionGraph` take pointer
  input and are neither `Focusable` nor `OnKeyDown`-handling** — unlike
  `ChordBoxControl` and `FireKnob`, which are both. Neither is the ONLY way to
  do its job (the EQ has numeric rows beside the plot; seeking and looping have
  keybinds), so they are a gap, not a block. Making a custom-drawn control
  keyboard-operable is a feature, not a sweep.
- **RTL needs no hard-coded-inset hunt.** Avalonia mirrors RTL with a transform
  rather than swapping per-side margins the way WPF does, so
  `Margin="8,0,0,0"` and `HorizontalAlignment="Left"` are not the fault class
  they would be there. The manual pack check below is still the only real
  verification.

