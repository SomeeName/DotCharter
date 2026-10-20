# AI tone design — the drawer panel and the Settings ▸ AI tab

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).
The SERVICE half (providers, the prompt, the validator, the secret store) is
[../../services/ai.md](../../services/ai.md); the contract both halves were
written against is `DotCharter.Tone/Ai/ToneDesignContracts.cs`.

## What it is

"Design a tone": the player types what they want, a model answers with a
validated `DspTonePreset`, and the four actions under the answer reuse the tone
drawer's OWN seams. **It is not a second door onto the rig** — that is the whole
design, and it is the rule this surface exists to obey.

| Piece | File |
|---|---|
| panel view model core (+ `ToneDesignResultViewModel`, `ToneDesignTurnViewModel`) — shared with RigLab | `DotCharter.Tone.UI/ViewModels/PlayMode/ToneDesignCoreViewModel.cs` |
| DotCharter's panel view model (derives from the core; Match + Sections partials) | `DotCharter.UI/ViewModels/PlayMode/ToneDesignViewModel.cs` |
| panel view core — the prompt, conversation and result, with an `ExtrasTemplate` slot | `DotCharter.Tone.UI/Views/PlayMode/ToneDesignCorePanel.axaml` (+`.cs`) |
| DotCharter's panel view — the core plus the Match / Sections markup in its `ExtrasTemplate` | `DotCharter.UI/Views/PlayMode/ToneDesignPanel.axaml` (+`.cs`) |
| the hook (a property + its construction) | `PresetBrowserViewModel.ToneDesign`, built in that type's ctor |
| the host in markup | `RigTonesView.axaml` (the rig's Tones view, its own file since 2026-09-27), `x:Name="RigToneDesignPanel"`: a RIGHT-HAND COLUMN beside the library, drawn only while `PresetBrowserViewModel.Designer.IsExpanded` (the head's "AI designer" toggle). It was the left column of the one Tones scroller (W4) and row 0 of the drawer grid before that |
| settings view model | `DotCharter.Presentation/Screens/Settings/AiSettingsViewModel.cs` (+ `.Search.cs`, + `MatchModelViewModel.cs`) behind `Contracts/Settings/IAiSettingsScreen.cs` |
| settings tab | `DotCharter.UI/Views/Settings/AiSettingsView.axaml` (+`.cs`) |

## The seams it reuses — and why that is the point

- **Hear it → `PresetBrowserViewModel.ApplyPreset`.** The same method a preset
  CARD click calls: `ToneApplyQueue.ApplyToPlayer(selectedSeat, preset)`, off
  the UI thread, single-flighted per seat, with `OnPresetApplied` re-syncing the
  board. A private apply here would have meant the wrong seat under a multi-seat
  board and a stale card grid — both bugs this drawer has already shipped once.
- **Save as tone → `PresetBrowserViewModel.SaveDesignedTone`**, which is
  `IDspTonePresetService.SaveUser(...)` plus the same `RebuildPickerRows` /
  `SelectUserRail` / `RefreshCards` / select-the-new-card tail `ConfirmSave` has.
  The tone lands in My Tones with `DspToneOrigin.User` and is VISIBLY in the
  library, not merely on disk.
- **Discard → `PresetBrowserViewModel.SnapshotLiveTone`**, re-applied through the
  same queue. **The snapshot is USER-origin**, for the reason the A/B compare
  slots are: the factory apply path loudness-normalizes, and a restore that comes
  back re-levelled has not restored what the player was hearing. It is taken
  ONCE, on the first Hear it — hearing two designs in a row still returns to the
  tone the player arrived with.
- **Tweak** sends a second `ToneDesignRequest` with `History` = every turn so far
  and `CurrentTone` = the current result's preset, and the reply REPLACES the
  result. `Design` clears both.

The module display names in the chain summary ("Rat → Power Amp → Cab") come
from `IDspChainService.Catalog` through a `Func<string,string>` that
`PedalboardViewModel` supplies — `() => _dsp` semantics, because the seat picker
repoints that field. An id the catalogue does not know falls back to ITSELF, not
to a blank: a module this build cannot name is information.

Song title and artist reach the request through
`ToneDesignViewModel.SongContextProvider`, assigned by the HOST in
`PlayModeToneRigViewModel.cs`'s `BuildRigScreen` (the chart editor's host has no
flow song and assigns none). `OpenSettings` is assigned by both hosts — Play
Mode raises `OpenSettingsRequested`, the shell calls its own `OpenSettings`.
The drawer hangs off the SHARED `PedalboardViewModel`, so whichever host builds
last wins; both mean the same thing by it.

## The panel's states

| State | What is on screen |
|---|---|
| closed (default) | in the rig, the column is not drawn at all — the Tones head's "AI designer" toggle opens it; standalone, the header alone (see the budget note below) |
| open, no provider | ONE line (`ToneDesign_NotConfigured`) + "Open Settings" + "Use ChatGPT (copy & paste)" (`ShowSetupBanner`). No provider input, no Design, no result: a capability the app cannot honour must never look live. The ChatGPT door opens the body with the copy & paste block and NO Design / Tweak button (`ShowDesignButton` / `ShowTweakButton` need `IsConfigured`) — see below |
| copy & paste open | the composer's "Use ChatGPT" toggle is pressed (`IsManualOpen`); under the send row: the route's title with Copy prompt, the paste box, Import — and a failed import's line (`ManualErrorText`) beside Import |
| open, idle | the thread shows the intro line (`ShowIntro`) and the host's extras; the composer has the prompt, Design, and "Describe the sound first." while the box is empty (`ShowPromptHint`) |
| running | the new "You" bubble is the thread's last row and the previous answer's card STEPS ASIDE (`ShowResult` is false while `IsRunning`) — it would sit under a question it does not answer; the status line beside the button carries the service's `IProgress<string>` and Cancel sits beside it — see the progress rule below |
| failed | a `FireEmptyBanner Danger` in the thread with a message keyed by `ToneDesignFault`; `NotConfigured` and `Authentication` also offer "Open Settings" — the other five faults get the message alone, because an action that cannot help is worse than none |
| cancelled | `ToneDesign_Cancelled` in the status line, no error (the contract promises `OperationCanceledException`, not a fault); the answer that is still current comes back |
| answered | ONE card: "AI", the tone name, the confidence pill, the chain as chips in each unit's family colour (`ToneDesignResultViewModel.ChainSteps`), why (the rationale, shown — it is half of the answer), what the validator adjusted, the TONE3000 credit, then Hear it / Save as tone / Discard. An answer the thread has moved past is one line (`ToneDesignTurnViewModel.IsEarlierAnswer`). The composer's box is now the refinement and its button Tweak; the status line carries what it cost: `12.4 s · ~$0.04` |

