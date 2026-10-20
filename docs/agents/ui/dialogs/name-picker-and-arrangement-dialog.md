# Dialogs (`Views/Dialogs/`) — UI deep reference — a name field is a picker; the arrangement dialog

## A name field is a PICKER, and whether it is closed depends on the vocabulary

- **Neither an `AutoCompleteBox` nor a bare `TextBox`.** An `AutoCompleteBox`
  only opens its list once something has been typed, so it reads as a plain text
  box and its options are undiscoverable — the event dialog's SECTION field
  shipped that way in 0.39.0 and the owner reported the picker as never having
  been ported (fixed 0.39.2). A bare `TextBox` is worse: the event dialog's TONE
  field was one until 0.57, so the key had to be typed from memory, nothing said
  what the chart defined, and a typo produced a marker that validated as "no
  matching tone" and silently never applied. Use a `ComboBox`; its chevron is
  what makes the list discoverable.
- **Closed vs editable is decided by the VOCABULARY, not by taste.** Sections are
  a CLOSED catalogue — `RsSectionCatalog`, and `getSectionUIName` has no
  fallback, so there is no such thing as a valid custom section name; an off-list
  name an import left behind is appended as a trailing entry rather than
  rewritten. Tone names are OPEN: naming a tone before authoring it is a
  legitimate order of work and imports carry arbitrary keys, so that field is
  `IsEditable="True"` with `Text="{Binding …, Mode=TwoWay}"` (a one-way `Text`
  binding on an editable `ComboBox` reads perfectly and saves nothing typed).
- **An open vocabulary earns an inline WARNING, never a refusal.**
  `SectionNameIsUnknown` and `ToneHasNoBody` are the two, each a `DlgDesc` row
  under its field. The tone one exists because "the chart has no body for this
  name" is otherwise completely silent: the marker is placed, it exports, and it
  plays nothing. That was the state of every psarc-opened chart before the loader
  imported tone bodies, and the editor never said a word.
- **What the chart actually HAS is answered by `Core/Utilities/ChartToneNames`,
  never re-spelled at the call site.** A tone reaches the rig only when the
  arrangement DECLARES it (`Declared`) and the project owns a BODY for it
  (`HasBody`) — two collections edited in two places whose disagreement fails
  silently. `Offerable` is what a picker shows. Matching is trimmed and
  case-insensitive over key AND name, because that is exactly how
  `RsTonePlaybackCoordinator` resolves a `ToneChange.ToneName` at playback;
  anything stricter reports a tone as missing that the rig then plays. Pinned by
  `Core/Utilities/ChartToneNamesTests` and the two tone lints in
  `UI/EventDialogFidelityTests`.

## The arrangement dialog — the one place the Rocksmith-facing fields are authored

- **The arrangement dialog (`Presentation/Screens/Dialogs/ArrangementSettingsViewModel`
  on the dialog host, over `Services/Editing/Arrangements/ArrangementSettingsEdit`) is the ONLY place the Rocksmith-facing arrangement fields are authored**
  (0.35): the per-chart scroll speed (`Arrangement.ScrollSpeed` — absent is
  "no preference", deliberately NOT 1.3, so the checkbox and the number are
  separate state), the path/route, picked bass, and the technique flags. The
  flags are split the way `ArrangementPropertiesDeriver` splits them and the UI
  must keep saying so: the four **undecidable** ones (`fingerPicking`,
  `twoFingerPicking`, `pickDirection`, `syncopation`) are always editable
  because nothing derives them; the twenty **derived** ones are shown as what
  the export would ship right now and only become editable behind
  `ArrangementProperties.ManualOverride` — which seeds from the derived answer,
  so ticking the pin is non-destructive (DLC Builder's "Edit" behaviour). With
  the pin OFF the dialog must NOT write those twenty back, or every save looks
  authored and the `.rscp` grows attributes the next export recomputes anyway.
  Rows are `ArrangementSettingsEdit.DerivedFlags` / `AuthoredFlags` accessor pairs, not 24
  hand-written properties — the same reason `RscpArrangementPropertyMap` is one
  table.

