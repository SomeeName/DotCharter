# Fire — the Play Mode modals (§6.5 settings, §6.6 Get Ready; §6.7 LAN removed)

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

**The Play Mode SETTINGS MODAL is on the layer (§6.5, 0.48)** —
`GameplaySettingsOverlay` / `GameplaySettingsOverlayCard` in `Screens/GameplaySettingsView.axaml`
(each section pane its own `GameSettings*View`),
built from `Settings.dc.html`. A **1020 × 660** `Border.FireScreen` of three
regions that meet hard edges: a 230 px `Border.FireRail`, the content pane, and
a `Border.FireFooter`. Six sections — Audio · Devices · Gameplay · Graphics ·
Appearance · Keybinds — over ~57 rows. Five things it settled:

1. **Every row carries its explanation, and that is enforced from BOTH ends.**
   `ViewModels/PlayMode/GameplaySettingsCatalog` is the row catalogue and its
   `GameplaySettingsEntry` takes `DescriptionKey` as a REQUIRED constructor
   argument — there is no overload that omits it. The markup half is
   `UI/GameplaySettingsModalTests.EveryControlInTheModal_SitsInARowThatExplainsItself`,
   which walks every `ToggleSwitch`/`ComboBox`/`Slider`/`KeybindCaptureBox` in the
   modal and demands a `FireRowDesc` in its row.
2. **The catalogue and the markup are proven to be ONE list.** The modal's rows
   are inline XAML and its search runs over the catalogue — two sources — so
   `EveryRowInTheModal_IsInTheCatalogue_AndViceVersa` lints both directions AND
   the title↔description pairing. Without it "search matches the real
   catalogue" degrades to "search matches whatever the catalogue said last year".
   (This is why it is a catalogue and not a `SettingsSearchWalker`-style tree
   walk: the editor's eight tab VIEWS can be walked platform-free; this modal was
   one region of `PlayModeView.axaml` when that was decided.)
3. **The scrim is `Border.FireOverlayScrim`, and that is asserted twice** — that
   `FireOverlayFill`'s gradient stops are all `#FF…`, and that the modal really
   paints with it rather than one of the app's translucent `OverlayScrim*`
   brushes. An overlay is SUPPOSED to cover what it covers; the "let the animated
   backdrop through" rule is for SCREENS.
4. **The rail is `RadioButton.FireNavRow`** (added to `Fire.axaml` for this) with
   a **string** `CommandParameter`. Both halves are the documented traps: a
   ToggleButton rail unchecks the active row when it is clicked again, and a
   `RelayCommand<int>` fed a literal `CommandParameter` throws during layout.
5. **Seven gated bodies inside ONE gated root** — so
   `PlayModeView.GameplaySettingsPaneNames` is appended to `HiddenSurfaces()`
   after the overlay root, cross-checked against the markup's `x:Name`s by
   `EveryGatedPane_IsExposedForPrewarm`.

Its live values are real: the input meter is `FireLevelMeter` over
`LivePlayViewModel.ReadInputPeak` (the same capture tap the menu backdrop's EQ
bars read, polled by a 20 Hz `DispatcherTimer` in `PlayModeView.axaml.cs` that
runs only while the modal is open and stops on detach), the latency chip is
`IAudioPlaybackService.MeasuredOutputLatencyMs`, and the Devices section binds
the app's ONE `AudioSettingsViewModel` — the real enumerated WASAPI/ASIO
devices, applied through the real `ApplyAudioConfiguration`.

**GET READY is on the layer (§6.6, 0.48)** — `TuningCheckScreenRoot` in
`Views/PlayMode/Screens/GetReadyView.axaml` (its centre column `GetReadyCentreView`), built from `Get Ready.dc.html`. The densest screen in the
app, and a full-bleed four-region layout rather than the centred 1120 px
scrolling column it was: header / 404 px left rail (CHOOSE YOUR PATH + HOW IT
PLAYS) / flex centre (the tuner full height over the tones + backing cards) /
footer (chamfered START · the quiet actions · the key hints). Seven things it
settled:

1. **The mockup's per-stem identity colours were CUT, and that is the rule
   working.** Guitar green / bass blue / drums orange are literally three of the
   six FIXED STRING identities, so reusing them would reassign an identity;
   minting four new decorative ones would break "colour is semantic". The stem's
   name and its mute state carry the same information.
2. **The string chip reaches its colour by TOKEN KEY, never by brush.**
   `FireStringIdentity.TokenKeyFor(index)` (the `FireUnitState.TokenKeyFor`
   shape) + `Converters/FireTokenBrushConverter`, so the six values still live
   once in `FireTokens.axaml` and no view model holds a colour. **The converter
   must use `TryFindResource`, not `Application.Resources`** — the Fire tokens
   are STYLE resources, and the dictionary lookup returns null for every key.