**The panel SUBSCRIBES to `IToneDesignService.ConfiguredChanged`** (0.55.1;
`EnsureSubscribed` / `OnServiceConfiguredChanged`, and `Detach()` unhooks). It
re-read `IsConfigured` on every expand before, which is a whole
`IConfigurationService.LoadAppConfig` on the UI thread inside the ≤150 ms switch
budget, on a property a binding touches every layout pass — so the value is
CACHED now and the event is what refreshes it. Subscribing also reaches a panel
that is ALREADY open, which the expand-time read never could. `EnsureSubscribed`
is still re-tried while the service resolves to null: a host can wire the drawer
before the AI slice, and a build without the slice never gets one.

**The progress channel is NOT `Progress<string>`.** That type captures a
`SynchronizationContext` and POSTS: on the UI thread it defers the line to a
later dispatcher pass, and with no context at all it hands it to the thread pool.
Either way "Asking Claude…" reaches the screen at some unspecified point AFTER
the provider said so, which reads as a hang on the one surface whose whole job is
to say what is happening. `InlineProgress` (a private `IProgress<string>` in this
view model) applies the line the moment `Report` returns, and `PostToUi` marshals
only when the report did NOT come from the UI thread — the same `CheckAccess`
shape the removed LAN board used. The guard samples
the status line INSIDE the fake provider, the instant `Report` returned
(`FakeToneDesignService.StatusAfterReport`); a guard that read it afterwards
would give the posted callback the time it needs and hide the bug, which is
exactly how this reached the full gate as an under-load flake.

**The result's status line reports elapsed time and the estimated cost**:
`ToneDesignResult.Elapsed` (measured by the SERVICE, spanning the provider round
trip AND the validation after it) and `AiModelPricing.FormatUsd(result.Usage)`
over the billed token counts. A model the price table does not know prints
`local` with no dollar figure — the other half of this feature is LM Studio and
Ollama, where a request really is free, and `~$0.00` would read as a measured
zero. A result carrying `TimeSpan.Zero` prints nothing at all rather than
`0.0 s`. Keys: `ToneDesign_ElapsedSeconds`, `ToneDesign_CostEstimate`,
`ToneDesign_CostLocal`, `ToneDesign_ResultMeta`.

## "ChatGPT (copy & paste)" — the route with no provider

For a player with a chat subscription and no API key. It is the SAME design,
with the player carrying the prompt to the chat and the answer back:

1. **Copy prompt** (`CopyPromptCommand`) builds the request exactly as Design or
   Tweak would (`BuildRequest`: the conversation ending on the box's text, the
   current tone for a refinement, song, tuning) and asks the service for
   `BuildManualPromptAsync` — the provider's prompt flattened to text — on the
   thread pool (the capture library is read from disk), then writes it to the
   clipboard back on the UI thread. The conversation does not change; the request
   is remembered (`_manualPending`) so the import answers what the chat was asked.
   The status line says `Prompt copied (~N tokens).`
2. **Import** (`ImportReplyCommand`) sends the pasted text through
   `ImportReplyAsync` (the provider path's own reply pipeline) and, on success,
   lands exactly where a provider answer lands: a "You" turn, the answer card,
   the answer's row once the thread moves on. The next Tweak can go to the
   provider or through another copy & paste round. The card's status line reads
   `From ChatGPT (copy & paste)` — never a time or a cost (`AiUsage.IsManual`).
3. **A failed import changes nothing else**: the answer card, the thread, the
   boxes and the thread's own error banner are untouched; `ManualErrorText`
   (`ToneDesign_Manual_InvalidReply` for an answer with no usable tone) shows
   beside Import.

**Where it is reachable.** With a provider: the "Use ChatGPT" toggle in the send
row (`ToneDesignManualToggle`). Without one: the setup banner's
`ToneDesignUseManual`, which opens the body with the block open and no Design
button; unpressing the toggle returns to the banner. The host's extras (Match
from audio / by section) stay hidden while no provider is configured.

**It is compact because it is PINNED.** The block lives in the composer, which
never scrolls, so its height is taken from the thread: the instructions are the
buttons' tooltip and the paste box's placeholder, and the box is capped at three
lines (`FireToneReplyMaxHeight`, a style for the same reason as the prompt's
floor). The first cut (two instruction lines, a 120 px box) left the thread 0 px
tall at 1280x800; `Perf/ToneDesignManualRouteUiPerfSmokeTests` pins ≥ 64 px.

Tests: `UI/ToneDesignViewModelTests` (the `Copy_*`, `Import_*`,
`A_failed_import_*`, `The_route_is_reachable_*` cases),
`Services/Ai/ToneDesignServiceTests` (the copied prompt IS the provider's; a chat
answer imports to the provider path's preset), and the headless
`Perf/ToneDesignManualRouteUiPerfSmokeTests` in both hosts plus
`Perf/RigLabConstructionUiPerfSmokeTests` over the real RigLab host.

## The layout rule this surface adds

**A COLUMN THAT READS LIKE A CONVERSATION (2026-09-27).** The panel is a card
of its own at the right of the Tones view, three bands top to bottom:

- the **header** ("AI · Design a tone") on a seam;
- the **thread** — `ToneDesignScroll`, the column's ONE scroller, bar resting
  visible: the intro while it is empty, a "You" bubble per request, an earlier
  answer as one line, the newest answer as the card, and under a rule the
  host's `ExtrasTemplate` (DotCharter's "From the recording" and "Match by
  section" — other ways INTO an answer, not part of this one);
- the **composer**, pinned under the thread on the sunken floor: ONE box (the
  prompt until there is an answer, the refinement after; Enter sends) and one
  row under it — the status line or the prompt hint on the left, at most two
  lines (`MaxLines`, the full line as a tooltip), Cancel and the send button on
  the right. The row is a button tall, so nothing that line says moves the box
  (`Perf/RigTonesRedesignUiPerfSmokeTests.TheComposersBox_DoesNotMove_…`).

The thread FOLLOWS the conversation: `ToneDesignCorePanel.FollowConversation`
(a Background post after a turn is added, the card appears, or the panel
opens) scrolls just far enough to show the newest turn, or the answer card's
bottom — its actions — when the card is taller than the thread
(`…ANewAnswer_IsScrolledIntoView_WithItsActions`). The panel's code-behind
hooks the view model only while attached.

**The composer's box floor is a STYLE** (`TextBox.FireFieldInput.FireTonePrompt`,
`FireTonePromptMinHeight`), not an attribute: the box lives in the body
TEMPLATE, where a `{DynamicResource}` attribute binds at Template priority and
`TextBox.FireFieldInput`'s own `MinHeight=0` style setter beat it — the box
came out 18 px tall.

