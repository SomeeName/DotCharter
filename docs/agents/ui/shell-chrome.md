# Shell chrome — menu bar, toolbar, status rail, popovers (UI deep reference)

One file per subject under `shell-chrome/`. Read the one you need:

- [title-bar-and-shell-styles.md](shell-chrome/title-bar-and-shell-styles.md) — the custom title bar's drag region; ShellStyles.axaml
- [chrome-grammar-and-status-rail.md](shell-chrome/chrome-grammar-and-status-rail.md) — the chart editor chrome grammar; the status rail's readouts and its one message; the activity rail
- [popover-shell.md](shell-chrome/popover-shell.md) — the popover shell (FirePopover)

## Opening a dropped project (#604)

The window's drop and Open Recent share `ProjectMenuViewModel.LoadFile`.
After awaiting the session loader, the funnel raises `ProjectOpened` only when
`ActiveProject` is a new, non-null graph (the path can be the same on a reopen).
`MainWindowViewModel` handles it with `ShowEditor`, which restores charting chrome
and selects the chart editor from Home or Play Mode. Cancelled/failed parses,
audio-only loads and import-dialog routes do not request that navigation.
`ProjectMenuViewModelTests` pins the completion and graph checks;
`DroppedFileRoutingTests` pins the shell subscription.

## Small moves (#494)

Commands only move; none is removed, each stays in the menu bar and in "Go to anything" under the same palette id (`UI/SmallMovesReachabilityTests`).

- **File ▸ Audio** holds Attach audio…, Audio from YouTube… (contested, pruned by `ContestedMenu`) and Separate stems…; Tools ▸ Tones kept Tone designer, Pedalboard and Tuner (three rows, the floor).
- **Screenshot highway** is File ▸ Export ▸ and the camera button `ScreenshotButton` on the 3D preview's editor strip (F12 unchanged).
- **Tempo map ▸ Start & end…** is one dialog (`ITempoMapOperationScreen`) with the five operations as a choice; the five `tempo.*` palette ids open it on their own choice. Tempo map is flat now (the lint's "carries a group" list drops it).
- **Help** has no submenu for diagnostics: Usage statistics and Diagnostics sit below a separator after Check for updates (two rows are under the submenu floor).
- **Edit ▸ Bookmarks…** opens the Bookmarks dialog (`IBookmarksScreen`): ten slots with times, Go to / Set here / Clear. The Shift+0-9 keys and the ten `edit.toggleBookmark.N` palette rows are unchanged.
- **Inspector Song tab** ends with "Edit song metadata…" (`SongOverviewViewModel.OpenSongMetadataCommand`, wired by `MainWindowViewModel`), the same verb as File ▸ Song metadata….

## Chart view polish (#495)

- **Wording follows the control (#532):** Arrangements' visibility solo says "Show only this arrangement"; its chart-sound mute says "Mute chart sound". The View menu's Mixer tab row reuses `Inspector_Tab_Mixer`, matching the Inspector. Keybind titles use Arrangements and Practice tools, as the toolbar panels do.

- **Short labels, full sentence in the tooltip:** Arrangements ▸ Copy sections & phrases (`Menu_CopyStructure`), Tools ▸ Generate ▸ Accept suggested hand shapes / as arpeggios / Chord shape from hand shape; each tooltip starts with the full sentence (`UI/ChartViewPolishMenuTests`). Tone designer and Rig… have tooltips saying what each is for.
- **Grips from held notes** is in Edit ▸ Chord and on Shift+right-click over a
  hand shape. Right-clicking a selected bar with Shift keeps the run selected.
- **Distinct Tools glyphs:** no row shares its glyph with a sibling or its own group (Fix & clean up IconPulse, AI chat IconChat, Linked measures IconSheetMusic, Tones group IconTune, Showlights IconLightbulb).
- **New default chords** for commands that had none: Generate fret hand positions Alt+Shift+F, Merge notes into chord Ctrl+J, Split chord into notes Alt+Shift+J, Fix chart Ctrl+Shift+Q (settings rows, palette gestures, menu rows).
- **Quiet 3D preview and status rail:** see `rendering/scene-and-components/components-and-batching.md` (show-light chip), `ui/rendering-perf/preview-3d-loop.md` (statistics line) and `shell-chrome/chrome-grammar-and-status-rail.md` (audio health dot, no Snap on the rail).
