using Avalonia.Input;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.UI.Constants;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.Services.Session;

namespace DotCharter.UI.Input;

public class AppKeybindRegistrar
{
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IConfigurationService _configService;
    private readonly IEventEditingService _eventEditing;
    private readonly IAppDialogService _dialogs;
    private readonly IGlobalEditingCoordinator _globalEditing;
    private readonly IHighwayEditingService _highwayEditing;
    private readonly MatchPatternStore _patternStore;
    private readonly IChartPenService _pen;
    private readonly IProjectSessionService _projectSession;
    private readonly ProjectState _projectState;
    private readonly IKeybindRegistry _registry;
    private readonly TransportViewModel _transport;
    private readonly LyricsInspectorViewModel _lyrics;
    private readonly MainWindowViewModel _shell;
    private readonly InspectorViewModel _inspector;

    public AppKeybindRegistrar(
        IKeybindRegistry registry,
        ProjectState projectState,
        ChartEditorViewModel chartEditor,
        TransportViewModel transport,
        IGlobalEditingCoordinator globalEditing,
        IHighwayEditingService highwayEditing,
        IChartPenService pen,
        IConfigurationService configService,
        IEventEditingService eventEditing,
        IAppDialogService dialogs,
        IProjectSessionService projectSession,
        LyricsInspectorViewModel lyrics,
        MainWindowViewModel shell,
        InspectorViewModel inspector,
        MatchPatternStore patternStore)
    {
        _patternStore = patternStore;
        _registry = registry;
        _projectState = projectState;
        _chartEditor = chartEditor;
        _transport = transport;
        _globalEditing = globalEditing;
        _highwayEditing = highwayEditing;
        _pen = pen;
        _configService = configService;
        _eventEditing = eventEditing;
        _dialogs = dialogs;
        _projectSession = projectSession;
        _lyrics = lyrics;
        _shell = shell;
        _inspector = inspector;

        // Restore the persisted overlay toggle so each session opens with the
        // user's last choice instead of the renderer default.
        FingeringRenderer.Enabled = _configService.LoadAppConfig().Show3DFingering;
    }