**The column's width is the breakpointed `FireRigToneDesignerWidth`, capped at
42 % of the Tones body and floored at 340** (`RigTonesSizing.DesignerWidth`,
applied from `RigTonesView`'s code-behind on `SizeChanged`): at 1280×800 the
token alone left the card grid narrower than one card.

**IT IS A MODE, NOT A DRAWER (W4, 2026-09-16), and the measurement is the whole
argument.** The panel used to be row 0 of the tone drawer, which shared the
rig's fixed-height workspace with the module editor. At 1600×900 — the smallest
supported window — that left `RigToneDesignScroll` a **140 px viewport for
487 px of content**, and 155 px even at 2560×1440, because the cap is what the
drawer's head, its catalogue-action foot and a browsable card grid leave rather
than a function of the screen. Not one of the four controls this panel is about
was on screen at once: the prompt field alone measured y=108..172.

The tone designer, the matcher and the Tones list are now the rig's **Tones
mode** (`AmpFxRigViewModel.IsTonesMode`, which IS
`PedalboardViewModel.IsBrowserOpen`). W4 made the panel the LEFT COLUMN of
that mode's one scroller; since 2026-09-27 it is the right-hand column with
its own thread scroller (above). Three consequences of W4 that still hold:

- **`AmpFxRigView.CapToneDesign` is gone**, with `MinBrowserBodyHeight` and
  `MinToneDesignHeight`. The cap existed because the panel sat in an `Auto` row
  inside a fixed one; `RigTonesScroll` is the `*` row and sizes itself.
- **`ToneDesignPanel.IsStubbed` is gone.** The panel collapsed to its header
  whenever the editor wanted the workspace; they no longer share one.
- **The subtitle is the thread's INTRO now** (`ToneDesign_Subtitle`, shown while
  the thread is empty), not a header line.

Pinned by `Perf/RigTonesModeUiPerfSmokeTests
.TheFourDesignControls_AreOnScreenTogether_AtTheSmallestWindow`: the prompt and
Design (pinned in the composer) and "From the recording" / "Match by section"
(in the thread) must be inside the window and, for the two in the thread, inside
its unscrolled viewport, at 1600×900, 1920×1080 and 2560×1440. **Run it in
de-DE too**: German is what decides it at 1600×900 — the designer's vertical
insets (`FirePadToneHead` / `…Thread` / `…Composer`) were tightened until it
passed with a few pixels to spare.

