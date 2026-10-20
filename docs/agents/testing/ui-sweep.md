# The headless UI sweep (`UiSweepUiPerfSmokeTests` + `UiSweep.cs`)

Deep reference for `DotCharter.Tests/AGENTS.md`. One pass over EVERY view in
`DotCharter.UI/Views`: construct → attach to a real `Window` → `Show()` → lay out
→ interrogate the tree, then write a contact sheet. Built for "check every dialog,
button, menu and view" without a window ever reaching the desktop. It lives under
`Perf/`, so it is OUTSIDE the default gate and runs as its own
`dotnet test --filter-class "*UiPerfSmokeTests"` invocation — a
PROCESS boundary, not the teardown hang this used to be blamed on. See
[perf-harnesses.md](perf-harnesses.md) for the 2026-08-23 measurement.

The bullets below sit under `Perf/` in the source guide; the nesting is kept
as-is so the text is byte-identical to what it replaced.

  - **`UiSweepUiPerfSmokeTests` + `UiSweep.cs` — the headless UI sweep.** One
    pass over EVERY view in `DotCharter.UI/Views`: construct → attach to a real
    `Window` → `Show()` → lay out → interrogate the tree, then write a contact
    sheet. Built for "check every dialog, button, menu and view" without a
    window ever reaching the desktop.
    - **The catalogue is DERIVED, not listed.** `UiSweepCatalog.ViewTypes()`
      reflects over the compiled `DotCharter.UI` assembly for every
      `Control` under `DotCharter.UI.Views` with a public parameterless ctor, so
      a new view is swept the day it compiles.
      `Catalog_coversEveryViewFileInTheRepo` ties that back to the repo: every
      `.axaml` under `Views/` whose root is not a `ResourceDictionary`/`Styles`
      is either swept or in `UiSweepCatalog.Exclusions` **with a reason**
      (`EveryExclusion_carriesAReason` enforces the reason). Two exclusions
      today: `Preview3DView` (initializes a real Vulkan device) and `MainWindow`
      (it owns the host window and the prewarm pipeline).
    - **It realizes the HIDDEN subtrees too, or it grades ~5% of Play Mode.**
      `PlayModeView` is 8,900 lines over sixteen named `*ScreenRoot`s behind 280
      `IsVisible` gates, and Avalonia skips `MeasureCore` for an invisible
      element — so until 0.54 only whichever screen happened to be current was
      ever laid out and every check silently passed over Menu, Song Select,
      Get Ready, Countdown, Playing, Pause, Failed, Results and the rig
      (Co-op Setup, Stats, Calendar, Network, History and Trends were removed
      on 2026-09-19). The sweep now consumes
      **`IPrewarmableSurfaces`** where the view declares it (parent before
      child) and otherwise the hidden `*Root` controls, and realizes each
      through the product's own `HiddenSubtreePrewarmer.Realize` — so the sweep
      is also a live test of the path that keeps the first switch inside its
      budget. **It must never WRITE `IsVisible`** (source-linted by
      `UI/UiSweepCoverageTests`, comments stripped). Measured A/B with the
      shell's own `DOTCHARTER_NO_SURFACE_PREWARM=1`: controls CHECKED
      12,787 → 23,331 overall, **PlayModeView 256 → 4,169** (of 4,987 visuals),
      SettingsView 966 → 3,660, DialogContainerView 4 → 3,652.
      Visibility inside a realized root is judged RELATIVE to it —
      `IsEffectivelyVisible` is false for everything under a hidden root — and a
      control is attributed to the OUTERMOST root that can see it, so a nested
      gated pane is not reported twice.
    - **A view that inherits its style grammar is GIVEN it.** Six of the seven
      inspector tabs and `ChordTemplateEditorView` carry no `StyleInclude`:
      `InspectorView` includes `InspectorStyles.axaml` once for all of them.
      Swept alone they matched no selector and every size reported for them was
      of an unstyled tree — `NotesInspectorView` measured 723x135 bare against
      587x221 styled. `UiSweepCatalog.InheritedStyleIncludes` DERIVES the
      grammar (a view with no include of its own is matched against every view
      that declares it as an element, recursing when that host has none either)
      and the sweep adds it to the host window. Pinned in the DEFAULT gate by
      `UI/UiSweepCoverageTests`, in both directions — a view that includes its
      own must be given nothing.
    - **`clipped-text` is an ERROR on every surface, hidden or visible (0.57).**
      It was demoted to a warning for hidden surfaces while its backlog stood at
      seven — `UiSweepRunner.HiddenSurfaceClippedTextSeverity` — and that backlog
      is now empty and the constant is gone. The seven, and what fixed them:
      Get Ready's left rail is a FIXED 404 px holding a 92 px label plus a
      segmented strip, which left the six-up speed row 17 px of content per cell
      (`"100"` needs 22.8) and the three-up chord row 58 (`"Standard"` needs
      60.8) — both fixed by a **`.FireSegmentItem.Dense`** variant
      (`FirePadSegmentItemDense`, 6,6 against the shared 12,6) applied to those
      two strips only, because every other segmented strip in the app has the
      room; `"2.3 S ON SCREEN"` at 106.5 px in the same 92 px label column now
      wraps, which costs the strip beside it nothing; and four stem-model names
      at 283.9–331.1 px in the settings shell's 275 px control column now wrap
      like the description already under them. **If it ever needs demoting
      again, restore the backlog list with it** — a class that fails the run
      permanently is a class nobody reads.
      **It is swept in THREE cultures, and it took a second backlog to empty.**
      `AppConfig.LanguageCulture` defaults to `"system"`, so before the fixture
      pinned a culture the run silently followed the developer's Windows
      language. Pinning it to `en-US` made the gate deterministic and left
      German and Japanese layout ungraded — so the pin is an env var,
      `DOTCHARTER_HEADLESS_CULTURE` (`HeadlessSessionFixture.SweptCulture`), and
      the release script re-runs this class plus
      `SettingsConstructionUiPerfSmokeTests` under `de-DE` and `ja-JP`.
      Measured 2026-08-28 at `883a39c2`, de-DE produced six `clipped-text`
      errors and ja-JP three (a subset — Japanese strings are shorter, so only
      the tightest three slots overran). All are FIXED; each fix was the slot,
      not the string:
      | Surface | de-DE | ja-JP | Fix |
      |---|---|---|---|
      | `DiagnosticsWindow` "AKTIVES PROTOKOLL" | 120.7 in 92 | — | the three path labels in that FIXED 92 px column wrap (the rows are Auto-height) |
      | `PlayModeView` TuningCheck "Einrichtung überspringen" | 166.8 in 116 | 135.1 in 116 | the Get Ready footer's fixed 150 px column is `Auto` + `MinWidth="150"` on the cell |
      | `SettingsView` page subtitle | 514.2 in 470 | — | wraps (the search box beside it is a fixed 320, so this column is never guaranteed) |
      | `SettingsView` KeybindsTab "Tastenbelegung" | 148.5 in 137 | — | the Danger "reset" button beside it is `MaxWidth="200"` with a wrapping label, so the `*` title column keeps its width |
      | `OverflowPopoverView` "Projekt speichern" | 97.4 in 83 | 89.7 in 83 | the Edit group is 2 + 1 instead of three-across (`FirePopoverGridAction` is a fixed 32 px high, so the label cannot wrap out of it) |
      | `TracksPopoverView` "Keine Gesangsspur" | 114.9 in 98 | 113.8 in 98 | the vocals `ComboBox` is 188 px, not 150 — a ComboBox placeholder has no ellipsis |
      Two `SettingsConstructionUiPerfSmokeTests
      .NothingInAControlColumn_IsArrangedPastTheRowsRightEdge` offenders went
      with them, both the horizontal-StackPanel-in-a-fixed-300-px-column trap:
      "SICHERUNG & UMZUG" arranged to 991 px in an 896 px row and "Netzwerk
      durchsuchen" to 969 — both now `WrapPanel`s, the same fix the
      Check-for-updates row above them already carried. ja-JP hit the FIRST of
      those two as well ("バックアップと移行", 1006 px in 896): a shorter
      script is not a narrower BUTTON PAIR, and that row's two buttons overrun
      in every language but English.
      **Do not add a blanket `TextTrimming` to make a finding go away.** An
      ellipsis is excluded from this check by design, so a trimming setter on a
      shared class (a ComboBox placeholder, a `ControlTheme` part) silences the
      detector for every control that wears it, not just the one that was red.
      Note the `.Dense` variant had to be spelled for BOTH `ToggleButton` and
      `RadioButton`: `FireClassReachabilityTests` reads selectors literally, and
      a `:is(ToggleButton)` selector alone makes the RadioButton usage look like
      a typo. It caught that within one run.
    - **The DataContext is derived too, from the markup's ROOT `x:DataType`** —
      the compiled-binding declaration the product itself relies on. It reads
      both `clr-namespace:` and `using:` prefixes and resolves the type in the
      UI assembly or in `DotCharter.Presentation` (the redesign views bind a
      contract from there; an unresolved type used to fall back to the shell
      view model and report every command as a `dead-command-binding`). The
      instance comes from the live shell view-model graph first, then the real
      DI container, then `ActivatorUtilities` over that container; NOTHING is
      invented. A real three-arrangement chart (from `StressChartFactory`, named
      "UI sweep fixture") goes into the shared `ProjectState` before the shell
      VM is harvested, and **each surface's own `IsOpen` is flipped true** — 39
      of these views are dialog scrims, and unopened they measure an empty
      overlay. That one line took the sweep from 11k laid-out visuals to 21k.
      The VMs are DI singletons, so `IsOpen` is put back afterwards or one open
      dialog shows through every later capture.
    - **A surface whose content is a LIST is seeded too, and until v0.44 it was
      not.** An `ItemsControl` with no items instantiates NO `ItemTemplate`, so
      the markup where a list surface keeps almost all of its layout was never
      constructed, never styled and never measured — and every check passed it
      by having nothing to look at. The recovery dialog was reported clean
      while its row template held the overlap the owner then found in a
      screenshot. `UiSweepListSeeder` puts ONE item into every empty
      `ItemsControl` that has an `ItemTemplate`: it builds the list's own
      ELEMENT type, from the container at the top level and then from the
      type's own constructors, filling `required` members (a compile-time
      contract reflection walks straight past — a reflectively built
      `RecoverableSession` arrives with a null `Metadata` and the row VM over it
      throws on its first line). Rules that are load-bearing:
      - **the recursion never leaves `DotCharter.*` and asks the CONTAINER only
        for the item type itself.** Resolving the types found by walking
        constructors and properties constructs arbitrary SERVICES — that is how
        a list seeder ends up building the JUCE engine and taking the testhost
        down, which it did.
      - **strings and dates get a representative value, not `default`.** An
        empty string measures zero, so a row built from type defaults can never
        clip, overflow or overlap — the template would be laid out and still
        show none of its faults. `SampleText`/`SampleInstant` are FIXED so two
        sweeps of an unchanged tree agree. Booleans, enums and numbers are left
        alone: those decide what a row MEANS, and writing them invents state.
      - **seeding is not realizing.** Adding an item queues that `ItemsControl`
        with the layout manager and does NOT mark its ancestors invalid, so
        `root.Measure(sameSize)` returns immediately and the rows stay unbuilt —
        silently, with the sweep still green. Only `UpdateLayout()` drains it.
        `UiSweepResult` reports `SeededLists` and `RealizedRows` separately and
        `Sweep_seedsEmptyListsAndActuallyLaysOutTheirRows` asserts both; a
        minority of seeded lists legitimately realize nothing (a collapsed HUD
        strip, an unselected tab), so it asserts in bulk.
      - **seeding runs BEFORE the hidden sub-surfaces are realized, and the two
        COMPOUND.** A hidden element is skipped by `MeasureCore`, not removed
        from the tree, so an `ItemsControl` inside a gated screen is found and
        seeded straight away and its rows are then built by the realization
        pass. Neither mechanism reaches those rows alone: realization alone
        measures an empty list, seeding alone never measures it. Seed AFTER and
        every gated list goes back to empty. `RealizedRows` is therefore counted
        after `InspectHiddenSurfaces`, not in the seeding block.
      - **a list it cannot build is REPORTED (`unseeded-list`), never skipped.**
        Four are, today; `TechniqueQueryOption` does `choices[0]` in its ctor,
        so seeding it would mean inventing a choice.
    - **Findings, by severity.** ERRORS fail the run: `construction-failed`
      (anything thrown while building/attaching/laying out — this is what
      catches `Padding="{DynamicResource Space*}"`), `measured-zero`,
      `dead-command-binding` (Avalonia logged a failed binding onto a `Command`
      — the button that draws and does nothing), and — since 0.53, on every
      surface since 0.57 — **`clipped-text`**. WARNINGS are reported, never asserted:
      `overflows-parent`, `cell-content-overlap`, `clipped-seeded-text`,
      `inert-action-control`, `collapsed-at-rest`, `unseeded-list`,
      `binding-error`, `null-source-binding`.
      `clipped-text` was promoted once its backlog was empty: it had found nine
      DEFAULT keybind chords sliced mid-word and "Duplicate" sliced in ENGLISH
      in two three-across dialog rows, and after those the class came back empty
      across all 80 surfaces. The check already excludes text that WRAPS or
      ELLIPSIZES, so a finding is text with no plan for not fitting — which is
      the one failure class here that is invisible to every bounds assertion,
      because the TextBlock measured fine and it is the CELL that is too small.
      **Seeding the empty lists re-opened it** — an item template that had never
      been measured cannot have been clean. One was a real product fault and was
      FIXED rather than added to a backlog: `StackedTracksView`'s per-track
      actions sit in a header column pinned to 118 px (it must line up with the
      main timeline's header), leaving a 77 px slot for "Send selection here" at
      96 px in ENGLISH; the captions wrap now. **The hidden-surface backlog those
      findings did not join has since been cleared to zero and its demotion
      constant deleted (0.57) — see the `clipped-text` bullet above.**
    - **…but a finding on the SEEDER'S OWN sample string is
      `clipped-seeded-text`, always a warning.** `UiSweepListSeeder.SampleText`
      is 33 characters because that file says so, so failing the run on it would
      gate the build on the harness's choice of string rather than on anything
      the product does. It is still reported — a slot that cannot hold 33
      characters is worth knowing — but under its own kind, so it can never be
      read as a product finding or quietly dilute the promoted `clipped-text`.
      Four today (2026-08-11), all inside gated Play Mode screens — the two on
      the Appearance tab's theme-color cards were fixed by letting the card
      text wrap rather than backlogged.
    - **`overflows-parent` on a REALIZED HIDDEN ROOT'S DIRECT CHILD is the
      PREWARM's arrange rect, not a product layout (0.58).**
      `HiddenSubtreePrewarmer.Realize` measures and arranges each direct visual
      child of a hidden root at `Rect(SlotSizeOf(root))` — the root's whole
      layout SLOT. For a root that stretches, its own bounds ARE that slot and
      nothing looks out of place. For a CONTENT-SIZED root it is not:
      `SongBrowserView`'s `BrowserEmptyStates` is `HorizontalAlignment="Center"
      VerticalAlignment="Center" MaxWidth="460"` and was arranged 409x105 by a
      real pass, so realizing it handed its card the full cell and the sweep read
      **`StackPanel 'BrowserFirstRunState' escapes its Grid 'BrowserEmptyStates'
      (409x105) by 400x320 px`** — the largest warning in the report, and a
      layout no user can ever see (showing the empty state re-measures the whole
      cell and the card lands centred). `IsPrewarmArrangeRect` stands the check
      down for exactly that: the root's box must differ from its slot AND the
      child must sit entirely inside the slot rect. Both halves are needed —
      matching on "is a direct child" alone would hide a real overflow, and the
      child is NOT at the origin (`Arrange` resolves its own alignment inside
      that rect, so the card landed at 400,320).
    - **A finding names the control now, not just its type.** "Grid escapes its
      Grid" identifies two of the hundred Grids on a surface. Findings carry the
      `x:Name` or the style classes (`Describe`), the child's own rect, and the
      nearest named ancestor (`NearestNamed`) — which is what turned the 2 px
      `PlayModeView` warning from anonymous into `under StackPanel
      'GameSettingsNav'` in one run.
    - **The two overflow warnings that remain are BY DESIGN and must not be
      "fixed".** `MixPopoverView` (x3) and `TempoPopoverView` (x2) report
      `Grid 'HorizontalTemplate' escapes its Grid 'SliderContainer' (146x20) by
      0x6 px — child at 0,-6 146x32`: one per SLIDER, and it is the thumb
      headroom `Styles/Inputs.axaml` un-clips on purpose (a 14 px thumb painting
      its shadow and its 1.15/1.25 hover/press scales outside a 20 px row).
      `Perf/SliderThumbHeadroomUiPerfSmokeTests` REQUIRES that overhang, so
      removing it would trade one warning for one failure and a sliced thumb.
    - **`collapsed-at-rest` carries its own reason now, DERIVED from the tree**
      (`CollapseReason`) — the class stood unexplained long enough to be
      re-investigated from scratch more than once, and the answer was always one
      of two facts the tree already knows. An `ItemsControl` with no items is a
      host swept on its own whose rows are declared elsewhere (`EditorMenu`, a
      `Menu` subclass whose eight headers live in `Views/Shell/Menus/`); an
      all-hidden subtree is a host with nothing open in it
      (`DialogContainerView`). A hand list of "allowed to be empty" was rejected
      for the reason it always is — it goes stale silently — but a derived FACT
      cannot. Anything matching neither shape is the case worth reading.
    - **`cell-content-overlap` is a separate detection class because nothing
      else can see it (v0.44).** `overflows-parent` compares a control against
      its OWN parent and stands down for anything under a `ScrollViewer` —
      i.e. every list row in the app. And arranged bounds do not show the fault
      at all: a horizontal `StackPanel` measures its children with INFINITE
      width, so its own `DesiredSize` is clamped to the slot it was given while
      its children are arranged at their natural widths from x=0 and nothing
      clips. The panel reports a perfectly correct rectangle while painting
      outside it, into the neighbouring Grid cell. The check unions each Grid
      child's whole PAINTED subtree and reports a pair of children in different
      cells whose paint intersects. Children of the SAME cell are skipped
      (stacking two mutually exclusive tags is how a Grid is used on purpose),
      the walk STOPS at a `ClipToBounds` element or a scroll presenter (content
      past one is not drawn, and clipping is a sanctioned fix), and `Popup` is
      excluded — it reports the PLACEMENT rect of a separate window, which every
      ComboBox template puts next to its dropdown glyph and which was 31 of the
      31 findings on the first run. Visibility is the CALLER's set, not
      `IsEffectivelyVisible`, so the check runs unchanged over a realized hidden
      surface — where that property is false for everything.
    - **The binding signal comes from `Logger.Sink`, not from property
      inspection** — a `{Binding}` whose path does not exist never pushes a
      value, so `Command` is indistinguishable from a button that declared none.
      `UiSweepLogSink` records `LogArea.Binding` warnings instead.
      **Messages ending "Value is null." are DROPPED**: those are ordinary
      null intermediates (nothing selected/loaded) and the first run buried four
      real diagnostics under ninety of them; they are counted, not listed.
    - **`measured-zero` on a stretching `UserControl` is a weak check and the
      harness says so** — an arranged view fills its rect whether or not its
      content survived. `collapsed-at-rest` (zero DESIRED size) is the real
      signal, and it is a WARNING because the dialog host, the command palette
      and the HUD strips are legitimately empty at rest; telling those apart
      needs a hand list, and a hand list goes stale silently.
    - **Note for anyone adding a broken-binding regression test: a view with
      `x:DataType` compiles its bindings, so a bad path is an AVLN2000 BUILD
      error, not a runtime one.** Only `{ReflectionBinding}` and untyped
      `DataTemplate` bindings reach the runtime check.
    - `DOTCHARTER_UI_SWEEP_DUMP=1` additionally writes a PNG per surface. It
      switches the shared session to `UseHeadlessDrawing = false` (Skia) in
      `HeadlessAppHelper` — the only mode in which `CaptureRenderedFrame`
      returns anything. Still headless. Artifacts land in
      `TestResults/ui-sweep/` (`index.html` contact sheet + `summary.txt`);
      the assertions run identically without it.
    - **Each surface is also walked as UIA sees it (#311).** After the hidden
      screens are realized, `AccessibilityAudit.Audit` walks the AUTOMATION
      tree (`ControlAutomationPeer.CreatePeerForElement` → `GetChildren`) and
      stores the result on `UiSweepResult.Accessibility`; a realized hidden root
      reports as `View/RootName`. `Perf/AccessibilityAuditUiPerfSmokeTests`
      reads it: the per-screen table goes to the test output and
      an `a11y-audit` markdown file in the sweep's artifact folder, the unnamed-control counts are a
      ratchet, and song rows (which a fresh profile has none of) are graded on
      the gallery fakes. A scroll bar's buttons and `AccessibilityView="Raw"`
      peers are not graded — Narrator's control view does not show them.
    - **The sweep runs ONCE per process** (`UiSweepRunner.Run()` caches). The
      surfaces share the app's DI singletons, so a second sweep starts from
      state the first left behind and does not reproduce it.
