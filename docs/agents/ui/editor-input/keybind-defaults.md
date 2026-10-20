# Editor keyboard input — keybind defaults, and user-created binds

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## Keybind defaults

The whole table lives in `KeybindConfig`; `AppKeybindRegistrar` (plus
`MainWindowViewModel` for Escape) is the only registration surface, and every
property needs a row in `Presentation/Screens/Settings/KeybindRowCatalog.cs` (the
Keybinds tab's rows are data) or it cannot be rebound. `KeybindDefaultsTests` enforces all four:

- **no default ships unbound** — ~25 actions used to, which put a large part of
  the vocabulary out of reach;
- **no two defaults collide** — `ChartKeybindHandler.RefreshBinds` resolves a
  collision last-registration-wins with a log line and no UI, so one of the pair
  is silently dead. The check expands the three digit families
  (`BookmarkBase` +0..9, `SetFretBase` +0..9, `StringMaskBase` and
  `PenToggleStringBase` +1..`StringEditMask.MaxStrings`) and includes the fixed
  `F12`/numpad registrations and the three chords the window tunnel claims
  (`Ctrl+Shift+P`, `F11`, `Alt+Enter`). **The two string families are sized from
  `MaxStrings`, never a literal 9** — hard-coding it would let a bump go
  unnoticed in the one file whose job is to notice;
- **every property is read by a registration site**;
- **every property has a settings row**;
- **a digit family may not leave the digit row**
  (`EveryDigitFamilyStaysInsideTheDigitKeys`). The registrar derives its key with
  `(Key)((int)Key.D0 + i)` on a plain enum, so `Key.D9 + 1` is `Key.A`, not an
  error: raising `MaxStrings` to 10 would silently bind `Ctrl+A` (shadowing
  `SelectAll`) and a BARE `A`. The collision check would then report it under a
  confusing name; this one fails where the message says what happened.

Every menu row and toolbar button whose verb a chord drives SHOWS that chord,
bound to the live `ProjectState.Keybinds` table (rebinds and Settings Cancel
move the label with the key) — `UI/MenuGestureCoverageTests` derives the pairs
from the registrar and fails a row that shows none or a different one;
`Perf/MenuGestureUiPerfSmokeTests` checks the bindings resolve and follow a
rebind.

Conventions in force:

| Row | Family |
|---|---|
| bare / `Shift`+letter | the per-string technique toggles |
| `Alt`+ the single-key chord | that verb's "group" variant (rscharter's Alt-prefixed "independently" pairing). **Where that would land on `Ctrl+Alt`, the group variant drops the Ctrl instead** — `ToggleIgnore` is `Ctrl+Shift+I` and its group is `Shift+Alt+I`, not the AltGr-eating `Ctrl+Shift+Alt+I`; Generate arpeggio (`Shift+Alt+G`) is the same move on Generate hand shape (`Ctrl+Shift+G`) |
| `Ctrl+Shift`+letter | chord-LEVEL flags and structural tools — plus EOF's own `Ctrl+Shift` verbs, which do not follow that reading (`Ctrl+Shift+D`/`X` are double/halve tempo, `menu/beat.c:77,84`) |
| `Shift`+digit | bookmarks |
| `Alt`+digit | set fret 0–9 |
| `Shift+Alt`+digit | chord-template fingering (thumb, 1–4); `Shift+Alt+Backspace` clears |
| `Ctrl`+digit | the string-edit mask |
| bare digit | the pen's string toggle at the playhead (`PenToggleStringBase`) |
| bare `,` / `.` | grid snap coarser / finer (EOF `editor.c:2035,2047`) |
| `Alt+,` / `Alt+.` | zoom out / in — the ALT row of the SAME two keys (EOF `editor.c:2035,2064`) |
| `Alt`+`Home`/`End` | the loop punch points (EOF's "Set start/end point", `menu/edit.c:255-256`) |
| `Ctrl`+`Ins`/`Del` | add / delete a BEAT (EOF `menu/beat.c:144-145`) — one level up from bare `Del`, which deletes a note |
| `F5`–`F7` | playback feedback: waveform, loop, audition |

**Do not add `Ctrl+Alt` bindings.** On a German layout — this project's owner
and its second locale — `Ctrl+Alt` **is** AltGr, so `Ctrl+Alt`+digit/Q/E/M
produce characters. The one pre-existing `Ctrl+Alt` binding (`Ctrl+Alt+R`, snap
all) is left alone.

Changing a shipped default needs a `JsonConfigurationService.NormalizeKeybinds`
migration keyed on the exact OLD default, so a user who deliberately rebound it
is untouched (v0.35 moved `TogglePreview3D` off `Ctrl+3` to free the mask row).

**A migration is a WAVE, and every wave is gated on
`KeybindConfig.MigrationVersion` — add one, bump
`KeybindConfig.CurrentMigrationVersion`, and put the moves in their own
`NormalizeKeybindsWaveN`.** The equality test alone is not enough and was not
until 2026-09-04: the waves ran on EVERY load, so a user who deliberately
rebound a verb ONTO its retired default (Ctrl+I for the Inspector, Ctrl+F for
3D fingering, Ctrl+B to add a beat, bare G for Ignore — each of them the
obvious chord for its verb, which is why it used to BE the default) had that
choice silently reset at every launch, for ever. `LoadKeybinds` persists the
stamp when it advances, which is what makes it once-only; a config with no
stamp reads as 0 and gets every wave once, exactly as before. Pinned by
`JsonConfigurationServiceTests.LoadKeybinds_ARebindOntoARetiredDefault_SurvivesTheNextLaunch`
and `.LoadKeybinds_StampsAnUnmigratedConfigOnDisk`.

### The EOF alignment passes

Owner, 2026-08-22: *"I want the keybinds to generally be just like EOF has so
it's familiar."* Three passes have acted on that — v0.39 (the PgUp/PgDn seek
family), v0.47 (eight verbs onto EOF's chord) and **2026-09-04, the third**.
The source of truth is the local-only reference checkout `EOF_SRC`: the menu
accelerators are the string literals in `EOF_SRC/menu/*.c`, the non-menu keys
are the `key[KEY_*]` handlers in `EOF_SRC/editor.c`. **Never guess an EOF
chord — cite the file and literal.**

`UI/EofKeybindParityTests` is the guard, and it has two halves that fail for
different reasons:

- `EofChords` / `Pass3EofChords` pin every default DotCharter SHARES with EOF
  to the verb EOF spells it for, so a later change that quietly re-homes one is
  a red test rather than lost muscle memory. `Pass3EofChords` rows carry the
  `file:line` they were read from.
- `AChordVacatedForEof_IsNotRefilled` pins the chords a pass deliberately left
  EMPTY because EOF spends them on a verb DotCharter does not have. Refilling
  one looks like tidiness and is drift.

**The list is deliberately of the ones that MATCH, not of every EOF key.** EOF
binds verbs DotCharter has no counterpart for (Toggle Anchor on `A`, Place RS
Phrase on `Shift+P`, move-note-by-grid-snap on `Ctrl+[`/`Ctrl+]`); inventing a
verb to fill the row would be the tail wagging the dog, and a keybind onto
nothing is worse than none.

Two EOF chords are knowingly NOT adopted, so nobody "fixes" them back:

- **Redo stays `Ctrl+Y`**, not EOF's `Ctrl+R` (`menu/edit.c:234`). `Ctrl+Y` is
  the Windows-wide redo; EOF is an Allegro/DOS-lineage program with no such
  platform convention to answer to.
- **Accent stays `Shift+A`**, not EOF's `Ctrl+Shift+A` (`menu/note.c:719`).
  `Shift+A` is EOF's *Anchor Beat* (`menu/beat.c:147`), a verb DotCharter does
  not have, so the chord is not contested — and moving accent would pull one
  member out of the Shift-row technique family whose other four
  (`Shift+X` mute, `Shift+V` vibrato, `Shift+N` linknext, `Shift+H` harmonic)
  are all EOF-exact, then force its group variant onto `Ctrl+Shift+Alt+A` —
  AltGr+Shift on a German layout, which the rule above forbids.

### Keybinds the USER creates — `RegisterDynamic`

Every rule above assumes the set of actions is fixed at compile time. The match
patterns (`Editing/Selection/Patterns/`, see `DotCharter.Services/AGENTS.md`)
break that assumption: the user authors them at runtime, each carries up to
three chords (select / next / previous), and there is no upper bound on how many
exist. `IKeybindRegistry.RegisterDynamic(Func<IEnumerable<KeybindRegistration>>)`
is the seam — a registration that produces a SET, re-enumerated on every
`RefreshBinds`. `AppKeybindRegistrar.BuildPatternBindings` is its only caller.

Four things about it that are load-bearing:

- **`RegisterDynamic` is for runtime-authored bindings only.** Anything with a
  fixed identity is a `KeybindConfig` property and obeys the four
  `KeybindDefaultsTests` guards; a dynamic bind has none of them, which is
  exactly why it may not be used for shipped actions.
- **The action resolves its pattern by ID at PRESS time, not registration time**
  (`AppKeybindRegistrar.RunPattern`). The binding is a pointer at stored data, so
  editing a pattern's facets takes effect immediately rather than at the next
  restart.
- **`KeybindRegistration.Label` is not decoration.** `DescribeBinding(chord)`
  reports it, and the Pattern Builder uses that to warn before a chord is
  assigned — without it a new pattern silently overrides a shipped editing key
  and the only evidence is a log line. The pattern labels are
  `pattern:{id}:{verb}` because the dialog filters its OWN binds out by that
  prefix; change the format and every saved pattern reports a conflict with
  itself.
- **The builder's Save must call `RefreshBinds()`.** The dialog would otherwise
  appear to work while the keys stayed dead.

`KeybindConfig.PatternBuilder` (`Ctrl+L`) opens the dialog and is an ordinary
static binding with all four guards.