**TYPE ROLES AND BUTTON TREATMENT (W4).** Every `TextBlock` in
`ToneDesignPanel`, `ToneSectionsPanel`, `RigControlAssignmentView` and
`RigPerformanceControlsView` wears one of three classes declared once in
`Styles/Fire/FireRigRolesAndTones.axaml` (ControlThemes `RigRoleTitleText` /
`RigRoleLabelText` / `RigRoleBodyText` since #242, worn with the class) — `RigRoleTitle` (panel title), `RigRoleLabel`
(section label: mono, tracked; also every status and load line) and
`RigRoleBody` (running text, `.Strong` for a row's own name). No `FontSize` and
no `LetterSpacing` at a call site in those files. Buttons: the **chamfer is the
treatment for a primary action and nothing else**, one per block, and
`Button.FirePrimary.Body` gives it the body face instead of Chakra at .22em —
Design, Match from audio and Match all read as headings without it.
`Button.FireQuiet` is not used on these surfaces at all: it is a bare label with
no container, no hover fill and no pressed state.

**The body lives in the `ContentControl`'s `ContentTemplate`
(`x:Name="ToneDesignBodyHost"`), not inline.** A template is not instantiated
until its host is MEASURED, and Avalonia skips `MeasureCore` for an invisible
element — so a collapsed panel realizes NO body subtree, rather than a hidden
one. Inline markup would be built with the drawer and add its first layout to
every drawer open, against the ≤150 ms switch budget, for a surface nobody asked
for. Measured: inlining it realizes **68** elements.
`Perf/ToneDesignUiPerfSmokeTests.TheCollapsedPanel_RealizesItsHeaderAndNothingElse`
is the guard and names the count it found.

The panel is column 1 of `RigTonesColumns` in `RigTonesView`; the library
(sidebar and card grid, `RigTonesScroll`) is column 0, and the head is row 0 of
`RigToneBrowserBody` above both, so nothing scrolls away from what it acts on.
`RigToneBrowser`'s own `IsVisible="{Binding IsTonesMode}"` still gates the lot —
and it is repeated on the host (`RigTonesMode`, the `RigTonesView` element in
`AmpFxRigView`), because the prewarmers reach this surface by NAME through
`HiddenNamedDescendants`, which reads the named control's own flag. They list
`RigTonesMode`, `RigToneBrowser` and `RigToneDesignPanel` IN THAT ORDER: the
view is a UserControl whose template is not applied until it is measured, so
the drawer is not even in the visual tree before the view is realized
(`…ThePrewarm_RealizesTheCardGrid_WhileTheModeIsClosed`).

**The match block has a DETERMINATE progress bar, shown only while the tone
library builds (2026-09-14).** `ToneMatchProgress` binds
`MatchProgressFraction` / `HasMatchProgress`, which `OnMatchStatusChanged`
derives from the status line through `RigLibraryProgress.TryParseBuilding` —
a "Building tone library: 12/96 · ~3 min left" line shows the bar at 0.125,
any other line hides it. Not an indeterminate bar over every stage: over
"Searching for a rig…" it would say nothing the words do not. Pinned by
`ToneDesignMatchTests.ALibraryBuildLine_DrivesTheProgressBar_AndAnyOtherLineHidesIt`
and `Perf/ToneDesignMatchUiPerfSmokeTests.TheLibraryBuildBar_ShowsWhileBuilding_AndMeasuresNonZero`.

**Superseded by W4 — kept because the FAULT is still the one to avoid.**
The paragraph below describes the drawer's capped scroller. The cap and the
drawer are gone (see the top of this section); what stays true is the shape of
the bug it fixed, which is why an `Auto` row inside a fixed one is never the
answer on this screen.

**The panel SCROLLED inside the drawer and was CAPPED from code (2026-09-14).**
Row 0 is `Auto`, a Grid measures an Auto row with INFINITE height, and the
drawer's row is a fixed pixel count — so an expanded panel with "Match by
section" and a result open was simply TALLER than the drawer and was arranged
straight out of the bottom of it, over the footer's tuner strip, with no
scrollbar (owner, 2560×1440 at the 1.35 Auto scale, ~1896×1000 DIPs: the
"Add section / Propose sections / Match all / Apply as tone set" row drawn
UNDER the tuner). Three things hold it now, and each is asserted separately:

- the panel sits in `RigToneDesignScroll`, a `ScrollViewer` with
  `VerticalScrollBarVisibility="Visible" AllowAutoHide="False"` — an
  auto-hidden bar is what hid the module editor's cut, and `AllowAutoHide`
  does not inherit, so it is set on THIS scroller;
- `AmpFxRigView.CapToneDesign` sets that scroller's `MaxHeight` to what the
  drawer body leaves after the head, the catalogue-action foot and a
  browsable card grid (`MinBrowserBodyHeight` 160), floored at
  `MinToneDesignHeight` 140 on a drawer dragged to its 260 px minimum. It is
  code and not a literal because the drawer is user-resizable; it runs on
  `SizeChanged` of the body, the head and the foot and after `SyncBrowserRow`;
- `RigToneBrowser` carries `ClipToBounds="True"`, so containment is
  structural whatever a future row asks for.

**Not scaled.** `RigScaleToFit` is for editor FACES — pictures of one piece of
gear. This is a FORM (text boxes, a combo, lists) and a scaled form has scaled
hit targets and scaled type. The preset card grid's scroller
(`RigBrowserCardScroll`) got the same rest-visible bar in the same change.

Pinned by `Perf/UiResolutionAuditUiPerfSmokeTests.TheToneDesignPanel_ScrollsInsideTheDrawer_AndNeverReachesTheFooter`
at 1920×1080 and 1896×1000: extent > viewport, the scroller's and the panel's
PAINTED bottom inside the drawer and above the footer, the bar visible with a
non-zero width, the card grid still on screen. Breaking the cap (the
`MaxHeight` assignment commented out) fails it with "the tone panel's scroller
wants 1334 px of a 1334 px viewport".

## "Match from audio" — the second way into the same result

**Design a tone asks a language model what a rig should be; this asks the
AUDIO.** The song's guitar is isolated for the tone-change span the player is
standing in, a matcher renders candidate rigs over a dry DI and ranks them, and
the selected candidate is published as **`ToneDesignViewModel.Result`** — the
same property a designed tone lands in. That IS the integration: Hear it, Save
as tone, Discard and Tweak are not re-implemented for matching and never learn
that the rig came from audio rather than from a model.

| Piece | File |
|---|---|
| the whole flow (+ `ToneMatchSource`, `ToneMatchCandidateViewModel`) | `DotCharter.UI/ViewModels/PlayMode/ToneDesignViewModel.Match.cs` |
| the markup | `ToneDesignPanel.axaml`, `x:Name="ToneMatchBlock"` (inside the body template, above `ToneDesignResult`) |
| the contract every lane codes against | `DotCharter.Services/Ai/Match/MatchContracts.cs` |

### The flow

1. **"Match from audio"** → `IStemIsolator.IsolateGuitarAsync(path, from, to, …)`,
   status `ToneMatch_Isolating`. An isolator that answers with an EMPTY
   `MonoAudio` is not an exception — the cache can legitimately hold silence for
   a span with no guitar — but it ends the flow with its own message.
2. **`ToneMatch_Searching`** → `IToneMatcher.MatchAsync`. The **prior is the
   design result when there is one, and `IRigEstimator` only when there is not**:
   the contract carries exactly ONE, and a player who has just described the tone
   has told us more than a model guessing from a stem. An estimator that throws
   is swallowed — a prior is an optimisation and must not fail the search.
3. **Results**: candidates sorted by distance ascending, the best one SELECTED
   (publishing a ranked list and making the player click row 1 is a step that
   only ever has one answer). Selecting a row sets `Result`; the four actions
   follow it.

Cancel is one token for recording, isolation and search alike, and every fault
is a sentence of its own — "matching failed" tells a player nothing they can act
on. Faults: not available, no song audio, no reference DI, no guitar isolated,
no candidates, no input, a capture gap, a stalled capture, and the generic one.

### The match percent, and why it is a ratio

**The percentage is computed in the SERVICE lane and the panel only DISPLAYS it**
(W5b, 2026-09-16). It was `ToneDesignViewModel.MatchPercent(distance, floor)` =
`100 x floor / max(distance, floor)`, and on real material that floor sits above
every candidate's distance, so **every row read 100 %** — on a match whose tones
the owner called plainly wrong. A view model cannot build a scale out of a
distance: the honest one needs a second MEASURED anchor (one render of an
information-free `overdrive -> amp -> cab` over the same DI) that only the matcher
can produce. The formula, both anchors and the calibration numbers are in
[../../services/ai-match-search.md](../../services/ai-match-search.md) under
"The match PERCENTAGE".

**What the panel owns is the THREE STATES and the breakdown.**
`ToneMatchCandidateViewModel` exposes `MatchPercent` (**`int?` — null is UNRATED,
and is not 0**), `IsRated`, `UnratedReason` / `UnratedText`, `MatchText` (the pill:
"46 % match" or "Unrated — …"), `ScaleText` (what 0 % and 100 % mean),
`SelfDistancePercent` / `WithinTargetNoise` / `NoiseCaveatText` (the candidate is
inside the passage's own variation) and `Breakdown` — up to four
`ToneMatchBlockViewModel` rows (`Label`, `SharePercent`, `WorseThanReference`,
`Text`), worst first — plus `BreakdownText` for a one-line form. The saved tone's
`Description` carries the same breakdown, so the number is never stored alone.
**0 % is a measurement and unrated is not; a template must not render them the
same.**

### The reference DI, and the one audio rule this surface adds

Matching renders candidates over a dry guitar, and rendering over the PLAYER's
own pickups is what makes the answer transfer to their rig — so "Record 8 s of
your playing" is offered first.

**The fallback is a clip this build GENERATES, not one it reads.**
`Assets/Ai/reference-di.wav` is EMBEDDED by the asset glob and deployed by
nothing, so the file branch answers in no build that ships — and until 0.55 the
first press of Match on a fresh install therefore said "no reference DI" and the
whole feature read as broken. `ConfigureMatch`'s `builtInReferenceDi`
(`Func<MonoAudio?>`) is the seam and the host passes
`Similarity.ReferenceDi.Medley` — the fixed 20 s Karplus-Strong take the
retrieval library was rendered over, which is also the better default: matching
over the clip the library itself used removes one whole source of disagreement.
Order is recorded → a file on disk if a host ships one → generated; the file
branch is kept because a host that deliberately ships a clip means it.
`ToneDesignMatchTests.WithNothingRecordedAndNoClipOnDisk_TheBuiltInClipIsUsed`.

**The recorder reads `ReadInputCaptureWindow`, never `ReadInputCapture`.** The
streaming read is SINGLE-CONSUMER and is feeding live note detection; a recorder
on it starves the thing that judges the player's notes. That has two
consequences the code spells out:

- **A windowed reader cannot drain the stale ring, so it ANCHORS.** The window
  is non-destructive and always hands back the newest ~0.7 s, which on the first
  poll is audio from before the button was pressed. The loop takes
  `TryGetInputCaptureSync` first, keeps nothing, and stitches later snapshots by
  ABSOLUTE stream position. (A streaming consumer drains instead — see
  `LivePlayViewModel.Session.cs`'s activation drain.)
- **A poll that arrives after the ring wrapped is a GAP and is reported**, never
  spliced: a spliced DI matches confidently against audio nobody played. And the
  loop carries a **poll budget**, because every other exit from it depends on a
  device making progress — without one, an engine whose sync counter answers
  while its ring stays empty spins forever. Both are pinned; the budget exists
  because removing the gap check during a mutation run hung the suite.

### The seams, and what the host still owes it

Nothing is registered here and nothing is resolved from here. Every dependency
arrives through **`ConfigureMatch(matcher, isolator, estimator, audio, delay,
bundledReferenceDi)`** — not the constructor, so this partial's merge surface is
members that exist nowhere else — and the composition root passes
`GetService`-backed `Func`s. **No matcher ⇒ `IsMatchAvailable` is false and the
block is off screen**, not disabled.

`MatchSourceProvider` is the host's answer to "which audio, which span, where is
the playhead", assigned in `PlayModeToneRigViewModel.cs`'s `BuildRigScreen` beside
`SongContextProvider` — it is the only place that can reach
`ProjectState.ActiveArrangement.ToneChanges` and the beat map, and
`DotCharter.Core/Utilities/ToneChangeSpans.cs` is the one thing that turns placements
into time ranges. **A host that assigns nothing still matches**: the transport
knows its own file (`IAudioPlaybackService.CurrentAudioPath`), so an unassigned
provider degrades to 30 s of whatever is loaded rather than to a dead
button — which matters because the chart editor's rig host wires no flow song
either. `PickSpan` skips a span the chart layer already calls too short
(`ToneSpan.IsUseful`) and never falls back to the WHOLE song, which would
average every tone in it into one confident answer about nothing.

**The no-tone-changes fallback FOLLOWS THE PLAYHEAD, and did not until
2026-09-16** (W5a, "match from audio produces truncated tones"). It returned
`(0, 30 s)` and ignored `PositionMs` entirely, so a chart with no tone changes —
the owner's own project, and every song that is not in the library — matched the
OPENING of the song wherever the player was standing. On `Polkadot Stingray -
Sakasma` the separated guitar for that window is digital silence for its first
ten seconds and −70 dBFS for five more, so the tone was built out of half an
intro the player was not listening to. It is now `fallback` of song FROM the
playhead, sliding back at the end of the song so a match there still gets a full
window; at playhead 0 the answer is unchanged.

**…and "no guitar" is not only an EMPTY buffer.** A separated stem for a stretch
with no guitar in it is a full-length buffer of ZEROS, which is not
`Length == 0` and went through the old guard into a match that reported a
confident percentage about an all-zero feature vector. The panel asks
`MatchTargetAudio.IsMeasurable` (the same trim the matcher applies, so the two
cannot disagree) and answers `ToneMatch_Fault_NoStems`. A window whose playing
sits after a long silent lead-in is still MATCHED — the trim is what makes it
usable.

**Discard clears the candidate list too**, through `OnResultChanged` rather than
an edit to `Discard`: a ranked list left under a discarded result offers a row
whose tone the panel has just put back.

### What the contract does not carry (for the next lane)

- **No tone CATEGORY on a candidate.** A matched tone takes the design's
  category when there is one and `DspToneCategory.Experimental` otherwise — the
  honest bucket for "we do not know", which keeps matched tones out of the
  curated rails instead of filing every one under Lead.
- **No audio DECODER.** The contract exchanges `MonoAudio` and names nothing
  that produces one from a file; the shared one is `Ai/Match/AudioClipLoader`,
  and the bundled clip reads through its forgiving `AudioClipLoading.TryLoad`
  (blank, missing, undecodable or empty is null — "no clip", never an
  exception), on a worker.

## "Match by section" — one rig per part of the song

**The owner's words:** "when generating the tone we would also have to pick
stems/instrument/part like 0:10 to 0:30 clean then when we want to get the
distortion 0:30 to 0:50 distortion etc". So this is a LIST: each row names a
stretch of song, a stem and a working label; "Match all" finds rigs for every
row in one run; and **"Apply as tone set"** turns the chosen rigs into tone
presets plus a `ToneChange` at each section's start, so the song plays with tone
changes. **It works on a song that is NOT in the library** — the chart's tone
changes are used when there are any, but a section can equally be typed in or
proposed from the audio, and nothing asks for an arrangement until the last step.

| Piece | File |
|---|---|
| the flow | `DotCharter.UI/ViewModels/PlayMode/ToneSections/ToneSectionsViewModel.cs` |
| one section (+ `ToneStemOption`) | `DotCharter.UI/ViewModels/PlayMode/ToneSections/ToneSectionRowViewModel.cs` |
| one suggestion | `DotCharter.UI/ViewModels/PlayMode/ToneSections/ToneSectionProposalViewModel.cs` |
| the `m:ss` clock (pure) | `DotCharter.UI/ViewModels/PlayMode/ToneSections/ToneSectionTime.cs` |
| the seam onto the panel | `DotCharter.UI/ViewModels/PlayMode/ToneSections/IToneSectionHost.cs` |
| the panel's host wiring | `DotCharter.UI/ViewModels/PlayMode/ToneDesignViewModel.Sections.cs` |
| the view | `DotCharter.UI/Views/PlayMode/ToneSectionsPanel.axaml` (+`.cs`) |
| the composition root | `PlayModeToneRigViewModel.cs` — `ConfigureSections(...)` + `PlaceToneSet` |
| the contract | `DotCharter.Services/Ai/Match/SectionContracts.cs` |

### A separate VIEW, hosted through a template

`ToneSectionsPanel` is its own `UserControl` with its own view model, hosted from
`ToneDesignPanel.axaml`'s `ContentControl x:Name="ToneSectionsHost"` — a
`ContentTemplate`, so a build without the section slice (or a collapsed panel)
realizes **no section subtree at all**, not a hidden one. That is the rule
`PlayModeView` learned at 780 KB: a big screen inlined in another screen is not a
type a second host can name, and it makes every open of the outer screen pay its
first layout. The host is a **SIBLING of `ToneMatchBlock`**, not a child: the
section slice registers independently of `IToneMatcher`, so a build with a set
matcher and no single matcher must still show it. Nothing here is on the prewarm
path — the whole drawer is still gated by `Rig.IsBrowserOpen`, so it is not an
`IPrewarmableSurfaces` surface for the same reason the design panel is not.

### The seams, and what the host owes it

Nothing is registered here and nothing is resolved from here. Everything arrives
through **`ConfigureSections(proposer, setMatcher, narrator, playheadMs,
placeToneChanges, saveTone)`** on `ToneDesignViewModel`, which the composition
root calls with `GetService`-backed `Func`s beside `ConfigureMatch`. **With
neither a proposer nor a set matcher registered `IsSectionsAvailable` is false
and the block is off screen**, not disabled.

What the flow needs back from the panel is **`IToneSectionHost`** — five
answers, explicitly implemented so they are not five new members on the panel's
public surface: `ResolveMatchSource` (the existing `MatchSourceProvider` answer,
`ToneChangeSpans.Resolve` and all), `EnsureReferenceDiAsync`, `ChainSummary`,
`SongContext`, `PublishResult` and `SaveTone`. **That is the whole integration**:
a section's chosen candidate is published as `ToneDesignViewModel.Result`, so
Hear it / Save as tone / Discard / Tweak are not re-implemented per section and
never learn the rig came from one; and its tone is saved through the drawer's own
`SaveDesignedTone`, so it lands in My Tones visibly.

`playheadMs` defaults to the engine `ConfigureMatch` was already given — asking a
host to wire the transport twice is how the two drift apart.

### Propose, match, explain, apply

- **Propose sections.** The chart's tone changes are resolved first and handed to
  `IToneSectionProposer.ProposeAsync` as its `chartSpans`, which is what lets the
  proposer report a boundary the audio AGREES with as `chart+audio`. With no
  proposer the chart spans ARE the offer, so a song in the library still proposes
  without the audio slice; with no chart they are empty, which is the
  song-not-in-the-library case. **A proposal is never applied silently** — each
  row carries its origin and confidence and has Accept / Dismiss.
- **Match all** → `IToneSetMatcher.MatchAsync` with every VALID row in song
  order. **A set never fails as a whole because one part could not be isolated**:
  each row carries its own status and its own ranked candidate list, best
  selected. The matcher's own `Error` string goes to the log; the row gets a
  sentence a player can act on.
- **Explain** → `IToneNarrator.NarrateAsync` on the SELECTED candidate — one call
  per press, never one per candidate. No narrator ⇒ the button cannot execute.
- **Apply as tone set** → one preset per chosen section (named after it) plus one
  `ToneChange` at its start. The placement delegate answers a COUNT, or **`null`
  for "this project has no arrangement"** — a different outcome from "placed
  none", and the list then keeps the presets and says so. `PlaceToneSet` in
  `PlayModeToneRigViewModel.cs` is the host half: `TimelineMath.PositionFromMs`
  (the app's one ms → `FractionalPosition` conversion) into an
  `AddToneChangesAction` under `IHistoryService.Do`, so a whole tone set is one
  Ctrl+Z.

Times are **TEXT** (`m:ss`, `m:ss.f`, or bare seconds), parsed on every keystroke;
an unparseable box, an end before the start or a span under
`ToneSection.MinDuration` leaves the row INVALID and out of the run rather than
quietly contributing 0:00 to 0:00. `0:75` is refused rather than read as 75 s.

### What the contract does not carry (for the next lane)

- **No tone CATEGORY on a section's candidate**, same as the single match: it
  takes `DspToneCategory.Experimental`.
- **No song title/artist on `IToneSectionProposer`**, though the set matcher and
  the narrator both take them — a proposer that wanted to use the song's known
  structure cannot.
- **`ToneSectionMatch.Error` is a free string**, not a fault enum, so the UI
  cannot key a per-failure message off it the way `ToneDesignFault` allows; every
  failed section gets one sentence.
- **Nothing carries the SET back** — `ToneSetDraft` exists but no interface takes
  or returns one, so "apply as tone set" is assembled entirely in the UI.

## Settings ▸ AI

A screen of its own like every Settings tab (`AiSettingsViewModel` behind
`IAiSettingsScreen`, rail row `ai` in `SettingsRail`), reached by the shell's
Save / Cancel / "Reset section" verbs through `ISettingsTab`, and counting a
POSITIVE list of persisted values through its `SettingsChangeSet`
(`ProviderKind`, `ModelId`, `BaseUrl`; `SelectedProvider` is the combo's option
OBJECT, not a value).

- **`ApiKeyEntry` is deliberately NOT tracked.** The tracker keeps a baseline
  COPY of every value it watches, and a key typed into the box has no business
  living in one.
- **The key is never read back into the box.** It goes through `IAiSecretStore`
  on save and the field is cleared; what is shown afterwards is a "key stored"
  indicator and a Clear button. An EMPTY box on save means *unchanged* — Clear
  is the erase. A password box that repopulates itself hands the secret to
  anything that can screenshot the window, and makes an empty box mean "erase".
- **Off Windows there is no DPAPI**, so the store's `IsPersistent` is false and
  the key is session-only. The row says so (`Ai_KeySessionOnly`, bound through
  `KeyIsSessionOnly`) — a user who believes a key was saved and finds it gone
  next launch reads that as the feature being broken. **`IsPersistent` is on
  `IAiSecretStore` since 0.55.1 and `KeyIsSessionOnly` reads the store it was
  HANDED.** It named `DpapiAiSecretStore` statically before, so with a fake
  injected the row still answered for the real store: what it asserted was
  really "is this Windows".
- **Built-in is shown and cannot be picked — and the picker learns that from the
  FACTORY.** Every row but `None` takes its `IsAvailable` from
  `IToneDesignModelFactory.Supports(kind)` (`SupportsKind`, 0.55.1), so the day
  the reserved slot is filled in the row enables itself with no UI change;
  `None` is the off switch, not a provider, and is always pickable. The contract
  pins the enum value so a config written by a later build round-trips today,
  and the row is still refused in `OnSelectedProviderChanged`, which bounces the
  selection back and SAYS why — a disabled `ComboBoxItem` is not reliably
  unpickable, and an option that silently reverts reads as a broken control.
  `AiSettingsViewModelTests.The_picker_rows_follow_the_factory_and_not_a_hard_coded_list`
  moves the factory's answer and watches the row follow.
- **`UseLibraryTones`** (default ON) is the fourth TRACKED property — a checkbox
  row (`AiUseLibraryTones`, keys `Ai_UseLibraryTonesLabel` / `_Desc` / `_Check`)
  under the provider rows. It switches grounding stage 2 off; see
  [docs/agents/services/ai.md](../../services/ai.md) § grounding stage 2. It is
  tracked because it is a persisted VALUE like the other three.
- **Saving a CHANGED configuration pokes
  `IToneDesignService.NotifyConfigurationChanged()`**, which raises
  `ConfiguredChanged` and makes an already-open "Design a tone" panel re-read.
  Inside the change test on purpose: a Save that moved nothing must not
  republish. The service is a fourth OPTIONAL ctor arg, resolved with
  `GetService` like the other two.
- **Test connection SAVES first**, then `IToneDesignModelFactory.Create(...)
  .ProbeAsync`. A probe of the settings on screen rather than the ones in force
  would pass for a configuration nothing is using — which is the one failure a
  test-connection button exists to rule out.
- **Both AI services are optional ctor args**, and `App.ViewModelServices.cs`
  resolves them with `GetService`, not `GetRequiredService`. A build without the
  provider slice must still OPEN Settings; the tab reports itself unavailable
  (`IsAvailable` → `Ai_Unavailable`) instead of taking the page down.
- The two preset buttons write `AiProviderPresets.LmStudioBaseUrl` /
  `.OllamaBaseUrl` — the service layer's constants, never a second copy.
- **Claude Code (local CLI) is the fourth provider row, and it is the one whose
  `IsAvailable` depends on the MACHINE** — `Supports(ClaudeCli)` is "a `claude`
  binary was found", so the row greys out where none is installed and
  `OnSelectedProviderChanged` bounces it with `Ai_Provider_ClaudeCliMissing`
  rather than `Ai_Provider_BuiltInHint`. Three rows are keyed to it:
  - **the one-line note** (`Ai_ClaudeCli_Note`, bound through
    `ShowClaudeCliNote`) sits ABOVE the model row and says what it runs, whose
    login it uses and whose usage it spends. Not in an expander: a cost note
    behind a disclosure triangle is a cost note nobody read.
  - **the API-key row is HIDDEN** (`ShowKey` excludes this kind). The provider
    takes no key, and a password box that ignores what is pasted into it is
    worse than no box. `AiSettingsClaudeCliTests` asserts the DIFFERENCE against
    the other two kinds, so it cannot pass by hiding the row for everyone.
  - **an optional path box** (`ShowCliPath`, `Ai_CliPathLabel`) for a machine
    where `claude` is not on `PATH`. It persists through **`BaseUrl`**, which
    that kind REUSES as a path — documented on `AiProviderSettings`, and the one
    thing about this row a later refactor is likely to drop. Empty means "search
    PATH"; a path that does not exist is its own error and never a silent
    fallback.
  - three more preset buttons (`UseClaudeHaiku` / `UseClaudeOpus` /
    `UseClaudeSonnet`, panel `AiClaudePresets`, `Ai_Preset_Claude{Haiku,Opus,Sonnet}`)
    fill the model box with `claude-haiku-5-5` / `claude-opus-5-5` /
    `claude-sonnet-5-5` (`ClaudeCliToneModel.DefaultModelId` / `OpusModelId` /
    `AlternateModelId`), shown for this kind only.
  Picking it seeds `DefaultClaudeModel` (= `ClaudeCliToneModel.DefaultModelId`)
  exactly as the API Claude row does. A model id already SAVED is never rewritten
  when those defaults move — only an empty box is filled, or one still holding
  the PREVIOUS kind's untouched default (`AiDefaultModels.Seed`), so moving from
  Claude Code to Codex never hands `claude-haiku-5-5` to Codex.
- **The Codex CLI kind gets its own two presets** (`AreCodexModelPresetsVisible`,
  `UseCodexSol` / `UseCodexLuna` → `gpt-6-sol` / `gpt-6-luna`, the
  `AiCodexPresets` panel, `Ai_Preset_Codex{Sol,Luna}`). Both apps' pickers list
  Codex (DotCharter's since 2026-09-28, enabled by the factory like every other
  row). Picking it seeds `gpt-6-luna` (`CodexCliToneModel.DefaultModelId`), and
  an empty model also means Luna, so
  Test works with the box empty.
- **What each provider shows is ONE table: `Presentation/Screens/Settings/AiProviderShape`**
  — its rows (key, base URL, CLI path, web research), its note (`AiClaudeCliNote` /
  `AiCodexCliNote`), its presets, the words of the path and model rows
  (`CliPathLabel` / `CliPathDesc` / `ModelDesc`), its default model and the
  picker's "not installed" reason. A new provider is one entry there; the view
  model reads `Shape` and never tests a kind itself. Pinned row by row by
  `AiSettingsCodexCliTests.TheProviderTable`. (RigLab's own view model still
  carries its conditions inline.)
- **The "Match model" block carries a SECOND row since 2026-09-14: the tone
  library** (`AiMatchLibraryStatus`, `AiMatchLibraryDownload`,
  `AiMatchLibraryCancel`, `AiMatchLibraryDetail`, its own indeterminate bar) —
  the pre-rendered retrieval library for the bundled DI, the second entry in the
  same catalogue, fetched by the same downloader
  (`IRigEstimatorModelDownloadService.EnsureLibraryInstalledAsync`). Own state
  (`Library*` on `MatchModelViewModel`), own command, own
  cancel: the two downloads run and fail independently, and
  `MatchModelViewModelTests` asserts a library download leaves the model's offer
  and call count untouched. The not-installed line says what the row is FOR
  ("without it the first match renders a small library (~4 min) and grows it in
  the background"), because a 9 MB download with no stated benefit is a button
  nobody presses. Installed shows the file's size only — whether it is the RIGHT
  library for the DI is the matcher's header check, reported in its log.

## Localization

**184 keys for this surface, in all three tables — RE-MEASURED 2026-09-16 after
the W4 rebase onto W5b, per prefix with `grep -c '<data name="<prefix>'` on
`Resources.resx`:** **`ToneDesign_*` 33** (the panel, its status lines and one
message per `ToneDesignFault`), **`ToneMatch_*` 53** (the match block, its
progress lines, one message per fault, and W5b's scale / unrated-reason /
per-block breakdown vocabulary — this row said **28** until W5b landed and
nothing updated it), **`ToneSections_*` 55** (the section list, the stem
vocabulary, the proposal origins, the per-row status lines and one message per
fault), **`Ai_*` 41** (the tab, including the 8 for the Claude Code CLI row:
`Ai_Provider_ClaudeCli`, `_ClaudeCliMissing`, `Ai_ClaudeCli_Note`,
`Ai_CliPath{Label,Desc,Placeholder}`, `Ai_Preset_Claude{Opus,Sonnet}`), plus
`Settings_AiTab` / `Settings_AiTab_Desc` (the rail).

**W4's own NINE are RIG keys and are not in that total** —
`Rig_TonesMode_{ChainKicker,ChainEmpty,EmptyTitle,EmptyBody}` (the mode's chain
strip and the empty workspace's no-selection variant),
`Rig_Editor_{NoAssignmentsTitle,PerformanceUnavailable}` (the module editor's
empty states) and `Rig_Editor_{HiddenTitle,HiddenBody}` + `Rig_Dock_Show` (its
editor-hidden variant and the toggle caption that undoes it).

**Whole-file figures, same measurement: 4,231 `<data>` rows in EACH of the three
tables; 487 / 510 / 558 KB CRLF (`wc -c` 498,915 / 522,037 / 570,974).** The row before this one read 4,204 and 484 / 507 /
554 KB, measured on the same branch BEFORE the rebase — i.e. it went stale by 22
keys the moment W5b merged. Re-grep; never carry either number forward.

The group kicker reuses the existing `Settings_Kicker_Tone`. Every value differs
across en / de-DE / ja-JP, so the untranslated ratchets in
`UI/LocalizationSweepTests.UntranslatedValues_DoNotGrow` did not move (de 245,
ja 24 — the German for "Instrument" and "Bass" IS the English word, so those two
rows are "Instrumentspur" and "Bassgitarre" rather than a raised ceiling).

`ToneDesignViewModel.FaultKey` is a switch returning literal key names rather
than a concatenation, so the sweep's C#-lookup half can see all seven.

**Plus `Settings_Ai_MatchLibrary_*` — 4 keys (2026-09-14)** for the library
row: `StatusLabel`, `StateMissing` (`{0}` MB), `StateInstalled` (`{0}` MB),
`Download`. Failure, cancel and "Connecting…" reuse the model row's
`Settings_Ai_MatchModel_*` keys.

## Tests

| Class | In the gate? | Covers |
|---|---|---|
| `UI/ToneDesignViewModelTests` (23) | yes | the conversation's shape (You / earlier answer / You / the card, the old card stepping aside while a refinement runs, the intro, the prompt hint, the chain chips falling back to ids); design → result; the song context in the request; one case per fault + which two offer Settings; cancel; tweak carries history + current tone; Hear it → the apply seam and Discard → the ONE restore; save → `SaveUser` with the designed rig; not-configured offers nothing; progress; warnings. Plus one case through the REAL `PresetBrowserViewModel` proving the save lands in the library and the snapshot is User-origin |
| `Presentation/Settings/AiSettingsViewModelTests` (13) | yes | load/save round trip, the key never read back, an empty box leaves the stored key, Clear erases, the Claude default, the two presets, Built-in refused, probe ok/failed/throwing, no-services, and that the change set counts the four persisted VALUES and never the key |
| `Perf/ToneDesignUiPerfSmokeTests` (5) | **no** — `[Collection("HeadlessUI")]`, its own `dotnet test` | the collapsed panel realizes only its header; the expanded panel (with a seeded result, so the confidence pill, the chain chips, warnings and the actions are REALIZED, and the composer shows ONE box — the refinement) measures non-zero; the setup-line-only state; the AI tab measures non-zero on its widest branch; the no-services tab says so |
| `Perf/RigTonesRedesignUiPerfSmokeTests` (18) | **no** — its own `dotnet test` | the Tones view over the REAL catalogue plus long-named user tones and a refined design, both hosts (DotCharter, RigLab), closed / fresh / answered, 1920×1080 and 1280×800, and the capture dump (`DOTCHARTER_UI_SWEEP_DUMP=1` + `DOTCHARTER_RIG_CAPTURE_DIR`); the thread follows a new answer; the grid keeps a whole card at 1280; the composer's box never moves; the head never moves and fits at 1280 (run it in de-DE); only the playing card says Edited; the prewarm realizes the grid with the mode closed |
| `UI/ToneSectionsViewModelTests` (43) | yes | the section flow against fake section services: add/remove/validate (three shapes of bad time, and an invalid row left out of the run), propose with and without a proposer, accept/reject, match-all's request and its per-row outcomes including ONE failing section, cancel, no-sections / no-reference-DI, explain (and no Explain without a narrator), apply-as-tone-set's N presets + N entries at the right starts, the no-arrangement path, the off-screen-when-unregistered cases through a real `ToneDesignViewModel`, and the pure `m:ss` clock |
| `Perf/ToneSectionsUiPerfSmokeTests` (3) | **no** — `[Collection("HeadlessUI")]`, its own `dotnet test` | the section list with a seeded row and candidates measures non-zero and realizes both templates; the block without a section yet keeps its list collapsed; **no section slice ⇒ the `ContentTemplate` is never instantiated**, not merely hidden |
| `UI/ToneDesignMatchTests` (49, re-measured 2026-09-16 after W5b replaced the `MatchPercent` theory with five panel cases; the row said 36 and was stale before W5a) | yes | the match flow against fake match services: the span isolated, the prior sent (design first, estimator second, a throwing estimator swallowed), the ranked list, the selected candidate driving Hear it / Save / Discard, cancel at each stage, one case per fault, the recorder's anchor / gap / poll budget, and the two pure decisions (`MatchPercent`, `PickSpan`) |
| `Perf/ToneDesignMatchUiPerfSmokeTests` (4) | **no** — `[Collection("HeadlessUI")]`, its own `dotnet test` | the match block with seeded candidates measures non-zero and realizes its rows; the selected candidate publishes the SHARED result block; the results list stays collapsed before a search; no engine hides the record button; no matcher hides the block |

The AI tab is also in `Perf/SettingsConstructionUiPerfSmokeTests`' `AllTabs`,
`UI/SettingsRoundTripTests`' `Surfaces` and `UI/SettingsTabReachabilityTests`'
`Surfaces` — a new settings tab belongs in all three or they pass vacuously for
it.

`ToneDesignViewModel.SetResultForTests` is the smoke class's seam: the result
block is where a silent resource fault would hide, and a block nothing ever
shows is never laid out.
