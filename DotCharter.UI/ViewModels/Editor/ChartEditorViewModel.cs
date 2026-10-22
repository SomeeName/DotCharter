using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Constants;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.ViewModels.Editor.Linking;

namespace DotCharter.UI.ViewModels.Editor;

public partial class ChartEditorViewModel : ViewModelBase, IChartEditorSurface
{
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly TransportViewModel _transport;
    private readonly WaveformOnsetSnapper _onsetSnapper = new();

    [ObservableProperty] private VocalTrack? _activeVocalTrack;
    [ObservableProperty] private EditorFocusArea _focusArea = EditorFocusArea.NoteHighway;
    [ObservableProperty] private ObservableCollection<LyricPhrase> _lyricPhrases = [];

    /// <summary>The difficulty level the editor shows, and the way back to the hardest.</summary>
    public DifficultyLevelSelector Levels { get; }

    public ChartEditorViewModel(
        ProjectState projectState,
        TransportViewModel transport,
        IHistoryService historyManager,
        ISelectionService selectionManager,
        ITempoService tempoManager,
        ITrackContextRegistry trackContexts,
        ILocalizationService localization)
    {
        _projectState = projectState;
        _localization = localization;
        _transport = transport;
        HistoryManager = historyManager;
        historyManager.HistoryChanged += () => Interlocked.Increment(ref _historyRevision);
        Selection = selectionManager;
        Tempo = tempoManager;
        TrackContexts = trackContexts;
        Levels = new DifficultyLevelSelector(projectState);

        _projectState.PropertyChanged += (_, e) =>
        {
            // The flip toggle has to reach the surfaces OUTSIDE a viewport — the
            // inspector's chord box and its per-string rows — or it inverts the
            // highway and leaves them pointing the other way.
            if (e.PropertyName is nameof(ProjectState.FlipStrings) or nameof(ProjectState.LeftyFlip)
                or nameof(ProjectState.InvertStrings))
            {
                OnPropertyChanged(nameof(FlipStrings));
            }

            if (e.PropertyName == nameof(ProjectState.ShowSuggestedHandShapes)) RequestRedraw();

            if (e.PropertyName is nameof(ProjectState.PlacedNotes) or nameof(ProjectState.PlacedChords)
                or nameof(ProjectState.HandShapes) or nameof(ProjectState.Anchors)
                or nameof(ProjectState.EventPoints) or nameof(ProjectState.Beats)
                or nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveProject))
            {
                if (e.PropertyName != null) OnPropertyChanged(e.PropertyName);
                if (e.PropertyName == nameof(ProjectState.PlacedNotes)) OnPropertyChanged(nameof(Notes));
                if (e.PropertyName == nameof(ProjectState.PlacedChords)) OnPropertyChanged(nameof(Chords));
                if (e.PropertyName == nameof(ProjectState.ActiveArrangement))
                {
                    OnPropertyChanged(nameof(Tones));
                    OnPropertyChanged(nameof(FocusedTrack));
                    OnPropertyChanged(nameof(ActiveRibbon));
                }
                if (e.PropertyName == nameof(ProjectState.ActiveProject))
                {
                    OnPropertyChanged(nameof(Arrangements));
                    OnPropertyChanged(nameof(Showlights));
                    OnPropertyChanged(nameof(VocalTracks));
                }

                ChartDataChanged?.Invoke();
            }
        };