    public void RegisterAll()
    {
        // Settings Cancel/import replace the table; read it afresh on every refresh.

        // === Transport ===
        // One door: TransportViewModel.TogglePlayPause asks the ENGINE which way
        // it goes. Branching on the view model's cached IsPlaying here made Space
        // a no-op the first time it was pressed after a song ran to its end (the
        // engine pauses itself there and nothing told the view model).
        _registry.Register(() => _projectState.Keybinds.PlayPause,
            () => _transport.TogglePlayPauseCommand.Execute(null));
        // v0.32 one-key screenshot (fixed key, the `Key.Add` precedent —
        // no Keybinds-schema entry needed for a fixed binding).
        _registry.Register(() => Key.F12,
            () => _shell.Export.CaptureHighwayScreenshotCommand.Execute(null), "CaptureHighwayScreenshot");

        // The transport bar's grouped popovers. `Play Menus.dc.html` prints
        // these three chords in the Mix / Tools / Tracks footers, and until they
        // were registered the caps were left blank rather than printed — the
        // panels now print them LIVE, so a rebind moves the legend with it.
        _registry.Register(() => _projectState.Keybinds.OpenMixPopover,
            () => _shell.RequestPopover(TransportPopover.Mix));
        _registry.Register(() => _projectState.Keybinds.OpenToolsPopover,
            () => _shell.RequestPopover(TransportPopover.Tools));
        _registry.Register(() => _projectState.Keybinds.OpenTracksPopover,
            () => _shell.RequestPopover(TransportPopover.Tracks));

        _registry.Register(() => _projectState.Keybinds.ToggleMetronome,
            () => _transport.MetronomeEnabled = !_transport.MetronomeEnabled);
        _registry.Register(() => _projectState.Keybinds.ToggleClaps,
            () => _transport.ClapsEnabled = !_transport.ClapsEnabled);
        // The command, not the property: it refuses while the chart cannot sound.
        _registry.Register(() => _projectState.Keybinds.ToggleChartSynth,
            () => _transport.ToggleChartSynthCommand.Execute(null));

        _registry.Register(() => _projectState.Keybinds.SpeedUp,
            () => _transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementNormal));
        // FAST is the coarse one and FINE is the small one: the constants are
        // named after the step size rather than the
        // default modifier.
        _registry.Register(() => _projectState.Keybinds.SpeedUpFast,
            () => _transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementCoarse));
        _registry.Register(() => _projectState.Keybinds.SpeedUpFine,
            () => _transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementFine));
        _registry.Register(() => Key.Add,
            () => _transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementNormal), "SpeedUp");

        _registry.Register(() => _projectState.Keybinds.SpeedDown,
            () => _transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementNormal));
        _registry.Register(() => _projectState.Keybinds.SpeedDownFast,
            () => _transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementCoarse));
        _registry.Register(() => _projectState.Keybinds.SpeedDownFine,
            () => _transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementFine));
        _registry.Register(() => Key.Subtract,
            () => _transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementNormal), "SpeedDown");

        _registry.Register(() => _projectState.Keybinds.DoubleGrid,
            () => _transport.GridDivisor = Math.Min(96, _transport.GridDivisor * 2));
        _registry.Register(() => _projectState.Keybinds.HalveGrid,
            () => _transport.GridDivisor = Math.Max(1, _transport.GridDivisor / 2));

        // === The editor loop + audition (stage 3) ===
        // Charting a hard passage means hearing it repeatedly, and the editor
        // had no loop at all — LoopStart/LoopEnd lived only inside a Live Play
        // session. Audition plays whatever the SHARED target ladder resolves,
        // so "hear this" needs no separate notion of what "this" is.
        _registry.Register(() => _projectState.Keybinds.SetLoopStart, () => _transport.SetLoopStartCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.SetLoopEnd, () => _transport.SetLoopEndCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleLoop, () => _transport.ToggleLoopCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.AuditionSelection, () => _transport.AuditionSelectionCommand.Execute(null));

        // === Navigation ===
        _registry.Register(() => _projectState.Keybinds.ToggleWaveform,
            () => _projectState.ShowWaveform = !_projectState.ShowWaveform);

        _registry.Register(() => _projectState.Keybinds.SeekStart, () => _chartEditor.ExecuteSeek(0));
        _registry.Register(() => _projectState.Keybinds.SeekEnd,
            () => _chartEditor.ExecuteSeek(_projectState.EffectiveTimelineLengthMs));
        _registry.Register(() => _projectState.Keybinds.MoveToFirstItem, () => _chartEditor.ExecuteSeek(0));
        _registry.Register(() => _projectState.Keybinds.MoveToLastItem,
            () => _chartEditor.ExecuteSeek(_projectState.EffectiveTimelineLengthMs));

        _registry.Register(() => _projectState.Keybinds.SeekLeft,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs - ChartEditorConstants.SeekIntervalNormalMs));
        _registry.Register(() => _projectState.Keybinds.SeekLeftFast,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs - ChartEditorConstants.SeekIntervalCtrlMs));
        _registry.Register(() => _projectState.Keybinds.SeekLeftFine,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs - ChartEditorConstants.SeekIntervalShiftMs));

        _registry.Register(() => _projectState.Keybinds.SeekRight,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs + ChartEditorConstants.SeekIntervalNormalMs));
        _registry.Register(() => _projectState.Keybinds.SeekRightFast,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs + ChartEditorConstants.SeekIntervalCtrlMs));
        _registry.Register(() => _projectState.Keybinds.SeekRightFine,
            () => _chartEditor.ExecuteSeek(_projectState.CurrentTimeMs + ChartEditorConstants.SeekIntervalShiftMs));

        // Grid-line seek: the keys above move a fixed number of MILLISECONDS,
        // which never lines up with the grid the notes snap to.
        _registry.Register(() => _projectState.Keybinds.SeekPrevGridLine, () => _chartEditor.SeekToGridLine(-1));
        _registry.Register(() => _projectState.Keybinds.SeekNextGridLine, () => _chartEditor.SeekToGridLine(1));

        // EOF's Seek family (EOF_SRC/menu/song.c:64-94) — one key pair, four
        // granularities, the modifier picks which. See KeybindConfig.
        _registry.Register(() => _projectState.Keybinds.SeekPrevBeat, () => _chartEditor.SeekToBeat(-1));
        _registry.Register(() => _projectState.Keybinds.SeekNextBeat, () => _chartEditor.SeekToBeat(1));
        _registry.Register(() => _projectState.Keybinds.SeekPrevAnchor, () => _chartEditor.SeekToAnchor(-1));
        _registry.Register(() => _projectState.Keybinds.SeekNextAnchor, () => _chartEditor.SeekToAnchor(1));
        // The chord panel's bulk-edit preview marks.
        _registry.Register(() => _projectState.Keybinds.SeekPrevEditTarget, () => _chartEditor.SeekToUseHighlight(-1));
        _registry.Register(() => _projectState.Keybinds.SeekNextEditTarget, () => _chartEditor.SeekToUseHighlight(1));
        _registry.Register(() => _projectState.Keybinds.SeekPrevScreen, () => _chartEditor.SeekByScreen(-1));
        _registry.Register(() => _projectState.Keybinds.SeekNextScreen, () => _chartEditor.SeekByScreen(1));
        _registry.Register(() => _projectState.Keybinds.SeekFirstBeat,
            () => _chartEditor.ExecuteSeek(_chartEditor.Beats.Count > 0 ? _chartEditor.Beats[0].Time : 0));
        _registry.Register(() => _projectState.Keybinds.SeekChartEnd,
            () => _chartEditor.ExecuteSeek(_chartEditor.Beats.Count > 0
                ? _chartEditor.Beats[^1].Time
                : _projectState.EffectiveTimelineLengthMs));
        _registry.Register(() => _projectState.Keybinds.Rewind, () => _chartEditor.ExecuteSeek(0));

        _registry.Register(() => _projectState.Keybinds.SeekNext, () => _chartEditor.SelectNextNote(false));
        _registry.Register(() => _projectState.Keybinds.SeekNextExtend, () => _chartEditor.SelectNextNote(true));
        _registry.Register(() => _projectState.Keybinds.SeekPrev, () => _chartEditor.SelectPrevNote(false));
        _registry.Register(() => _projectState.Keybinds.SeekPrevExtend, () => _chartEditor.SelectPrevNote(true));

        // The inspector work queue: the same motion as SeekNext/SeekPrev, but
        // only over the sounds matching the queue's filter. It SELECTS rather
        // than only seeking, so the panel's fields are live and the per-string
        // verbs have a target — the difference from the old "next missing
        // fingering" button it subsumes.
        _registry.Register(() => _projectState.Keybinds.QueuePrevMatch, () => _inspector.Queue.PreviousMatch());
        _registry.Register(() => _projectState.Keybinds.QueueNextMatch, () => _inspector.Queue.NextMatch());

        _registry.Register(() => _projectState.Keybinds.ArrangementNext, () => _chartEditor.ActivateNextArrangement());
        _registry.Register(() => _projectState.Keybinds.ArrangementPrevious, () => _chartEditor.ActivatePreviousArrangement());

        // === Panel toggles ===
        _registry.Register(() => _projectState.Keybinds.Toggle3DFingering, () =>
        {
            var cfg = _configService.LoadAppConfig();
            cfg.Show3DFingering = !cfg.Show3DFingering;
            _configService.SaveAppConfig(cfg);
            FingeringRenderer.Enabled = cfg.Show3DFingering;
        });
        _registry.Register(() => _projectState.Keybinds.ToggleInspector, () => _shell.ToggleInspectorCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.TogglePreview3D, () => _shell.TogglePreviewCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleAiChat, () => _shell.ToggleAiChatCommand.Execute(null));

        // === Global Editing ===
        _registry.Register(() => _projectState.Keybinds.Undo, () => _globalEditing.Undo());
        _registry.Register(() => _projectState.Keybinds.Redo, () => _globalEditing.Redo());
        _registry.Register(() => _projectState.Keybinds.Copy, () => _globalEditing.CopySelected(_chartEditor.FocusArea));
        _registry.Register(() => _projectState.Keybinds.Cut, () =>
        {
            _globalEditing.CopySelected(_chartEditor.FocusArea);
            _globalEditing.DeleteSelected(_chartEditor.FocusArea);
        });
        _registry.Register(() => _projectState.Keybinds.Paste,
            () => _globalEditing.Paste(_projectState.CurrentTimeMs, _transport.GridDivisor));
        _registry.Register(() => _projectState.Keybinds.PasteAtMouse,
            () => _globalEditing.Paste(_projectState.LastPointerTimeMs, _transport.GridDivisor));
        _registry.Register(() => _projectState.Keybinds.PasteSpecial, () => _shell.ShellDialogs.OpenPasteSpecialCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.SelectAll, () =>
        {
            if (_chartEditor.FocusArea == EditorFocusArea.TimelineEvents)
                _globalEditing.SelectAllEvents();
            else
                _globalEditing.SelectAllNotes();
        });
        _registry.Register(() => _projectState.Keybinds.SelectLike, () => _globalEditing.SelectLikePattern());
        _registry.Register(() => _projectState.Keybinds.PatternBuilder, () => _shell.ShellDialogs.OpenPatternBuilderCommand.Execute(null));

        // Drop the last section again at the playhead without the dialog. 44
        // markers in one session, 18 of them 'chorus' — everything after the
        // first of a repeat is pure repetition. Routed through the shell so the
        // key and the command palette run ONE implementation.
        _registry.Register(() => _projectState.Keybinds.StampSection, () => _shell.StampSectionCommand.Execute(null));

        // Every user-authored match pattern, re-enumerated on each RefreshBinds.
        // This is the one registration whose SIZE is data rather than code — see
        // IKeybindRegistry.RegisterDynamic.
        _registry.RegisterDynamic(BuildPatternBindings);
        _registry.Register(() => _projectState.Keybinds.Delete, () => _globalEditing.DeleteSelected(_chartEditor.FocusArea));
        _registry.Register(() => _projectState.Keybinds.DeleteRelated, () => _globalEditing.DeleteSelectedRelated(_chartEditor.FocusArea));
        // Duplicate = copy + paste-at-mouse — quick clone at the cursor
        // position. The verb lives on the view model so the timeline context
        // menu's "Duplicate" and this keybind stay one code path.
        _registry.Register(() => _projectState.Keybinds.DuplicateSelection,
            () => _shell.DuplicateSelectionAt(_projectState.LastPointerTimeMs));
        _registry.Register(() => _projectState.Keybinds.SnapSelected, () => _globalEditing.SnapSelectedNotes(_transport.GridDivisor));
        _registry.Register(() => _projectState.Keybinds.SnapAll, () => _globalEditing.SnapAllNotes(_transport.GridDivisor));

        // === Note Modifiers ===
        // The ceiling is the ACTIVE ARRANGEMENT's lane count, not the project's.
        // A project-level lane count would be wrong — ChartData is the
        // PROJECT-level record and its tuning vector is stamped once from the
        // first arrangement, so in a project with a 6-string lead and a 4-string
        // bass, editing the bass would clamp to string 5 and push notes onto two
        // lanes the bass has not got and the renderer does not draw.
        // An arrow means the direction the charter can SEE. The highway lays
        // string 0 at the TOP unflipped, so Up has to DECREMENT the index there —
        // it incremented, which is why up moved the note down and down moved it
        // up. See StringLaneDirection.
        _registry.Register(() => _projectState.Keybinds.StringUp, () => MoveStringOnScreen(towardTop: true));
        _registry.Register(() => _projectState.Keybinds.StringDown, () => MoveStringOnScreen(towardTop: false));
        // Same lane step, same screen direction — the note keeps its PITCH and
        // gets re-fretted for the new string (Guitar Pro's Alt+Up / Alt+Down).
        _registry.Register(() => _projectState.Keybinds.StringUpSamePitch, () => MoveStringOnScreen(towardTop: true, samePitch: true));
        _registry.Register(() => _projectState.Keybinds.StringDownSamePitch, () => MoveStringOnScreen(towardTop: false, samePitch: true));
        _registry.Register(() => _projectState.Keybinds.FretUp, () => _highwayEditing.MoveSelectedFretUp());
        _registry.Register(() => _projectState.Keybinds.FretDown, () => _highwayEditing.MoveSelectedFretDown());
        // ONE command, ONE history node. A 12-iteration loop would let
        // a single keystroke on a barre chord spend twelve of the 500 history
        // nodes, run the spacing enforcer twelve times and mint up to eleven
        // intermediate chord templates (nothing collects templates, so they
        // would stay). The per-string clamp makes the single ±12 step identical to
        // twelve ±1 steps.
        _registry.Register(() => _projectState.Keybinds.FretUpOctave, () => _highwayEditing.MoveSelectedFretByOctave(1));
        _registry.Register(() => _projectState.Keybinds.FretDownOctave, () => _highwayEditing.MoveSelectedFretByOctave(-1));

        _registry.Register(() => _projectState.Keybinds.ToggleHopo, () => _highwayEditing.ToggleSelectedHopo());
        _registry.Register(() => _projectState.Keybinds.ToggleHarmonic, () => _highwayEditing.ToggleSelectedHarmonic());
        _registry.Register(() => _projectState.Keybinds.ToggleMute, () => _highwayEditing.ToggleSelectedMute());
        _registry.Register(() => _projectState.Keybinds.ToggleLinkNext, () => _highwayEditing.ToggleSelectedLinkNext());
        _registry.Register(() => _projectState.Keybinds.ToggleVibrato, () => _highwayEditing.ToggleSelectedVibrato());
        _registry.Register(() => _projectState.Keybinds.ToggleTremolo, () => _highwayEditing.ToggleSelectedTremolo());
        _registry.Register(() => _projectState.Keybinds.TogglePicking, () => _highwayEditing.ToggleSelectedPicking());
        _registry.Register(() => _projectState.Keybinds.ToggleAccent, () => _highwayEditing.ToggleSelectedAccent());
        _registry.Register(() => _projectState.Keybinds.ToggleIgnore, () => _highwayEditing.ToggleSelectedIgnore());
        _registry.Register(() => _projectState.Keybinds.TogglePassOtherNotes, () => _highwayEditing.ToggleSelectedPassOtherNotes());

        _registry.Register(() => _projectState.Keybinds.ToggleHopoGroup, () => _highwayEditing.ToggleSelectedHopoGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleHarmonicGroup, () => _highwayEditing.ToggleSelectedHarmonicGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleMuteGroup, () => _highwayEditing.ToggleSelectedMuteGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleLinkNextGroup, () => _highwayEditing.ToggleSelectedLinkNextGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleVibratoGroup, () => _highwayEditing.ToggleSelectedVibratoGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleTremoloGroup, () => _highwayEditing.ToggleSelectedTremoloGroup());
        _registry.Register(() => _projectState.Keybinds.TogglePickingGroup, () => _highwayEditing.ToggleSelectedPickingGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleAccentGroup, () => _highwayEditing.ToggleSelectedAccentGroup());
        _registry.Register(() => _projectState.Keybinds.ToggleIgnoreGroup, () => _highwayEditing.ToggleSelectedIgnoreGroup());
        _registry.Register(() => _projectState.Keybinds.TogglePassOtherNotesGroup, () => _highwayEditing.ToggleSelectedPassOtherNotesGroup());

        // === Tools ===
        _registry.Register(() => _projectState.Keybinds.EditFhp, () => _shell.ShellDialogs.OpenFhp());
        _registry.Register(() => _projectState.Keybinds.AnchorFretUp, () => NudgeAnchor(1, AnchorNudgeTarget.Fret));
        _registry.Register(() => _projectState.Keybinds.AnchorFretDown, () => NudgeAnchor(-1, AnchorNudgeTarget.Fret));
        _registry.Register(() => _projectState.Keybinds.AnchorWidthUp, () => NudgeAnchor(1, AnchorNudgeTarget.Width));
        _registry.Register(() => _projectState.Keybinds.AnchorWidthDown, () => NudgeAnchor(-1, AnchorNudgeTarget.Width));
        _registry.Register(() => _projectState.Keybinds.GenerateHandShape, () => _globalEditing.GenerateHandShapeFromSelection());
        _registry.Register(() => _projectState.Keybinds.GenerateArpeggio, () => _globalEditing.GenerateArpeggioFromSelection());
        _registry.Register(() => _projectState.Keybinds.SplitHandShape, () => _globalEditing.SplitHandShapeAtSelection());
        // The two accept verbs of the suggested bars: the same coordinator call the
        // Tools menu's rows and the palette reach through the shell's commands.
        _registry.Register(() => _projectState.Keybinds.AcceptSuggestedHandShapes,
            () => _globalEditing.AcceptSuggestedHandShapesInSelection(arpeggio: false));
        _registry.Register(() => _projectState.Keybinds.AcceptSuggestedArpeggios,
            () => _globalEditing.AcceptSuggestedHandShapesInSelection(arpeggio: true));
        // The chord-shape palette's keyboard half: one verb, owned by the Chord shapes pane.
        _registry.Register(() => _projectState.Keybinds.RepeatPreviousChord,
            () => _inspector.ChordShapes?.RepeatPreviousChordCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.DoubleTime, () => _globalEditing.ApplyDoubleTimeToSelection());
        _registry.Register(() => _projectState.Keybinds.HalfTime, () => _globalEditing.ApplyHalfTimeToSelection());

        // === Zoom & Length ===
        // Viewport-center anchored while Follow is off (0.16 6.6);
        // zoom-around-playhead as ever while Follow is on.
        _registry.Register(() => _projectState.Keybinds.ZoomIn,
            () => TimelineNavigation.ApplyZoom(_projectState, ChartEditorConstants.ZoomInFactor));
        _registry.Register(() => _projectState.Keybinds.ZoomOut,
            () => TimelineNavigation.ApplyZoom(_projectState, ChartEditorConstants.ZoomOutFactor));
        // Through the shell's command, so the key, the menu and the palette share one target
        // rule (selected sounds, then hand shapes, then the lyric — TimelineWheelAdjuster).
        _registry.Register(() => _projectState.Keybinds.IncreaseLength,
            () => _shell.IncreaseLengthCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.DecreaseLength,
            () => _shell.DecreaseLengthCommand.Execute(null));

        // === File Ops ===
        _registry.Register(() => _projectState.Keybinds.SaveProject, () => { _ = _projectSession.SaveProjectAsync(); });
        _registry.Register(() => _projectState.Keybinds.SaveProjectAs, () => { _ = _projectSession.SaveProjectAsAsync(); });
        _registry.Register(() => _projectState.Keybinds.OpenProject, () => { _ = _projectSession.OpenProject(); });
        _registry.Register(() => _projectState.Keybinds.NewProject, () => _shell.Imports.OpenNewProjectCommand.Execute(null));

        // === Bookmarks (BookmarkBase modifier + 0..9) ===
        for (var i = 0; i <= 9; i++)
        {
            var idx = i;
            _registry.Register(
                () => new KeyChord((Key)((int)Key.D0 + idx), _projectState.Keybinds.BookmarkBase.Modifiers),
                () => _chartEditor.ToggleBookmarkAtPlayhead(idx),
                nameof(KeybindConfig.BookmarkBase));
        }

        // === Set Fret (SetFretBase modifier + 0..9) ===
        // With the keyboard pen ON these are the pen's TYPED digits: multi-digit
        // inside the accumulator window ("1" then "2" = fret 12, the whole run
        // one undo step), transposing the shape instead when no string mask is
        // set, and targeting the sound at the playhead when nothing is selected.
        // With the pen OFF this is byte-for-byte the pre-v0.35 single-digit
        // "set the selection's fret" — the branch lives inside the pen service
        // so there is one call site.
        for (var i = 0; i <= 9; i++)
        {
            var fret = i;
            _registry.Register(
                () => new KeyChord((Key)((int)Key.D0 + fret), _projectState.Keybinds.SetFretBase.Modifiers),
                () => _pen.TypeFretDigit(fret, _transport.GridDivisor),
                nameof(KeybindConfig.SetFretBase));
        }

        // === String-edit mask (StringMaskBase modifier + 1..9 → string 0..8) ===
        // The persistent per-string selector every per-string verb routes
        // through: fret +-/set, mute, HOPO, harmonic, picking, vibrato, tremolo,
        // link-next. An EMPTY mask means every string, so pressing the same
        // digit twice returns to "all". Drawn on the 2D lane headers.
        //
        // MaxStrings is 9 because there are nine digit KEYS — it is not a claim
        // about the arrangement. `Ctrl+7` on a 4-string bass would set a mask
        // bit with no lane header to draw it on, and an invisible mask makes
        // every per-string verb a silent no-op: precisely the invisible modal
        // state the mask contract forbids. Refuse instead.
        for (var i = 0; i < StringEditMask.MaxStrings; i++)
        {
            var stringIndex = i;
            _registry.Register(
                () => new KeyChord((Key)((int)Key.D1 + stringIndex), _projectState.Keybinds.StringMaskBase.Modifiers),
                () =>
                {
                    if (!StringDigitTargeting.IsAddressable(stringIndex, _projectState.ActiveArrangement)) return;
                    _projectState.ToggleStringEditMask(stringIndex);
                },
                nameof(KeybindConfig.StringMaskBase));
        }

        _registry.Register(() => _projectState.Keybinds.StringMaskClear, () => _projectState.ClearStringEditMask());

        // === The keyboard pen (PenToggleStringBase modifier + 1..9 → string 0..8) ===
        // Note ENTRY, the verb ~100 editing keybinds did not have: toggle a
        // string into the sound at the PLAYHEAD — create / add to the chord /
        // remove / delete the sound when the last string goes. Registered
        // unconditionally so the row is rebindable and collision-checked;
        // IChartPenService is INERT until AppConfig.UseFastChartingInput is on.
        //
        // Bounded by the ARRANGEMENT, not by the nine digit keys:
        // ApplyNotePlacementCommand never consults the string count, so a bare
        // `6` on a 4-string bass created a note on string index 5 — a lane the
        // renderer does not draw and the pointer cannot reach.
        for (var i = 0; i < StringEditMask.MaxStrings; i++)
        {
            var stringIndex = i;
            _registry.Register(
                () => new KeyChord((Key)((int)Key.D1 + stringIndex), _projectState.Keybinds.PenToggleStringBase.Modifiers),
                () =>
                {
                    if (!StringDigitTargeting.IsAddressable(stringIndex, _projectState.ActiveArrangement)) return;
                    _pen.ToggleString(stringIndex, _transport.GridDivisor);
                },
                nameof(KeybindConfig.PenToggleStringBase));
        }

        // === Misc ===
        _registry.Register(() => _projectState.Keybinds.TapLyric, () => _lyrics.TapNext());
        _registry.Register(() => _projectState.Keybinds.ToggleLyricWordPart, () => _lyrics.ToggleSelectedWordPart());
        _registry.Register(() => _projectState.Keybinds.ToggleLyricPhraseEnd, () => _lyrics.ToggleSelectedPhraseEnd());
        _registry.Register(() => _projectState.Keybinds.ApplyHandShapeTemplateToChords,
            () => _shell.ApplyHandShapeTemplateToChordsCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.InsertBeatAtPlayhead,
            () => _shell.InsertBeatAtPlayheadCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.RemoveBeatAtPlayhead,
            () => _shell.RemoveBeatAtPlayheadCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.InsertVocalAtPlayhead,
            () => _shell.InsertVocalAtPlayheadCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleChordSplit,
            () => _shell.ToggleChordSplitCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleChordOnlyBox,
            () => _shell.ToggleChordOnlyBoxCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleChordPassOtherNotes,
            () => _shell.ToggleChordPassOtherNotesCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ConvertToPowerChordSmall,
            () => _shell.ConvertToPowerChordSmallCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ConvertToPowerChordBig,
            () => _shell.ConvertToPowerChordBigCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.MutedStrumsToChord,
            () => _shell.MutedStrumsToChordCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.ToggleGhostGems,
            () => _shell.ToggleGhostGemsCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.TappedHarmonic,
            () => _shell.TappedHarmonicCommand.Execute(null));

        _registry.Register(() => _projectState.Keybinds.SetFingerNone,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(-1));
        _registry.Register(() => _projectState.Keybinds.SetFingerThumb,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(0));
        _registry.Register(() => _projectState.Keybinds.SetFinger1,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(1));
        _registry.Register(() => _projectState.Keybinds.SetFinger2,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(2));
        _registry.Register(() => _projectState.Keybinds.SetFinger3,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(3));
        _registry.Register(() => _projectState.Keybinds.SetFinger4,
            () => _inspector.SetFingerOnSelectedStringsCommand.Execute(4));

        // === Named phrase catalog ===
        // Routed through the shell so this block needs no new constructor
        // parameter — the catalog pane VM hangs off MainWindowViewModel.
        _registry.Register(() => _projectState.Keybinds.TogglePhraseCatalog,
            () => _shell.TogglePhraseCatalogCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.PhraseCatalogAdd,
            () => _shell.PhraseCatalog.CaptureSelectionCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.PhraseCatalogPaste,
            () => _shell.PhraseCatalog.PasteSelectedCommand.Execute(null));
        _registry.Register(() => _projectState.Keybinds.PhraseCatalogPasteByName,
            () => _shell.FocusPhraseCatalogSearchCommand.Execute(null));
    }

    /// <summary>
    ///     Three verbs per stored <see cref="MatchPattern" />: select every
    ///     occurrence, and step to the next / previous one. Read fresh on every
    ///     <see cref="IKeybindRegistry.RefreshBinds" /> so a pattern added,
    ///     rebound or deleted in the builder takes effect on Save with no restart.
    ///     <para>
    ///         The labels are <c>pattern:{id}:{verb}</c> because the builder
    ///         filters its OWN binds out of the collision warning by that prefix —
    ///         a pattern must not report that it conflicts with itself.
    ///     </para>
    /// </summary>
    internal IEnumerable<KeybindRegistration> BuildPatternBindings()
    {
        foreach (var pattern in _patternStore.List())
        {
            // Captured per iteration: the closures outlive this loop.
            var id = pattern.Id;

            if (pattern.SelectKey.IsBound)
            {
                yield return new KeybindRegistration(pattern.SelectKey,
                    () => RunPattern(id, PatternVerb.Select), $"pattern:{id}:select");
            }

            if (pattern.NextKey.IsBound)
            {
                yield return new KeybindRegistration(pattern.NextKey,
                    () => RunPattern(id, PatternVerb.Next), $"pattern:{id}:next");
            }

            if (pattern.PreviousKey.IsBound)
            {
                yield return new KeybindRegistration(pattern.PreviousKey,
                    () => RunPattern(id, PatternVerb.Previous), $"pattern:{id}:previous");
            }
        }
    }

    /// <summary>
    ///     Resolves the pattern by ID at PRESS time, not at registration time, so
    ///     editing a pattern's facets takes effect immediately — the binding is a
    ///     pointer at stored data, never a snapshot of it.
    /// </summary>
    private void RunPattern(string id, PatternVerb verb)
    {
        var pattern = _patternStore.Get(id);
        if (pattern == null) return;

        if (verb == PatternVerb.Select)
        {
            _globalEditing.SelectByPattern(pattern);
            return;
        }

        var target = _globalEditing.MoveToPatternMatch(
            pattern, _projectState.CurrentTimeMs, verb == PatternVerb.Next ? 1 : -1);
        if (target.HasValue) _chartEditor.ExecuteSeek(target.Value);
    }

    /// <summary>
    ///     The capo is read at PRESS time from the arrangement being edited, not
    ///     captured at registration: the anchor floor is <c>capo + 1</c>, and
    ///     switching arrangements must not leave the nudge clamping against the
    ///     previous one's capo.
    /// </summary>
    private void NudgeAnchor(int delta, AnchorNudgeTarget target) =>
        _highwayEditing.AdjustSelectedAnchors(delta, target, _chartEditor.ActiveArrangement?.Capo ?? 0);

    /// <summary>
    ///     Moves the selected sounds one lane toward the top or the bottom of the
    ///     HIGHWAY, whichever way the string index happens to run there.
    /// </summary>
    private void MoveStringOnScreen(bool towardTop, bool samePitch = false)
    {
        var flip = _projectState.LeftyFlip ^ _projectState.InvertStrings;
        var step = StringLaneDirection.IndexStep(flip, towardTop);
        var strings = StringDigitTargeting.StringCountOf(_projectState.ActiveArrangement);

        if (samePitch)
        {
            _highwayEditing.MoveSelectedStringPreservingPitch(step, strings);
            return;
        }

        if (step > 0) _highwayEditing.MoveSelectedStringUp(strings);
        else _highwayEditing.MoveSelectedStringDown();
    }

    private enum PatternVerb
    {
        Select,
        Next,
        Previous
    }
}
