# Fire — the editor surfaces: dialogs, settings, chrome, inspector, workspace

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

**The Settings page's two hard constraints, which survived its conversion.**
`Views/Settings/SettingsStyles.axaml` has two hard constraints when it is
converted: it is included by tabs that `SettingsSearchTests` constructs **with no
platform and on any thread**, so it may contain no `Cursor` setter and no
`Transitions` setter — and the cross-tab search matches rows by the
`Grid.FormRow` class and groups by `Border.SettingsGroup` +
`TextBlock.GroupTitle`, so those class names are load-bearing and must survive
any restyle. Fire tokens referenced from there resolve to nothing under a
platform-free construction, which is harmless (the control keeps its default)
— but it means the Settings smoke tests, not the search tests, are what prove
the Fire chrome actually applied.

### The editor's two remaining surfaces joined the layer

**The ~38 DIALOGS and the eight-tab SETTINGS PAGE.** They were the last two
surfaces on the old grammar, and they were converted **by re-pointing their
shared style files, not by editing 46 views**: `Views/Dialogs/DialogStyles.axaml`
(included ONCE, by `Views/Shell/DialogContainerView.axaml`),
`Views/Settings/SettingsStyles.axaml` (included by every tab, and NOT by the
shell that hosts them — #412) and the settings shell (now `Views/Settings/SettingsShellView.axaml`). Every
class NAME survived — several of them are load-bearing — and every SHAPE changed.

Six things this conversion settled:

1. **The class names stay; the ladders change.** The palette and the typeface
   had already gone global, so what still separated these surfaces from a Fire
   screen was radius 6/14 against 8/10/12/16/18, a 15–18 px SemiBold title
   against Chakra 17/20, and an app-scale 8/12/16/20 rhythm beside the Fire
   10/12/14/18/22 one. Both files now name **only** `FireRadius*` / `FireSize*` /
   `FirePad*` / `FireGap*` / `FireSpace*`, pinned by the default-gate
   `UI/FireStyleClassTests.EachConvertedGrammar_ReachesOnlyForFireLadderTokens`
   (plus a companion that the file really SPEAKS the layer, so "no app tokens
   because no tokens at all" cannot pass).
2. **A grammar wears a Fire COMPONENT through its `Theme`, never by copying it.**
   Three are reused: `FirePrimaryButton` for the one primary action in a dialog
   footer AND in the settings footer (so the 45° chamfer, which is a live `Clip`
   bound to the button's own bounds with the glow on an unclipped ancestor,
   reaches 39 surfaces without being restated), `FireToggleSwitch` for every
   boolean in a row, and `FireNavRowToggle` for the settings rail — which is the
   same rail the Play Mode settings modal (§6.5) uses, so the app's two settings
   surfaces are now one component.
3. **The eyebrow went where a string already existed.** The signature lockup is
   an eyebrow over a title, and adding one to 38 dialog heads and ~30 settings
   groups would have meant inventing ~68 pieces of copy (136 resx entries) that
   say nothing the title does not. So the three strings that were ALREADY
   eyebrow-shaped became real Fire eyebrows — `TextBlock.DlgSectionLabel`,
   `TextBlock.AdvancedLabel`, `TextBlock.RailSection` — and the heads keep their
   icon-badge + title lockup, which is `Settings.dc.html`'s own content-header
   shape. The titles moved to the display face instead.
4. **A `Style` beats a `ControlTheme`, and that cuts both ways.** It is what lets
   a footer override the Fire primary's CTA-sized 30 px inset and `.22em`
   tracking (sized for a full-width PLAY NOW; "Einstellungen speichern" at that
   tracking is 300 px of button in a 380 px prompt) — and it is why every ghost
   rule is `Button:not(.Primary)`. A plain `> Button` rule repaints the primary's
   violet fill grey while its chamfered template still applies, which reads as
   the theme half-failing.
5. **Button chrome goes on `/template/ ContentPresenter`, never on the Button.**
   Fluent's Button template ROOT is the presenter, so a fill set on the control is
   simply painted over by any class that paints the presenter — which every class
   in `Styles/Buttons.axaml` does. One build of this conversion set it on the
   control and every Cancel in the app rendered as bare text with no box; it was
   caught in a screenshot, not by a test.
6. **The scrim stays TRANSLUCENT.** `Border.FireOverlayScrim`'s "a scrim is
   opaque" rule is for a full-screen Play Mode modal, where there is nothing
   behind it worth seeing. A dialog here opens over the chart being edited.

### …and in 0.50 the editor's CHROME followed, plus the icon rail came off

**The menu bar, the tool rail, the status rail and every dropdown they open**,
from the top bar and status rail of `Editor Project Settings.dc.html` and
`Rocksmith Song Browser.dc.html` — the two editor mockups. Three new regions in
`Fire.axaml` (`Border.FireMenuBar` 38 · `Border.FireToolbar` ·
`Border.FireStatusBar` 30, all with NO vertical inset, the §6.8 rule), one new
mark (`MenuItem.FireMenuTop`), and a THIRD converted grammar file,
`Views/Shell/ShellChromeStyles.axaml`, included by exactly three views. Four
things it settled:

1. **A DROPDOWN'S CHROME IS A RESOURCE KEY, NEVER A STYLE SELECTOR — and the
   app had it wrong since the selectors were written.** Avalonia's
   `MenuItem`/`ComboBox` templates set their popup Border's Background,
   BorderBrush, BorderThickness, CornerRadius and Padding from a
   `{DynamicResource …}` INSIDE the template. That is a LOCAL value and it beats
   any style setter, so `Styles/Chrome.axaml`'s
   `MenuItem /template/ Popup Border { Background … }` and `Styles/Inputs.axaml`'s
   ComboBox twin were silent no-ops for several releases: every menu in the app
   opened in Fluent's own `#1c1d28` at radius 5 while the surfaces around them
   moved to Fire, and the only setters that ever applied were the ones the
   template does not set (a `BoxShadow`, a `Margin`) — which is exactly what made
   the rules look like they worked. `Styles/FireMenuChrome.axaml` restates the
   keys in Fire values and the grammar merges it, which also SCOPES it: a
   resource lookup from inside a popup walks up to the control that opened it.
   Its literals cannot drift — `UI/EditorChromeTests` compares them to the Fire
   tokens colour by colour.
2. **The ICON RAIL is gone, and its eight buttons went three ways.** It was not
   navigation: six of its icons toggled panels, and it cost 54 px of the one axis
   the editor cannot spare. CHART / LIBRARY / SETTINGS became `MainNavSwitcher`
   in the menu bar (three `RadioButton.FireSegmentItem`s, two-way onto
   `MainWindowViewModel.SelectedMainTabIndex` — the same property the rail drove);
   the inspector / 3D / sheet toggles joined the pedalboard toggle in the tool
   rail's right-hand cluster; and **Play Mode became a File-menu entry**, because
   the rail was the ONLY route into the game shell from a running editor and a
   fourth segment would name a destination that hides the bar it sits on.
3. **The switcher is composition, not invention.** `Border.FireSegment` +
   `RadioButton.FireSegmentItem` already match the mockup's frame (padding 3,
   radius 9, border .08, fill .03) and item (radius 6, mono, violet .28 when
   active) almost exactly. The one divergence is its HEIGHT — 34 against the
   mockup's ~28, because the shared item inset is `12,6` against the mockup's
   `12,4` — and it was left alone: the segmented control is the layer's clearest
   "BUILT ONCE" component and a 2 px win is not worth forking it.
4. **The mono steps needed a fourth state and a transform.**
   `TextBlock.FireMono.Danger` was missing (the error counter is the one thing on
   a status rail that can be bad), and `Converters/TextConverters.ToUpper` exists
   because the layer's "mono is always uppercase" rule is a CONTENT rule that
   bound data cannot satisfy — a project name arrives in whatever case the user
   typed and Avalonia has no `text-transform`.

Guarded by the default-gate `UI/EditorChromeTests` (the rail and `IconBtn`
staying deleted, every destination still reachable, the switcher's element type
and its tab indices read off `MainWindowViewModel`, the popup-key ↔ token
comparison, and a lint that no style rule anywhere sets a template-local popup
property) plus `Perf/ShellConstructionUiPerfSmokeTests.{MenuBar_laysOutItsMenuAndTheThreeWaySwitcher,
TheSwitchersActiveSegment_ReallyLights, Toolbar_carriesThePanelTogglesTheIconRailUsedToOwn,
StatusBar_buildsAttachesAndLaysOutItsMetricCluster}`.

### …and the INSPECTOR followed, as the FOURTH converted grammar

`Views/Editor/InspectorStyles.axaml`, included by `InspectorView` (which hosts
all eight tabs, so the classes cascade regardless of each tab's own DataContext)
plus `MixerInspectorView`, `PhraseCatalogView` and `StemMixerView`. There is no
mockup for the inspector — the layer itself was the reference. Five things it
settled:

1. **The tab strip's ITEMS are the shared segment; its FRAME is not
   `Border.FireSegment`.** A rounded pill frame inside a full-width pane header
   reads as a floating island, against the design's own "no centred islands".
   The strip is a `Border.TabStrip` rail carrying eight
   `ToggleButton.FireSegmentItem`s, so the one component still owns the radius,
   the mono caption, the violet `.28` active fill and the focus ring. It WRAPS,
   and must: eight captions are ~700 px in a ~380 px pane, and German is ~40 %
   longer.
2. **`LocExtension` grew an `Upper` flag**, because "mono is always uppercase"
   is a content rule that could not be satisfied in the resx here: the eight tab
   captions are the mono strip AND the Chakra header of the tab each one opens,
   so half the strip shipped in caps and half in sentence case. It routes
   through the same `TextConverters.ToUpper` the status rail uses.
3. **A converted grammar may legitimately RESTATE app classes.**
   `Button.Subtle`, `ToggleButton.Seg`/`Chip`, `TextBlock.Label`/`Caption`/
   `HelpText`, `PathIcon.SectionIcon` and `Border.PaneHeader` carry ~150
   controls in these tabs; converting without them leaves the surface half on
   each grammar. A `UserControl.Styles` include is applied after the
   application's, so the restatement wins for the four includers and every other
   consumer keeps its old look — the call `SettingsStyles` already made for
   `Border.SettingsGroup`.
4. **`Cursor` is allowed here and `Transitions` is not.** The Cursor ban exists
   only because `SettingsSearchTests` builds settings tabs with no platform;
   nothing constructs an inspector view that way, and dropping the setters would
   cost 42 `Button.Subtle` actions their hand cursor.
   `FireStyleClassTests.TheInspectorGrammar_CarriesNoTransitionsSetter_ButMayCarryACursor`
   asserts both halves so the difference cannot decay into an omission.
5. **Two app keys are NOT ladder steps and stay.** `RowHeightField` (32) and
   `FieldLabelRail` (84) are the pane's geometry; the ladder's nearest row
   height is `FireFieldHeight` = 36, which is a full-width settings row, and
   four pixels a row costs a whole field over a card in a dock pane.

Guards: the default-gate `UI/FireStyleClassTests` (the two `EachConvertedGrammar_*`
theories now carry the file, plus the inspector-specific Transitions/Cursor pair
and `NoInspectorSurface_AnimatesItsOwnChrome`) and
`Perf/InspectorConstructionUiPerfSmokeTests
.{InspectorTabStrip_isTheSharedFireSegment_andEveryHeaderLightsWhenActive,
TheInspectorChrome_ReallyWearsTheFireTypeLadder}`.
---

### …and in 0.51 the editor's WORKSPACE followed the rails

The owner's next report was that the surface *between* the converted rails was
still old. It was: the track rail, the pane headers, the minimap frame, the 3D
preview's editor strip and the whole Dock.Avalonia pane chrome. They are the
FOURTH converted grammar, `Views/Editor/EditorWorkspaceStyles.axaml`. Four
things it settled:

1. **A grammar file's INCLUDE SITE is decided by where the view can end up, not
   by who declares it.** `Border.PaneHeader` used to live in the app-scoped
   `Styles/Containers.axaml`, which reaches every window in the process for
   free. A `StyleInclude` does not — and a docked pane is re-hosted in a
   Dock.Avalonia `HostWindow` the moment it is floated, and `Preview3DView` in a
   plain `Window` the moment it is popped out. So the three views that can leave
   the main window include the file THEMSELVES, and MainWindow includes it, on
   the two editor elements that wear it, for the surfaces it declares directly (the rail and the minimap have no view of
   their own). `UI/EditorWorkspaceChromeTests
   .EveryViewWearingAWorkspaceClass_AlsoIncludesTheGrammar` is the guard, and the
   failure it catches is invisible until someone drags a pane out.
2. **…and never at WINDOW scope (#412).** It used to be, and every Border and
   TextBlock in Play Mode, the song browser, Settings and the prewarmed dialogs
   carried its rules (~54,000 value frames). Every selector stays a CLASS
   selector all the same: each pane's own include covers that whole pane.
3. **The Dock pane chrome is a RESOURCE-KEY bridge, exactly like the dropdowns.**
   `DockFluentTheme`'s templates read their fills, borders and foregrounds from
   `{DynamicResource Dock*Brush}` written inside the template — a local value —
   so a `dock|ToolChromeControl /template/ Border` rule is a silent no-op.
   `Styles/Dock.axaml` restates ~37 keys in literal Fire values (a
   `SolidColorBrush` cannot defer to another brush, and a `ResourceDictionary`
   cannot merge a `<Styles>`), pinned colour-by-colour by
   `UI/EditorWorkspaceChromeTests.EveryDockChromeKey_RestatesItsFireToken`.
4. **The 160 px track rail is the layer's tightest column, so its rows WRAP.**
   A `ToggleButton` fed a string `Content` gets a `ContentPresenter` whose
   TextBlock no `TextWrapping` setter can reach, so the label can only ever be
   trimmed — and all five captions are nouns German lengthens by ~40 %. The rows
   carry a real `TextBlock` CHILD instead, which the grammar wraps; wrapping is
   legal because `FireNavRowToggle`'s presenter sits in a star Grid column, so
   its width really is bound. The rail's one accent is the nav row's own 3 px
   bar: the always-painted stripe down its left edge is gone, because (unlike
   `StackedTracksView`'s, which is bound to `IsActive`) it was unconditional.

The **timeline's own five chrome colours** moved with it —
`ChartTopMarginBg`/`ChartBottomMarginBg`/`ChartBorder`/`ChartMinimapBorder`/
`ChartMeasure` were the last zinc greys in the editor. They stay real
`ThemeTokens` (a community pack still overrides them) and stay OUT of the Fire
layer, which is fixed art direction; `App.axaml` and `ThemePresets.DarkSlate`
carry the same five values and `TheTimelineChromeDefaults_MatchTheDarkSlatePreset`
keeps them in step. The DATA colours beside them (strings, notes, playhead) are
untouched.

Guarded by the default-gate `UI/EditorWorkspaceChromeTests` plus
`Perf/ShellConstructionUiPerfSmokeTests
.{EditorTrackRail_wearsTheFireNavRow_andItsGermanLabelsFitThe160pxColumn,
EditorPaneHeaderAndPreviewStrip_layOutUnderTheWorkspaceGrammar}`.
`Preview3DView` itself is deliberately not constructed in a smoke test — its
ctor builds the Vulkan viewport control, and attaching that headlessly is a
native-HWND lifetime change rather than a layout test.

**The two hard constraints on `SettingsStyles.axaml` are unchanged and now
apply to `DialogStyles.axaml`, `ShellChromeStyles.axaml` and
`EditorWorkspaceStyles.axaml` too.** It is included by tabs that
`SettingsSearchTests` constructs **with no platform and on any thread**, so it may
contain no `Cursor` setter and no `Transitions` setter — extended to both files by
`UI/FireStyleClassTests.NeitherSharedGrammar_CarriesACursorOrTransitionsSetter`.
That is also why a settings control may not simply wear `Button.FireGhost`: the
class sets a `Cursor`. *Wearing* it is harmless (the class lives in the app-scoped
`Fire.axaml`, which a platform-free construction never builds); *copying* it into
a shared grammar file is not, so settings' ghost buttons are a Cursor-free
restatement. And the cross-tab search matches rows by `Grid.FormRow` and groups by
`Border.SettingsGroup` + `TextBlock.GroupTitle`/`GroupSubtitle`, so those names are
load-bearing and must survive any restyle —
`FireStyleClassTests.TheSettingsGrammar_StillDressesEveryRowAndGroupClass`
checks the STYLE half (a whole-token match, because `TextBlock.GroupSubtitleX`
contains `TextBlock.GroupSubtitle` and a `Contains` sailed through the rename it
exists to catch).

Fire tokens referenced from `SettingsStyles.axaml` resolve to nothing under a
platform-free construction, which is harmless (the control keeps its default) —
but it means the two `*ConstructionUiPerfSmokeTests`, not the search tests, are
what prove the Fire chrome actually applied. They now assert the type ladder with
LITERAL expectations ("Chakra Petch" / 20, "Space Grotesk" / 13.5, "IBM Plex
Mono" / 9.5 + tracking), because deriving them from the same token the style
reads would move both sides together and pass straight through the regression.

### The two EDITOR screens

The first Fire surfaces outside Play Mode's shell, from
`Rocksmith Song Browser.dc.html` and `Editor Project Settings.dc.html`.

**`Views/Panels/SongBrowserView`** (deleted 2026-09-23 — the Library is
`Views/Library/LibraryView` over `ViewModels/Panels/LibraryScreen` now; the rules
below still hold for it) — header / filter strip / list + 316 px
detail pane / footer, full bleed. It exists to be the SAME screen as Play Mode's
Song Library, and the shared half is now real code:
`ViewModels/Panels/SongRowFacts` (tuning + capo in ONE cell, amber when either
means work on the guitar, one initial per arrangement) and a generic
`SongArtistGrouping.Stamp`. Three rules it settled:

- **A pinned scrollbar and a pinned caption row cannot both be right.**
  `ScrollViewer.AllowAutoHide="False"` reserves layout width INSIDE the scroll
  viewport and not in the caption row above it, so every column right of the
  flexible one sat 6 px left of its caption — and the offset is the theme's
  scrollbar thickness, so it is worse in the real app than headless. Column
  alignment is this screen's acceptance criterion; the always-on bar lost.
- **A list a user PICKS from renders its pick.** "LOAD WHICH PATH" is a real
  `ListBox.FireList` selection that decides which arrangement the editor opens
  on — the load path grew an optional preferred-arrangement argument for it.
  A legend that looked like a picker would have been the §7 fault.
- **An absent number prints nothing, never 0.** `LibraryArrangementEntry
  .NoteCount` is 0 when the manifest carried no `NotesHard`, and an arrangement
  with genuinely no notes does not ship.

**The settings shell (now `Views/Settings/SettingsShellView`) + the tabs** — the row grammar was already
converted in 0.48; §6.9 adds the CARD reading. Every group opens with a mono
kicker (`TextBlock.GroupKicker`), each rail entry carries a one-line
description, the content column is capped at 1040 and centred, and the footer
carries a real unsaved-changes counter beside `Reset section · Cancel · SAVE
CHANGES`. Two decisions worth carrying forward:

- **A kicker is a CATEGORY, so it is a small shared vocabulary.** Seventeen
  `Settings_Kicker_*` values cover all 43 groups; three groups reading EDITING
  is the information, and one invented string per group could not carry it.
  This overturns `SettingsStyles.axaml`'s earlier "an eyebrow would say nothing
  the title does not" — that was true of a per-group string, not of a category.
- **"N unsaved changes" had no state behind it and one was built.** It began
  as a reflection tracker over the old page's generated properties; since every
  tab migrated (2026-09-22) each tab is its own source through Presentation's
  explicit `SettingsChangeSet`, naming what it counts, and the shell sums them
  ([settings.md](../settings.md)). That is the pattern to copy when a mockup
  asks a screen to count its own pending state: name the settings POSITIVELY,
  so a status field can never be counted.