        // Focus is NOT ActiveArrangement (ITrackContextRegistry says so), and it
        // moves on its own: ToolCoordinator.HandlePointerPressed focuses whatever
        // stacked panel was clicked, and GlobalEditingCoordinator focuses the
        // target of a Send-selection-here — both deliberately leaving
        // ActiveArrangement alone. Until this subscription existed, FocusedTrack
        // and ActiveRibbon only re-raised on an ActiveArrangement change, so
        // View ▸ Timeline Rows (five `ChartEditor.ActiveRibbon.*` two-way
        // bindings in MainMenuView) went on toggling the PREVIOUS track's ribbon
        // object after either of those gestures — a checkbox drawn over a model
        // that is not the one it writes.
        //
        // No unsubscribe: registry and editor are both app-lifetime singletons
        // (App.EditingServices / App.ViewModelServices), and in tests they are
        // constructed and collected as a pair.
        TrackContexts.FocusedTrackChanged += () =>
        {
            OnPropertyChanged(nameof(FocusedTrack));
            OnPropertyChanged(nameof(ActiveRibbon));
        };
    }

    /// <summary>
    ///     The one string-orientation answer, forwarded from
    ///     <c>ProjectState.FlipStrings</c> so surfaces outside a viewport — the
    ///     inspector's chord box and its per-string rows — follow the same flip
    ///     toggle the highway does.
    /// </summary>
    public bool FlipStrings => _projectState.FlipStrings;

    /// <summary>The playhead, read on demand by the timeline's automation peer
    /// (never bound: it moves at up to 60 Hz).</summary>
    public double PlayheadMs => _projectState.CurrentTimeMs;

    /// <summary>The editor's localization, for the views it owns that XAML
    /// constructs (the timeline's automation peer) and so cannot take one
    /// through a constructor.</summary>
    public ILocalizationService Localization => _localization;

    public ChartData? CurrentChart
    {
        get => _projectState.ActiveProject?.Chart;
        set
        {
            // Fix: Added safety checks to prevent possible null reference assignment
            var activeProject = _projectState.ActiveProject;
            if (activeProject != null && value != null)
            {
                activeProject.Chart = value;
                OnPropertyChanged();
            }
        }
    }

    public Arrangement? ActiveArrangement
    {
        get => _projectState.ActiveArrangement;
        set
        {
            _projectState.ActiveArrangement = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     The ACTIVE arrangement's own metadata — its name, its tuning — changed
    ///     in place. Forwarded to <see cref="ProjectState.NotifyActiveArrangementChanged" />,
    ///     which is where the reasoning lives: <c>Arrangement</c> raises nothing,
    ///     so a binding through this property has nothing else to hear.
    /// </summary>
    public void NotifyActiveArrangementMetadataChanged() => _projectState.NotifyActiveArrangementChanged();

    public void SetActiveArrangement(Arrangement? arrangement) => ActiveArrangement = arrangement;

    /// <summary>
    ///     Re-publishes the chart so everything bound to it re-reads, then announces the
    ///     active arrangement: its NAME and TUNING are not on <c>ChartData</c>, so the
    ///     toolbar's Tracks button, the Tracks popover's picker and the Tools popover's
    ///     drop-tune label only re-read on that second raise
    ///     (<c>ArrangementMetadataRefreshTests</c>). Also the arrangement Save's undo/redo callback.
    /// </summary>
    public void RepublishActiveArrangement()
    {
        var chart = CurrentChart;
        if (chart != null)
        {
            CurrentChart = null!;
            CurrentChart = chart;
        }

        NotifyActiveArrangementMetadataChanged();
    }

    public ObservableCollection<Arrangement> Arrangements =>
        _projectState.ActiveProject?.Arrangements ?? [];

    /// <summary>The open project itself, for the few things that are PROJECT-level
    /// rather than arrangement-level — the authored tone bodies most of all, which
    /// live on the project while everything that references them lives on an
    /// arrangement.</summary>
    public ChartProject? ActiveProject => _projectState.ActiveProject;

    public UserSettingsState UserSettings => _projectState.UserSettings;

    public ObservableCollection<VocalTrack> VocalTracks =>
        _projectState.ActiveProject?.VocalTracks ?? [];

    private static readonly ObservableCollection<Showlight> EmptyShowlights = [];

    public ObservableCollection<Showlight> Showlights =>
        _projectState.ActiveProject?.Showlights ?? EmptyShowlights;

    public ObservableCollection<ToneChange> Tones =>
        ActiveArrangement?.ToneChanges ?? [];

    public ObservableCollection<GuitarNote> PlacedNotes => _projectState.PlacedNotes;
    public ObservableCollection<Chord> PlacedChords => _projectState.PlacedChords;
    public ObservableCollection<BeatMarker> Beats => _projectState.Beats;
    public ObservableCollection<FretAnchor> Anchors => _projectState.Anchors;
    public ObservableCollection<EventPoint> EventPoints => _projectState.EventPoints;
    public ObservableCollection<HandShape> HandShapes => _projectState.HandShapes;

    /// <summary>What a dynamic-difficulty run draws instead of the active level; null otherwise.</summary>
    public ChartLevel? GameplayLevel => _projectState.GameplayLevel;

    /// <summary>The level every edit addresses (<see cref="PlacedNotes" />' owner).</summary>
    public ChartLevel? ActiveLevel => _projectState.ActiveLevel;
    private static readonly ObservableCollection<Bookmark> EmptyBookmarks = [];
    public ObservableCollection<Bookmark> Bookmarks =>
        _projectState.ActiveProject?.Bookmarks ?? EmptyBookmarks;

    private static readonly ObservableCollection<ChartAnnotation> EmptyAnnotations = [];
    public ObservableCollection<ChartAnnotation> Annotations =>
        _projectState.ActiveProject?.Annotations ?? EmptyAnnotations;

    public ObservableCollection<GuitarNote> Notes => PlacedNotes;
    public ObservableCollection<Chord> Chords => PlacedChords;

    public ISelectionService Selection { get; }

    /// <summary>
    /// Per-arrangement editing contexts. The focused track's selection /
    /// preview / hover state is what UI commands and keybindings act on.
    /// In Phase 1 every context's selection is the same global instance —
    /// later phases give each track its own.
    /// </summary>
    public ITrackContextRegistry TrackContexts { get; }

    public TrackEditorContext? FocusedTrack => TrackContexts.FocusedTrack;

    /// <summary>
    /// Convenience handle to the focused track's ribbon visibility flags
    /// so the main timeline header can bind tone/section/phrase toggles
    /// the same way the multi-track stacked rows do.
    /// </summary>
    public RibbonVisibility? ActiveRibbon => FocusedTrack?.Ribbon;

    // Made private to satisfy the analyzer warnings
    private IHistoryService HistoryManager { get; }

    private long _historyRevision;

    /// <summary>Moves on every undo-history change (do, undo, redo, a merged
    /// gesture tick, clear) — the content version the renderers' per-chart
    /// latches key on (<see cref="SharedRepeatChords" />). Read from the 3D render thread
    /// too, hence the interlocked read.</summary>
    public long HistoryRevision => Interlocked.Read(ref _historyRevision);

    /// <summary>The dashed hand-shape suggestions, computed ONCE per level and edit and read by
    /// every surface that shows or hits them: the timeline and its stacked rows, the song map and
    /// the hand-shape tool. A latch per reader ran the whole-level detector once each per edit.</summary>
    public HandShapeSuggestionLatch SharedHandShapeSuggestions { get; } = new();

    /// <summary>The repeat-chord decision, computed ONCE per level and edit and read by the 2D
    /// timeline rows and the 3D render thread alike (a thread-safe latch).</summary>
    public RepeatChordLatch SharedRepeatChords { get; } = new();

    /// <summary>The link group every timeline emphasises or flashes.</summary>
    public LinkHighlight LinkHighlight { get; } = new();

    /// <summary>
    ///     The linked-measures surfaces (panel, toast, Inspector banner), for the menus and the
    ///     palette to bind through. It needs the app config, which this view model does not take,
    ///     so it is its own singleton and registers itself here when it is constructed (the
    ///     Inspector view model takes it, so that happens with the shell).
    /// </summary>
    [ObservableProperty] private LinkedMeasuresViewModel? _linkedMeasures;

    /// <summary>The hand shapes the inspector's last validation pass found E05/D09
    /// against (<see cref="DotCharter.Services.Editing.Validation.HandShapeFindings" />);
    /// the 2D timeline tints their bars. Published by the inspector, never
    /// derived here, so the bar and the issue list cannot disagree.</summary>
    /// <summary>The 2D timeline draws the suggested hand shapes — View ▸ Show
    /// suggested hand shapes.</summary>
    public bool ShowSuggestedHandShapes => _projectState.ShowSuggestedHandShapes;

    public IReadOnlySet<HandShape> HandShapeFindings { get; private set; } =
        DotCharter.Services.Editing.Validation.HandShapeFindings.None;

    /// <summary>Swaps in a new finding set and redraws — only when it differs, as
    /// the inspector re-validates on a debounce after every edit.</summary>
    public void PublishHandShapeFindings(IReadOnlySet<HandShape> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (ReferenceEquals(findings, HandShapeFindings) || HandShapeFindings.SetEquals(findings)) return;
        HandShapeFindings = findings;
        RequestRedraw();
    }

    /// <summary>The chords, hand shapes and anchors the editor marks as a pending
    /// change: the chord panel's bulk-edit preview or, while one is open, the AI
    /// chart pass's review, which wins. Outlined on the 2D timeline, marked on
    /// the song map. Published by their owners off the resolver their write goes
    /// through, never derived here.</summary>
    public ChordUseHighlight UseHighlight { get; private set; } = ChordUseHighlight.None;

    private ChordUseHighlight _panelHighlight = ChordUseHighlight.None;
    private ChordUseHighlight _reviewHighlight = ChordUseHighlight.None;
    private ChordUseHighlight _draftMergeHighlight = ChordUseHighlight.None;

    /// <summary>Raised when <see cref="UseHighlight" /> changes; the song map repaints.</summary>
    public event EventHandler? UseHighlightChanged;

    /// <summary>The chord panel's preview: swaps in a new marked set and redraws, only
    /// when the shown members differ.</summary>
    public void PublishUseHighlight(ChordUseHighlight highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        _panelHighlight = highlight;
        ShowHighlight();
    }

    /// <summary>The AI chart pass review's marks; <see cref="ChordUseHighlight.None" />
    /// hands the marks back to the chord panel.</summary>
    public void PublishReviewHighlight(ChordUseHighlight highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        _reviewHighlight = highlight;
        ShowHighlight();
    }

    /// <summary>The chart-from-audio merge card's phrase marks: shown under an open AI
    /// review, over the chord panel's preview.</summary>
    public void PublishDraftMergeHighlight(ChordUseHighlight highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        _draftMergeHighlight = highlight;
        ShowHighlight();
    }

    private void ShowHighlight()
    {
        var shown = !_reviewHighlight.IsEmpty ? _reviewHighlight
            : !_draftMergeHighlight.IsEmpty ? _draftMergeHighlight
            : _panelHighlight;
        if (shown.SameAs(UseHighlight)) return;
        UseHighlight = shown;
        UseHighlightChanged?.Invoke(this, EventArgs.Empty);
        RequestRedraw();
    }

    /// <summary>Seeks to the previous (<paramref name="direction" /> &lt; 0) or next
    /// marked sound on the level on screen, wrapping round; false when it has none.</summary>
    public bool SeekToUseHighlight(int direction)
    {
        if (UseHighlight.Step(PlacedChords, Beats, _projectState.CurrentTimeMs, direction) is not { } timeMs)
            return false;
        ExecuteSeek(timeMs);
        return true;
    }
    private ITempoService Tempo { get; }

    /// <summary>Undo chain rows for the toolbar hover preview (element 0 =
    /// what the next Ctrl+Z reverts). Rebuilt on demand by
    /// <see cref="RefreshHistoryPreviews"/> right before the flyout opens —
    /// no reason to track history churn while nobody is looking.</summary>
    public ObservableCollection<Dialogs.HistoryPreviewItem> UndoPreview { get; } = [];

    /// <summary>Redo chain rows along the preferred branch, same contract.</summary>
    public ObservableCollection<Dialogs.HistoryPreviewItem> RedoPreview { get; } = [];

    /// <summary>Names a history step in the UI language.</summary>
    private string DescribeStep(string description) =>
        DotCharter.Services.Editing.HistoryActionText.Describe(description, key => _localization[key]);

    public void RefreshHistoryPreviews()
    {
        UndoPreview.Clear();
        var step = 1;
        foreach (var s in HistoryManager.GetUndoSteps())
        {
            UndoPreview.Add(new Dialogs.HistoryPreviewItem(step,
                DescribeStep(s.Description),
                s.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                IsCurrent: step++ == 1));
        }

        RedoPreview.Clear();
        step = 1;
        foreach (var s in HistoryManager.GetRedoSteps())
        {
            RedoPreview.Add(new Dialogs.HistoryPreviewItem(step,
                DescribeStep(s.Description),
                s.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                IsCurrent: step++ == 1));
        }
    }

    public ObservableCollection<GuitarNote> PreviewNotes => Selection.PreviewNotes;
    public ObservableCollection<Chord> PreviewChords => Selection.PreviewChords;
    public ObservableCollection<HandShape> PreviewHandShapes => Selection.PreviewHandShapes;
    public ObservableCollection<EventPoint> PreviewEventPoints => Selection.PreviewEventPoints;
    public ObservableCollection<FretAnchor> PreviewAnchors => Selection.PreviewAnchors;

    public ObservableCollection<GuitarNote> SelectedNotes => Selection.SelectedNotes;
    public ObservableCollection<Chord> SelectedChords => Selection.SelectedChords;
    public ObservableCollection<HandShape> SelectedHandShapes => Selection.SelectedHandShapes;
    public ObservableCollection<FretAnchor> SelectedAnchors => Selection.SelectedAnchors;

    public event Action? ChartDataChanged;

    /// <summary>
    ///     Swaps in a project graph.
    /// </summary>
    /// <param name="project">The new graph.</param>
    /// <param name="resetHistory">
    ///     True when this is a genuine LOAD (open, new, import, PSARC, checkpoint restore)
    ///     — the undo stack belongs to the project that just went away and must not
    ///     survive into this one. Left false for an in-place graph swap that happens
    ///     INSIDE an undoable action (see <c>ChartMaintenanceService.RestoreProjectSnapshot</c>),
    ///     where clearing the history would destroy the very stack being replayed.
    ///     <para>
    ///         Without this, opening a second project left the first project's undo stack
    ///         live: Ctrl+Z appeared to do nothing (the actions mutate the old, now
    ///         detached graph), the title bar sprouted a phantom "*" for a project the
    ///         user never edited, and the entire previous project — plus every action's
    ///         captured before/after state — was retained for the life of the session.
    ///     </para>
    /// </param>
    public void ApplyProject(ChartProject project, bool resetHistory = false)
    {
        if (resetHistory) HistoryManager.Clear();

        // Selection holds direct references INTO the graph, and the selection
        // service is an app-wide singleton — so any swap leaves it pointing at
        // notes/chords/shapes that belong to a project that is not on screen. Edit
        // commands then mutate the detached graph (silently doing nothing the
        // user can see) and the whole old project stays pinned in memory behind
        // those references. Unconditional: this is true of an in-place swap too,
        // where the incoming graph is a fresh clone with all-new instances.
        Selection.ClearAll();

        _projectState.ActiveProject = project;

        // UNCONDITIONAL: an `if (project.Arrangements.Any())` guard
        // would be a live data-corruption bug, not a safety check.
        //
        // ProjectState.ActiveArrangement's setter already carries the fix for
        // exactly this fault one layer down — read the comment on it: nulling
        // the arrangement is what cascades ActiveLevel to null, and
        // ChartDocumentState.PlacedNotes/PlacedChords/HandShapes/Anchors and
        // IEditingScope all resolve off ActiveLevel. Guarding the ASSIGNMENT
        // meant the setter never ran, so opening a project with no arrangements
        // left ActiveArrangement AND ActiveLevel pointing into the project that
        // had just been closed — and every edit after that landed in the closed
        // project's level while the new one was on screen.
        //
        // A zero-arrangement project is reachable: XmlChartSerializer handles
        // vocals-only RS XML explicitly (VocalTracks.Count > 0 &&
        // Arrangements.Count == 0). It also pinned the whole closed graph.
        _projectState.ActiveArrangement = project.Arrangements.FirstOrDefault();

        ActiveVocalTrack = project.VocalTracks.FirstOrDefault();
    }

    partial void OnActiveVocalTrackChanged(VocalTrack? value)
    {
        LyricPhrases = value?.Phrases ?? new ObservableCollection<LyricPhrase>();
    }

    // Methods decorated with [RelayCommand] should typically be private
    // as the Toolkit will generate the public ICommand (e.g., UndoCommand)
    [RelayCommand]
    private void Undo() =>
        // Guarded for the same reason the keybind funnel is — see HistoryStepGuard.
        HistoryStepGuard.Run(HistoryManager.Undo, "Undo", _projectState, null, _localization);

    [RelayCommand]
    private void Redo() => HistoryStepGuard.Run(HistoryManager.Redo, "Redo", _projectState, null, _localization);

    /// <summary>
    /// The grid snap with note magnetism: an existing note/chord closer than the
    /// grid line wins, so a tuplet or an off-grid import stays reachable without
    /// changing the divisor. The magnet itself is the shared, pure
    /// <see cref="SoundMagnetSnap"/> — the keyboard pen snaps through the same
    /// one, so keyboard and pointer place at the same position.
    /// </summary>
    public FractionalPosition SnapToGrid(double timeMs)
    {
        var beats = Beats;
        var gridSnap = Tempo.SnapToGrid(timeMs, beats, _transport.GridDivisor);
        return SoundMagnetSnap.Apply(timeMs, gridSnap, beats, PlacedNotes, PlacedChords);
    }

    /// <summary>
    /// Snaps a lyric start time to the grid (and nearby notes, via
    /// <see cref="SnapToGrid"/>) but lets a waveform onset win when one sits at
    /// least as close to the cursor as the grid line. Gives the vocal editor
    /// "snap to the sung attack" without changing note-highway snapping. Falls
    /// back to the plain grid snap when no waveform is loaded.
    /// </summary>
    public FractionalPosition SnapLyricStartMs(double timeMs)
    {
        var gridPos = SnapToGrid(timeMs);
        var beats = Beats;
        var waveform = _projectState.WaveformData;
        if (beats.Count == 0 || waveform is null || !waveform.HasData) return gridPos;

        var radiusMs = Math.Max(20.0, EstimateGridStepMs(timeMs, beats) * 0.5);
        var onsetMs = _onsetSnapper.FindNearestOnsetMs(waveform, timeMs, radiusMs);
        if (onsetMs is null) return gridPos;

        var gridMs = gridPos.GetPositionMs(beats);
        return Math.Abs(onsetMs.Value - timeMs) <= Math.Abs(gridMs - timeMs)
            ? TimelineMath.PositionFromMs(onsetMs.Value, beats)
            : gridPos;
    }

    private double EstimateGridStepMs(double timeMs, IList<BeatMarker> beats)
    {
        var idx = TimelineMath.BinarySearchBeats(beats, timeMs);
        if (idx < 0) idx = 0;
        var nextTime = idx + 1 < beats.Count ? beats[idx + 1].Time : beats[idx].Time + 500;
        var beatMs = Math.Max(1.0, nextTime - beats[idx].Time);
        var divisor = Math.Max(1, _transport.GridDivisor);
        return beatMs / divisor;
    }

    public void RequestRedraw() => ChartDataChanged?.Invoke();

    public void ExecuteSeek(double timeMs)
    {
        // Effective length, not the audio length: a chart-only project has
        // TotalAudioLengthMs == 0, and clamping to that pinned EVERY seek to 0
        // — no ruler/minimap seeks, no Home/End, and selection navigation
        // (inspector queue, next/prev-note) selected offscreen sounds without
        // the view ever moving, because this funnel is what brings them in.
        timeMs = Math.Clamp(timeMs, 0, _projectState.EffectiveTimelineLengthMs);
        _projectState.CurrentTimeMs = timeMs;
        _transport.SeekTo(timeMs);
        // ExecuteSeek is the deliberate-navigation funnel (minimap, sheet
        // strip, keybind seeks, inspector jumps, dialogs). A deliberate seek
        // re-locks the decoupled viewport onto the playhead so the sought
        // position is what you see (0.16 6.6). Viewport pans never call this.
        _projectState.TimelineFollowsPlayhead = true;
    }

    /// <summary>
    /// Steps the playhead to the previous / next GRID LINE (<paramref name="direction"/>
    /// −1 / +1) — the one row of the editing-rework audit's frequent-operations
    /// table DotCharter answered with "none". The arrow keys move a fixed number
    /// of MILLISECONDS, so on a 6/8 bar or after a tempo change they never line
    /// up with the grid the notes snap to; this is what makes "seek, press a
    /// string key" land on a beat. Deliberately over <see cref="ITempoService"/>'s
    /// plain nearest-grid snap, NOT <see cref="SnapToGrid"/> — magnet-snapping
    /// onto sounds is PgUp/PgDn's job.
    /// </summary>
    public void SeekToGridLine(int direction)
    {
        var beats = Beats;
        if (beats.Count == 0) return;

        var divisor = Math.Max(1, _transport.GridDivisor);
        var now = _projectState.CurrentTimeMs;
        var nearest = Tempo.SnapToGrid(now, beats, divisor);
        var target = GridNavigation.Step(now, nearest, nearest.GetPositionMs(beats), divisor, direction);
        ExecuteSeek(target.GetPositionMs(beats));
    }

    /// <summary>
    ///     Step to the previous / next BEAT MARKER (<paramref name="direction" />
    ///     −1 / +1) — EOF's bare PgUp/PgDn (EOF_SRC/menu/song.c:91-92). Distinct
    ///     from <see cref="SeekToGridLine" />, which walks the SUBDIVISION grid:
    ///     the beat is the musical pulse and is where structure (sections,
    ///     phrases, anchors) belongs, which is why EOF gives it the bare row.
    /// </summary>
    public void SeekToBeat(int direction)
    {
        var beats = Beats;
        if (beats.Count == 0) return;

        var now = _projectState.CurrentTimeMs;
        // A tolerance so "previous" from a hair past a beat goes to the one
        // BEFORE it, not back onto the beat the playhead is visually sitting on.
        var epsilon = ChartEditorConstants.SeekNextPrevBufferMs;
        var target = direction < 0
            ? beats.LastOrDefault(b => b.Time < now - epsilon)
            : beats.FirstOrDefault(b => b.Time > now + epsilon);

        if (target != null) ExecuteSeek(target.Time);
    }

    /// <summary>
    ///     Step to the previous / next FRET-HAND POSITION — EOF's Alt+PgUp/PgDn
    ///     (EOF_SRC/menu/song.c:93-94). Charts reach ~100 anchors, and hunting with the mouse
    ///     is slow; this is the keyboard route.
    /// </summary>
    public void SeekToAnchor(int direction)
    {
        var beats = Beats;
        var anchors = Anchors;
        if (beats.Count == 0 || anchors.Count == 0) return;

        var now = _projectState.CurrentTimeMs;
        var epsilon = ChartEditorConstants.SeekNextPrevBufferMs;
        var ordered = anchors.Select(a => a.Position.GetPositionMs(beats)).OrderBy(t => t).ToList();
        var target = direction < 0
            ? ordered.Where(t => t < now - epsilon).Cast<double?>().LastOrDefault()
            : ordered.Where(t => t > now + epsilon).Cast<double?>().FirstOrDefault();

        if (target.HasValue) ExecuteSeek(target.Value);
    }

    /// <summary>
    ///     Page the playhead by one SCREEN of chart — EOF's Ctrl+PgUp/PgDn
    ///     (EOF_SRC/menu/song.c:85-86). A screen is the visible time span, which
    ///     is the viewport width divided by the current zoom.
    /// </summary>
    public void SeekByScreen(int direction)
    {
        var pixelsPerMs = _projectState.PixelsPerMs;
        if (pixelsPerMs <= 0) return;

        var screenMs = _projectState.EditorUi.TimelineViewportWidthPx / pixelsPerMs;
        if (screenMs <= 0) return;

        ExecuteSeek(_projectState.CurrentTimeMs + direction * screenMs);
    }

    /// <summary>
    ///     Where the eight seek/select verbs below start looking, in ms.
    /// </summary>
    private double SeekCursorMs(int direction)
        => _projectState.CurrentTimeMs + direction * ChartEditorConstants.SeekNextPrevBufferMs;

    /// <summary>
    ///     First beat strictly after <paramref name="timeMs" />, or null — the
    ///     beat-grid twin of <see cref="TimelineMath.FirstAfter{T}" />, which
    ///     <see cref="BeatMarker" /> cannot use because it carries a raw
    ///     <c>Time</c> rather than a position. Same precondition (ascending by
    ///     <c>Time</c>) as every other search over this grid.
    /// </summary>
    private static BeatMarker? FirstBeatAfter(IList<BeatMarker> beats, double timeMs)
    {
        int lo = 0, hi = beats.Count - 1;
        BeatMarker? found = null;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (beats[mid].Time > timeMs)
            {
                found = beats[mid];
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     Last beat strictly before <paramref name="timeMs" />, or null. The walk
    ///     back over an equal-<c>Time</c> run reproduces the stable
    ///     <c>OrderByDescending</c> this replaced, which yielded the FIRST of such
    ///     a run.
    /// </summary>
    private static BeatMarker? LastBeatBefore(IList<BeatMarker> beats, double timeMs)
    {
        int lo = 0, hi = beats.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (beats[mid].Time < timeMs)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0) return null;
        var tie = beats[found].Time;
        while (found > 0 && beats[found - 1].Time >= tie) found--;
        return beats[found];
    }

    public void SeekToNextElement(bool isShiftPressed)
    {
        if (isShiftPressed)
        {
            var cursor = SeekCursorMs(1);
            SelectSeekTarget(
                TimelineMath.FirstAfter(PlacedNotes, cursor, Beats),
                TimelineMath.FirstAfter(PlacedChords, cursor, Beats),
                true);
        }
        else
        {
            var nextBeat = FirstBeatAfter(Beats, SeekCursorMs(1));
            if (nextBeat != null) ExecuteSeek(nextBeat.Time);
        }
    }

    public void SeekToPrevElement(bool isShiftPressed)
    {
        if (isShiftPressed)
        {
            var cursor = SeekCursorMs(-1);
            SelectSeekTarget(
                TimelineMath.LastBefore(PlacedNotes, cursor, Beats),
                TimelineMath.LastBefore(PlacedChords, cursor, Beats),
                false);
        }
        else
        {
            var prevBeat = LastBeatBefore(Beats, SeekCursorMs(-1));
            if (prevBeat != null) ExecuteSeek(prevBeat.Time);
        }
    }

    private void SelectSeekTarget(GuitarNote? note, Chord? chord, bool preferEarlier)
    {
        if (note == null && chord == null) return;

        var useNote = chord == null || (note != null &&
                                        (preferEarlier
                                            ? note.Position <= chord.Position
                                            : note.Position >= chord.Position));

        if (useNote && note != null)
        {
            ExecuteSeek(note.Position.GetPositionMs(Beats));
            Selection.ClearSelections();
            Selection.SelectedNotes.Add(note);
            return;
        }

        if (chord == null) return;

        ExecuteSeek(chord.Position.GetPositionMs(Beats));
        Selection.ClearSelections();
        Selection.SelectedChords.Add(chord);
    }

    /// <summary>
    /// Selects the note or chord nearest in time to <paramref name="timeMs"/>,
    /// replacing any previous selection. Backs the sheet strip's click-to-select
    /// so the shared selection updates and every view (2D/3D/sheet) re-highlights.
    /// Picks the closer of the nearest note vs. nearest chord; a tie prefers the
    /// note. No-op when the active level has neither notes nor chords.
    /// </summary>
    public void SelectNearestToTime(double timeMs)
    {
        // Binary search per list, not two full passes with a GetPositionMs per
        // element. Same precondition as the arrow-key verbs beside it — these
        // lists are ascending by time, which ChartSpacingEnforcer restores after
        // every committed edit — and the same tie-break: the earlier element
        // wins, and among elements sharing a time the first in LIST order does,
        // which is what the old `d < best` comparison produced.
        var bestNote = TimelineMath.NearestTo(PlacedNotes, timeMs, Beats, out var bestNoteDist);
        var bestChord = TimelineMath.NearestTo(PlacedChords, timeMs, Beats, out var bestChordDist);

        if (bestNote == null && bestChord == null) return;

        Selection.ClearSelections();
        if (bestChord == null || (bestNote != null && bestNoteDist <= bestChordDist))
            Selection.SelectedNotes.Add(bestNote!);
        else
            Selection.SelectedChords.Add(bestChord);
    }

    public void SelectNextNote(bool extendSelection)
    {
        var cursor = SeekCursorMs(1);
        AddOrSelectSeekTarget(
            TimelineMath.FirstAfter(PlacedNotes, cursor, Beats),
            TimelineMath.FirstAfter(PlacedChords, cursor, Beats),
            true, extendSelection);
    }

    public void SelectPrevNote(bool extendSelection)
    {
        var cursor = SeekCursorMs(-1);
        AddOrSelectSeekTarget(
            TimelineMath.LastBefore(PlacedNotes, cursor, Beats),
            TimelineMath.LastBefore(PlacedChords, cursor, Beats),
            false, extendSelection);
    }

    private void AddOrSelectSeekTarget(GuitarNote? note, Chord? chord, bool preferEarlier, bool extend)
    {
        if (note == null && chord == null) return;

        var useNote = chord == null || (note != null &&
                                        (preferEarlier
                                            ? note.Position <= chord.Position
                                            : note.Position >= chord.Position));

        if (useNote && note != null)
        {
            ExecuteSeek(note.Position.GetPositionMs(Beats));
            if (!extend) Selection.ClearSelections();
            if (!Selection.SelectedNotes.Contains(note)) Selection.SelectedNotes.Add(note);
            return;
        }
        if (chord == null) return;
        ExecuteSeek(chord.Position.GetPositionMs(Beats));
        if (!extend) Selection.ClearSelections();
        if (!Selection.SelectedChords.Contains(chord)) Selection.SelectedChords.Add(chord);
    }

    public void SeekToBookmark(int index)
    {
        var project = _projectState.ActiveProject;
        if (project == null) return;
        var bm = project.Bookmarks.FirstOrDefault(b => b.Index == index);
        if (bm == null) return;
        ExecuteSeek(bm.Time);
    }

    public void ToggleBookmarkAtPlayhead(int index)
    {
        var project = _projectState.ActiveProject;
        if (project == null) return;
        var time = _projectState.CurrentTimeMs;
        var existing = project.Bookmarks.FirstOrDefault(b => b.Index == index);
        if (existing == null)
        {
            // No bookmark yet — create one at the playhead.
            project.Bookmarks.Add(new Bookmark { Index = index, Time = time });
        }
        else if (Math.Abs(existing.Time - time) > 0.5)
        {
            // Bookmark exists at a different time — jump to it (matches EOF /
            // Songle Reaper semantics where the same key both sets and seeks).
            ExecuteSeek(existing.Time);
        }
        else
        {
            // Already at the bookmark — remove it.
            project.Bookmarks.Remove(existing);
        }
    }

    public void SetBookmarkAtPlayhead(int index)
    {
        var project = _projectState.ActiveProject;
        if (project == null) return;
        var time = _projectState.CurrentTimeMs;
        var existing = project.Bookmarks.FirstOrDefault(b => b.Index == index);
        if (existing != null) existing.Time = time;
        else project.Bookmarks.Add(new Bookmark { Index = index, Time = time });
    }

    public void ActivateNextArrangement() => ShiftActiveArrangement(1);

    public void ActivatePreviousArrangement() => ShiftActiveArrangement(-1);

    private void ShiftActiveArrangement(int delta)
    {
        var arrangements = Arrangements;
        if (arrangements.Count == 0) return;
        var current = ActiveArrangement;
        var idx = current == null ? 0 : arrangements.IndexOf(current);
        if (idx < 0) idx = 0;
        var next = ((idx + delta) % arrangements.Count + arrangements.Count) % arrangements.Count;
        ActiveArrangement = arrangements[next];
    }
}