3. **A guard for a fixed identity must state its expectation INDEPENDENTLY.**
   The runtime chip test first derived its expected key from
   `FireStringIdentity` — so swapping a pair moved both sides together and it
   passed through the exact regression it exists to catch. It carries its own
   ordered literal list now.
4. **Every printed key hint is DATA** (`ViewModels/PlayMode/GetReadyKeys`), read
   by the strip that is drawn AND the handler that runs. Enter and T are claimed
   by `PlayModeView`'s tunnel handler; **Escape is deliberately NOT** — the app
   has one Escape chain, in the shell, and a second consumer navigates back
   twice. (The shell's chain has exactly one deliberate stand-down since
   2026-08-14: a MARKED search box with text to clear —
   `SearchBoxKeys.ClaimsEscape` via `GlobalChordGate.Context
   .SearchBoxClaimsEscape` — clears its text first; an empty box lets Esc
   through as before. Pinned by `GlobalChordGateTests`.)
5. **The live meter clock is the settings modal's**, widened to this screen —
   one `DispatcherTimer`, one capture tap, stopped on detach.
6. **A tuner ROW is a `Button`, because the screen promises it is (0.58).**
   `Skip check`'s own text says a skipped string is revisited "by tapping it",
   and `GuidedTuningCheckViewModel.SelectStringCommand` was written for exactly
   that — but the row template was a bare `Grid`, so the command had no surface
   raising it and a skipped or confirmed string could only be reached again by
   restarting the whole check. The row is `Button.FireListRow` with
   `BorderThickness="0" Padding="{DynamicResource FirePadNone}"`, the same
   turn-the-chrome-off shape `D6PlanList` uses: the inner Grid keeps its own
   `ColumnDefinitions="46,14,*,14,80"` and margin, so the caption row still
   lines up, and all the button adds is the hand cursor, the hover/press tint
   and the row focus adorner. `Perf/PlayModeScreensUiPerfSmokeTests
   .GetReadyScreen_aTunerRow_IsPressableAndReSelectsThatString` presses the real
   laid-out control and asserts the SELECTION MOVED — a null `Command` is
   invisible to any bounds check — and asserts the row is still exactly as wide
   as the caption row.
7. **Two figures had no backing and were BUILT rather than cut**: the
   per-arrangement note count is counted off the loaded chart's hardest level
   (so own charts and imports have one too, not just packages), and the
   stem-separation estimate is a MEASURED per-machine rate
   (`Services/Audio/StemSeparation/StemSeparationCost` +
   `AppConfig.StemSeparationSecondsPerAudioSecond`, folded on every successful
   run). There is no honest constant for separation cost, so an unmeasured
   machine shows **no estimate at all** rather than a guess.

`GetReadyCentreScroll` gives the tuner and sound cards their natural height
when the viewport is short. The Start footer is outside that scroll region.
`PlayModeScreensUiPerfSmokeTests.GetReady_AllSixStringsStayInsideTheirPanelAtSmallerSizes`
checks all six rows stay inside the tuner, clear of its input meter and the
tone card, at 1920×1000, 1536×800, 1440×760 and 1280×720.

Song Library's header uses `FirePadHeader` as its margin, and its browse bar
uses the shared `FireHeader` padding. The launch profile and guest name rows
put their action buttons in column 1, separate from the name fields in column 0.
`PlayModeScreensUiPerfSmokeTests.SongSelect_ControlsHaveWindowInsets` and
`PlayModeScreensUiPerfSmokeTests.LaunchScreen_profilesPanelExpandsAndMeasures`
check these layouts at 1920×1000 and 1280×720.

Guarded by the default-gate `UI/GetReadyScreenTests` (33 cases: the identities,
the tuner scale, the real check driving the real rows, the key-hint contract,
the measured cost, and source lints for the fixed value columns, the scroll
regions and §3.8's consequence sentences) plus
`Perf/PlayModeScreensUiPerfSmokeTests.GetReadyScreen_*` (six cases: both stem
states, both approximation states, the per-string colour identity read out of
the laid-out tree, the three tuner states, the two claimed key hints as real
presses, and the meter poll starting and stopping with the screen).

**The LAN MULTIPLAYER MODAL (§6.7)** was removed with LAN multiplayer sessions on
2026-09-19; restore from tag `pre-cut-2026-09`. Its `FireChoice.Card`,
`FireSlotDot` and `FireListRow.Unavailable` classes went with it only if nothing
else wears them — check `FireClassReachabilityTests` before reusing one.

