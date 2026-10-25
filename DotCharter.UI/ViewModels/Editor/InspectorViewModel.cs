// InspectorViewModel root: the injected services and every field, the tab strip, the constructor and composed sub-view-models, Dispose, the service event handlers, the 150 ms issue debounce that drives Inspector/InspectorIssuesViewModel, and the two history write funnels every other partial mutates through.

using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel : ViewModelBase, IDisposable
{
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IHighwayEditingService _highwayEditingService;
    private readonly IHistoryService _historyService;
    private readonly ProjectState _projectState;
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _issueRefreshTimer;
    [ObservableProperty] private ChordNote? _bendTargetChordNote;

    [ObservableProperty] private bool? _chordAccent;
    [ObservableProperty] private BendEditorViewModel? _chordBendEditor;

    [ObservableProperty] private ObservableCollection<ChordNote> _chordBendStrings = [];
    [ObservableProperty] private bool? _chordForceNoNotes;
    [ObservableProperty] private bool? _chordIgnore;
    [ObservableProperty] private int _chordNoteHarmonic;
    [ObservableProperty] private int _chordNoteHopo;

    // Per-string technique editors (target = BendTargetChordNote). These
    // mirror the per-note fields so the inspector can edit a specific string
    // inside a chord without splitting the chord.
    [ObservableProperty] private int _chordNoteMute;
    [ObservableProperty] private int _chordNoteBassPicking;
    [ObservableProperty] private bool? _chordNoteIgnore;
    [ObservableProperty] private bool? _chordNoteVibrato;
    [ObservableProperty] private bool? _chordNoteLinkNext;
    [ObservableProperty] private bool? _chordNoteTremolo;
    [ObservableProperty] private bool? _chordNoteUnpitchedSlide;
    [ObservableProperty] private int? _chordNoteSlideTo;
    [ObservableProperty] private bool? _chordPassOtherNotes;
    [ObservableProperty] private bool? _chordSplitIntoNotes;

    private bool _isSyncing;
    [ObservableProperty] private bool? _noteAccent;
    [ObservableProperty] private int _noteBassPicking;
    // Left-hand finger (-1 none, 0 thumb, 1-4), pick direction (PickDirectionType) and the
    // chord's strum direction (PickDirectionType) - shown from the first selected sound.
    [ObservableProperty] private int _noteLeftHandFinger = -1;
    [ObservableProperty] private int _notePickDirection;
    [ObservableProperty] private int _chordStrum;
    [ObservableProperty] private bool? _noteIgnore;

    // Guitar-Pro-style bend editor. One instance per selection target:
    //   * NoteBendEditor edits SelectedNote.BendValues
    //   * ChordBendEditor edits BendTargetChordNote.BendValues (which is
    //     one string inside SelectedChord).
    //
    // When the selection changes we rebuild the editors so their internal
    // projection of slots/steps is fresh. History undo calls
    // ReloadFromDomain on both editors so the graph reflects the
    // re-applied state.
    [ObservableProperty] private BendEditorViewModel? _noteBendEditor;

    // The sounding-pitch readout is a pure function of the two of these, so it
    // re-reads whenever either moves — including from the placement fretboard
    // and the keyboard verbs, which write these properties like everything else.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotePitchName))]
    [NotifyPropertyChangedFor(nameof(HasNotePitch))]
    private int _noteFret;
    [ObservableProperty] private int _noteHarmonic;
    [ObservableProperty] private int _noteHopo;
    [ObservableProperty] private bool? _noteLinkNext;
    [ObservableProperty] private int _noteMute;
    [ObservableProperty] private bool? _notePassOtherNotes;
    [ObservableProperty] private int? _noteSlideTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotePitchName))]
    [NotifyPropertyChangedFor(nameof(HasNotePitch))]
    private int _noteStringIndex;
    [ObservableProperty] private bool? _noteTremolo;
    [ObservableProperty] private bool? _noteUnpitchedSlide;
    [ObservableProperty] private bool? _noteVibrato;

    // Interactive placement (v0.33): a single-note mini-fretboard lets the
    // user click a string/fret to MOVE the note, exactly like the chord tab's
    // string picker. NotePreviewChord is a one-note synthetic Chord the
    // MiniFretboardControl renders (rebuilt as a fresh reference on every sync
    // so the control repaints); NoteStringCount / NoteTuningNames drive its
    // grid. Every click routes through SetNoteTargetCommand → ApplyNoteMutation
    // so it is a single undoable gesture like every other note edit.
    [ObservableProperty] private Chord? _notePreviewChord;
    [ObservableProperty] private int _noteStringCount = 6;
    [ObservableProperty] private IReadOnlyList<string>? _noteTuningNames;

    // Read-only sustain (note length) readout for the placement card — sustain
    // is a grid-relative FractionalPosition authored by dragging on the
    // timeline, so it stays display-only here (see the follow-up note in the
    // inspector guide) but is now visible at a glance instead of hidden.
    [ObservableProperty] private string _noteSustainLabel = string.Empty;

    [ObservableProperty] private Chord? _selectedChord;

    [ObservableProperty] private GuitarNote? _selectedNote;
    [ObservableProperty] private int _selectedTabIndex;

    // Note and Chord are merged into ONE `Sound` tab whose
    // content is a function of the SELECTION, not of which header was clicked
    // (04-inspector-redesign.md §3): they authored the same thing through two
    // headers, two footers and three fretboards.
    public bool IsSoundTabSelected => SelectedTabIndex == 0;
    public bool IsMixerTabSelected => SelectedTabIndex == 1;
    public bool IsSongTabSelected => SelectedTabIndex == 2;
    public bool IsLyricsTabSelected => SelectedTabIndex == 3;
    public bool IsIssuesTabSelected => SelectedTabIndex == 4;
    public bool IsNotesTabSelected => SelectedTabIndex == 5;
    public bool IsPhrasesTabSelected => SelectedTabIndex == 6;

    /// <summary>
    ///     The Riff Library, an inspector TAB rather than a dock
    ///     pane. A dock pane would be a second tab strip under the inspector's own, and
    ///     that strip should not exist — the Stem Mixer
    ///     pane duplicated the Mixer tab outright, and a riff library is the same
    ///     kind of thing as the other tabs: a surface you edit the open chart
    ///     from. One strip.
    /// </summary>
    public bool IsRiffLibraryTabSelected => SelectedTabIndex == RiffLibraryTabIndex;

    public bool IsChordShapesTabSelected => SelectedTabIndex == ChordShapesTabIndex;

    /// <summary>True while the user's explicit tab pick should hold —
    /// selection-driven auto-switching (note ⇒ Note tab, chord ⇒ Chord tab)
    /// then stops stomping their choice. Every write to
    /// <see cref="SelectedTabIndex" /> outside <see cref="AutoSelectTab" />
    /// is by definition a user pick (the tab header click path). The pin is
    /// NOT forever (that would kill auto-switching for the whole
    /// session after one click): a user selection of a DIFFERENT sound kind
    /// (note ⇄ chord) is fresh intent and releases it. Programmatic
    /// selections (<see cref="InspectorIssuesViewModel.JumpToIssue" />) never touch the pin or switch
    /// tabs — the Issues triage loop stays on the Issues tab.</summary>
    private bool _userPinnedTab;

    private bool _autoSwitchingTab;

    /// <summary>Set around selection mutations the INSPECTOR itself makes
    /// (issue jumps): those must not be mistaken for user intent.</summary>
    private bool _programmaticSelection;

    /// <summary>Sound kind of the last selection change (0 none, 1 note,
    /// 2 chord) — the transition detector behind the pin release.</summary>
    private int _lastSelectionKind;

    partial void OnSelectedTabIndexChanged(int value)
    {
        _ = value;
        if (!_autoSwitchingTab) _userPinnedTab = true;
        OnPropertyChanged(nameof(IsSoundTabSelected));
        OnPropertyChanged(nameof(IsMixerTabSelected));
        OnPropertyChanged(nameof(IsSongTabSelected));
        OnPropertyChanged(nameof(IsLyricsTabSelected));
        OnPropertyChanged(nameof(IsIssuesTabSelected));
        OnPropertyChanged(nameof(IsNotesTabSelected));
        OnPropertyChanged(nameof(IsPhrasesTabSelected));
        OnPropertyChanged(nameof(IsRiffLibraryTabSelected));
        OnPropertyChanged(nameof(IsChordShapesTabSelected));
        // The FEHLER groups are rebuilt on activation when an edit left them
        // stale while the tab was hidden (see RebuildIssueGroups).
        if (value == IssuesTabIndex) Issues.EnsureIssueGroupsCurrent();
        // Same rule for the work queue, which only this tab binds.
        if (value == SoundTabIndex) Queue.EnsureCurrent();
        // The phrase browser rebuilds on activation only (never per tick), and only
        // when its phrases moved: an unchanged list keeps its laid-out cards.
        if (value == PhrasesTabIndex) Phrases.EnsureCurrent();
        // Same rule for the riff library, and it is load-bearing: the catalog
        // reads every stored riff off disk, which is why its own constructor
        // defers the first load to a Background post. Loading it when the tab is
        // actually opened keeps that cost off the boot path.
        if (value == RiffLibraryTabIndex) RiffLibrary?.EnsureLoaded();
    }

    /// <summary>Index of the Phrases tab. Named because it is read in two
    /// places and the 8→7 collapse moved it once already.</summary>
    internal const int PhrasesTabIndex = 6;

    /// <summary>Index of the Mixer tab. Named for the same reason as the two
    /// below: the shell selects it when "show the stem mixer" is asked for.</summary>
    internal const int MixerTabIndex = 1;

    /// <summary>Index of the Riff Library tab (0.42.4, was a dock pane).</summary>
    internal const int RiffLibraryTabIndex = 7;

    /// <summary>Index of the Chord shapes tab: every chord template, its uses and duplicates.</summary>
    internal const int ChordShapesTabIndex = 8;

    /// <summary>Index of the merged Sound tab — where a sound selection lands.</summary>
    internal const int SoundTabIndex = 0;

    private void AutoSelectTab(int index)
    {
        if (_userPinnedTab) return;
        _autoSwitchingTab = true;
        SelectedTabIndex = index;
        _autoSwitchingTab = false;
    }

    public InspectorViewModel(
        ChartEditorViewModel chartEditor,
        IHistoryService historyService,
        IHighwayEditingService highwayEditingService,
        ChordTemplateEditorViewModel chordTemplateEditor,
        SongOverviewViewModel songOverview,
        TransportViewModel transport,
        ProjectState projectState,
        LyricsInspectorViewModel lyricsInspector,
        StemMixerViewModel stemMixer,
        AnnotationsInspectorViewModel annotations,
        ExportReadinessViewModel exportReadiness,
        InspectorIssuesViewModel issues,
        HandShapeEditorViewModel handShapeEditor,
        StringMaskViewModel stringMask,
        ILocalizationService localization,
        DotCharter.Services.Editing.Phrases.IPhraseIterationPropagationService? phrasePropagation = null,
        DotCharter.Services.Editing.Linting.IAudioChartLintService? audioLint = null,
        IAudioPlaybackService? audioPlayback = null,
        PhraseCatalogViewModel? phraseCatalog = null,
        IShapePanelScreen? chordShapes = null,
        Linking.LinkedMeasuresViewModel? linkedMeasures = null)
    {
        RiffLibrary = phraseCatalog;
        ChordShapes = chordShapes;
        LinkedMeasures = linkedMeasures;
        _chartEditor = chartEditor;
        _historyService = historyService;
        _highwayEditingService = highwayEditingService;
        _projectState = projectState;
        _localization = localization;
        ChordTemplateEditor = chordTemplateEditor;
        Sections.PropertyChanged += OnSectionsChanged;
        // the keyboard's technique verbs reach what a click here would.
        highwayEditingService.TechniqueReach = new ChordTechniqueKeyboardReach(LadderTargets,
            () => chordTemplateEditor.Preview.IsHostShown, () => _chartEditor.Selection.SelectedChords,
            chords => _projectState.SetMessage(chordTemplateEditor.Ladder.DescribeTechnique(chords)));
        SongOverview = songOverview;
        Transport = transport;
        ProjectState = projectState;
        Lyrics = lyricsInspector;
        StemMixer = stemMixer;
        Annotations = annotations;
        ExportReadiness = exportReadiness;
        // the Issues tab, driven from here (the debounce below, the tab
        // switch) and tied back through IInspectorIssuesHost — never a field.
        Issues = issues;
        Issues.AttachHost(this);
        HandShapeEditor = handShapeEditor;
        StringMask = stringMask;
        // the song's chart-synth playing style, under Mixer ▸ MIDI synth.
        SynthPlayStyle = new ChartSynthPlayStyleViewModel(projectState, historyService, localization);
        // v0.29 phrase browser: STANDALONE sub-VM (inspector ceiling — this
        // class only composes it and forwards tab activation).
        Phrases = new PhrasesInspectorViewModel(
            () => _chartEditor.ActiveArrangement,
            () => _chartEditor.Beats,
            _chartEditor.ExecuteSeek,
            phrasePropagation,
            localization,
            historyService);
        // v0.29 audio-verified lint (explicit-run block on the FEHLER tab):
        // same standalone composition pattern.
        // v0.36 work queue: the same standalone composition pattern — the host
        // only builds it and forwards refreshes.
        // The ACTIVE level, not the arrangement's first: on a multi-level chart
        // level 0 is the thinnest difficulty and is not what the charter is
        // looking at. ProjectState raises ActiveLevel, and the handler above
        // already routes that into ScheduleIssueRefresh -> Queue.Refresh().
        Queue = new SoundQueueViewModel(
            () => _chartEditor.ActiveArrangement,
            () => _projectState.ActiveLevel,
            () => _chartEditor.Beats,
            () => Issues.Items,
            _chartEditor.Selection,
            _chartEditor.ExecuteSeek,
            localization);
        NextTask = new SoundNextTaskViewModel(Queue, localization);
        AudioLint = new AudioLintViewModel(
            () => _chartEditor.ActiveArrangement,
            () => _chartEditor.Beats,
            () => audioPlayback?.CurrentAudioPath,
            _chartEditor.ExecuteSeek,
            audioLint,
            localization);

        _issueRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _issueRefreshTimer.Tick += (_, _) =>
        {
            _issueRefreshTimer.Stop();
            Issues.RecomputeIssues();
        };

        _chartEditor.Selection.PropertyChanged += OnSelectionChanged;
        _chartEditor.Selection.SelectedNotes.CollectionChanged += (_, _) => SyncNoteProperties();
        _chartEditor.Selection.SelectedChords.CollectionChanged += (_, _) => SyncChordProperties();
        // The state matrix reads hand shapes too (states 3 and 6), and nothing
        // else on this VM was watching them.
        _chartEditor.Selection.SelectedHandShapes.CollectionChanged += (_, _) => HandShapeEditor.SyncHandShapeProperties();
        HandShapeEditor.Synced += OnHandShapeEditorSynced;
        _historyService.HistoryChanged += OnHistoryChanged;
        _projectState.PropertyChanged += OnProjectStateChanged;
        Transport.PropertyChanged += OnTransportChanged;

        SelectedNote = _chartEditor.Selection.SelectedNote;
        SelectedChord = _chartEditor.Selection.SelectedChord;
        if (SelectedChord != null) AutoSelectTab(1);
        else if (SelectedNote != null) AutoSelectTab(0);
        SyncNoteProperties();
        SyncChordProperties();
        SyncChordTemplate();
        Issues.RecomputeIssues();
    }

    public TransportViewModel Transport { get; }
    public ChartSynthPlayStyleViewModel SynthPlayStyle { get; }
    public ProjectState ProjectState { get; }

    public ObservableCollection<GuitarNote> SelectedNotes => _chartEditor.Selection.SelectedNotes;
    public ObservableCollection<Chord> SelectedChords => _chartEditor.Selection.SelectedChords;

    public ChordTemplateEditorViewModel ChordTemplateEditor { get; }
    public SongOverviewViewModel SongOverview { get; }

    /// <summary>v0.29 phrase-iteration browser (tab 7) — standalone sub-VM.</summary>
    public PhrasesInspectorViewModel Phrases { get; }

    /// <summary>The Riff Library tab's view model (0.42.4). Optional so the many
    /// tests that construct an inspector without the shell still compile; the tab
    /// header hides itself when it is absent.</summary>
    public PhraseCatalogViewModel? RiffLibrary { get; }

    /// <summary>The Chord shapes tab's screen, a target-architecture contract. Optional for the
    /// same reason as <see cref="RiffLibrary" />; the tab header hides itself when it is absent.</summary>
    public IShapePanelScreen? ChordShapes { get; }

    /// <summary>The Sound tab's "Linked · edits apply to N places" banner. Optional for the
    /// same reason as <see cref="RiffLibrary" />; the banner hides itself when it is absent.</summary>
    public Linking.LinkedMeasuresViewModel? LinkedMeasures { get; }

    /// <summary>v0.29 audio-verified lint (FEHLER-tab block) — standalone sub-VM.</summary>
    public AudioLintViewModel AudioLint { get; }

    /// <summary>v0.36 work queue (the Sound tab's header strip) — standalone sub-VM.</summary>
    public SoundQueueViewModel Queue { get; }

    /// <summary>The Sound tab's Task section with nothing selected: the next thing that needs work.</summary>
    public SoundNextTaskViewModel NextTask { get; }

    /// <summary>The Issues tab: validation, severity groups, jump / auto-fix and the fix menu — standalone sub-VM.</summary>
    public InspectorIssuesViewModel Issues { get; }
    public LyricsInspectorViewModel Lyrics { get; }
    public StemMixerViewModel StemMixer { get; }

    /// <summary>The Notes tab's charting annotations — standalone sub-VM.</summary>
    public AnnotationsInspectorViewModel Annotations { get; }

    /// <summary>The FEHLER tab's export-readiness block — standalone sub-VM.</summary>
    public ExportReadinessViewModel ExportReadiness { get; }

    /// <summary>The Sound tab's Span card and the Shape card's rebind warning — standalone sub-VM.</summary>
    public HandShapeEditorViewModel HandShapeEditor { get; }

    /// <summary>The string-edit mask's verbs and the keyboard readouts — standalone sub-VM.</summary>
    public StringMaskViewModel StringMask { get; }

    /// <summary>Which section of the Sound tab's rail is on screen — standalone sub-VM.</summary>
    public SoundSectionsViewModel Sections { get; } = new();

    public void Dispose()
    {
        _issueRefreshTimer.Stop();
        _chartEditor.Selection.PropertyChanged -= OnSelectionChanged;
        _historyService.HistoryChanged -= OnHistoryChanged;
        _projectState.PropertyChanged -= OnProjectStateChanged;
        Transport.PropertyChanged -= OnTransportChanged;
        HandShapeEditor.Synced -= OnHandShapeEditorSynced;
        Sections.PropertyChanged -= OnSectionsChanged;
        Issues.DetachHost(this);
        SynthPlayStyle.Dispose();
    }

    /// <summary>The hand-shape selection, or the shape itself, moved: re-read what
    /// this view model derives from the selection.</summary>
    private void OnHandShapeEditorSynced()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        // The selection-issue memo reads SelectedHandShapes (an issue can
        // back-reference a hand shape), but it was invalidated from only two
        // places: NotifySelectionIssuesChanged on a fresh issue list, and the
        // ISelectionService.PropertyChanged handler — and that service raises
        // PropertyChanged for the SCALARS SelectedNote/SelectedChord only.
        // Selecting a hand shape is a CollectionChanged, so the memo kept the
        // answer it computed for whatever was selected before: the header badge
        // read 0 over a shape carrying a finding, and stayed 0.
        NotifySelectionIssuesChanged();
        NotifySelectionKindChanged();
    }

    private void OnTransportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (TransportChangeRefreshesPlayheadLabel(e.PropertyName)) OnPropertyChanged(PlayheadLabelChangedArgs);
    }

    private void OnHistoryChanged()
    {
        SyncNoteProperties();
        SyncChordProperties();
        SyncChordTemplate();
        NoteBendEditor?.ReloadFromDomain();
        ChordBendEditor?.ReloadFromDomain();
        ScheduleIssueRefresh();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ProjectStateChangeRefreshesPlayheadLabel(e.PropertyName, Transport.IsPlaying))
            OnPropertyChanged(PlayheadLabelChangedArgs);

        // Export readiness is per-project and only computed on request: a
        // project switch invalidates the old result instead of silently
        // re-checking the new project.
        if (e.PropertyName == nameof(ProjectState.ActiveProject))
        {
            ExportReadiness.Reset();
        }

        // The audio lint is per-ARRANGEMENT as well as per-project, and its
        // findings are times into the song that was analysed — against any other
        // chart they seek the new song to the old one's positions. Same contract
        // as the export findings above: a switch invalidates the old result
        // rather than leaving it standing. Reset also cancels a decode in
        // flight, which is tens of seconds of work for a chart nobody is on.
        // The audio lint is per-ARRANGEMENT as well as per-project, and its
        // findings are times into the song that was analysed — against any other
        // chart they seek the new song to the old one's positions. Same contract
        // as the export findings above: a switch invalidates the old result
        // rather than leaving it standing. Reset also cancels a decode in
        // flight, which is tens of seconds of work for a chart nobody is on.
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement))
        {
            AudioLint.Reset();
            // The mask readout names strings by the arrangement's tuning.
            StringMask.NotifyMaskChanged();
        }

        // The string-edit mask is keyboard-driven (Ctrl+1..9) as well as
        // pointer-driven, so the panel has to follow it: the technique readout
        // reflects the first MASKED string and every scope bar quotes the mask
        // in words. Nothing else would produce a notification.
        if (e.PropertyName == nameof(ProjectState.StringEditMask))
        {
            SyncChordNoteTechniques();
            NotifyMaskScopeChanged();
        }

        if (e.PropertyName == nameof(ProjectState.PreviewNoteFret))
            StringMask.NotifyPenFretChanged();

        if (e.PropertyName is nameof(ProjectState.ActiveArrangement)
            or nameof(ProjectState.ActiveLevel)
            or nameof(ProjectState.Beats)
            or nameof(ProjectState.TotalAudioLengthMs)
            or nameof(ProjectState.ActiveProject))
        {
            ScheduleIssueRefresh();
        }
    }

    private void ScheduleIssueRefresh()
    {
        _issueRefreshTimer.Stop();
        _issueRefreshTimer.Start();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The selection's findings are a function of the selection: forget the
        // materialized list so the next bound read recomputes it.
        _selectionIssues = null;
        if (e.PropertyName == nameof(ISelectionService.SelectedNote))
        {
            SelectedNote = _chartEditor.Selection.SelectedNote;
            if (SelectedNote != null) OnUserOrProgrammaticSelection(kind: 1);
            SyncNoteProperties();
        }
        else if (e.PropertyName == nameof(ISelectionService.SelectedChord))
        {
            SelectedChord = _chartEditor.Selection.SelectedChord;
            if (SelectedChord != null) OnUserOrProgrammaticSelection(kind: 2);
            SyncChordProperties();
            SyncChordTemplate();
        }
    }

    /// <summary>
    ///     The auto-switch, shrunk. With Note and Chord merged into one tab
    ///     there is nothing to auto-switch BETWEEN, so this collapses to "if the
    ///     user is on another tab and picks a sound, go to Sound — unless the
    ///     tab is pinned". The pin survives, and an issue jump still never
    ///     switches tabs.
    /// </summary>
    private void OnUserOrProgrammaticSelection(int kind)
    {
        if (_programmaticSelection)
        {
            // An inspector-driven selection (issue jump) — record the kind so
            // the user's NEXT direct pick is judged against it, but never
            // switch tabs or release the pin.
            _lastSelectionKind = kind;
            return;
        }

        // A different sound kind than last time is fresh user intent — the
        // inspector follows it even if a tab was pinned before.
        if (kind != _lastSelectionKind) _userPinnedTab = false;
        _lastSelectionKind = kind;
        AutoSelectTab(SoundTabIndex);
    }

    private void ApplyNoteMutation(Action<GuitarNote> mutation)
    {
        if (_isSyncing || !SelectedNotes.Any()) return;
        var action = new MutateNotesAction(_chartEditor.PlacedNotes, SelectedNotes, mutation,
            _highwayEditingService.GetLinkNextUpdater());
        _historyService.Do(action);
    }

    private void ApplyChordMutation(Action<Chord> mutation)
    {
        if (_isSyncing || !SelectedChords.Any()) return;
        if (LadderTargets() is { } ladder)
        {
            ApplyChordMutationOnLadder(ladder, mutation);
            return;
        }

        var oldStates = SelectedChords.Select(c => c.Clone()).ToList();
        var newStates = SelectedChords.Select(c =>
        {
            var clone = c.Clone();
            mutation(clone);
            return clone;
        }).ToList();

        var action = new MutateChordsAction(_chartEditor.PlacedChords, SelectedChords, oldStates, newStates,
            _highwayEditingService.GetLinkNextUpdater());
        _historyService.Do(action);
    }

}
