using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using LyricEvent = DotCharter.Core.Domain.Vocals.LyricEvent;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Services.Editing.Selection;

public partial class SelectionService : ObservableObject, ISelectionService
{
    [ObservableProperty] private FretAnchor? _hoveredAnchor;
    [ObservableProperty] private Chord? _hoveredChord;
    [ObservableProperty] private EventPoint? _hoveredEventPoint;
    [ObservableProperty] private HandShape? _hoveredHandShape;
    [ObservableProperty] private GuitarNote? _hoveredNote;

    [ObservableProperty] private int _hoveredStringIndex = -1;
    [ObservableProperty] private double _hoveredTimeMs = -1.0;
    [ObservableProperty] private ToneChange? _hoveredTone;
    [ObservableProperty] private LyricEvent? _hoveredLyric;
    [ObservableProperty] private LyricEvent? _selectedLyric;

    [ObservableProperty] private Chord? _selectedChord;
    [ObservableProperty] private GuitarNote? _selectedNote;
    [ObservableProperty] private Arrangement? _previewArrangement;

    /// <inheritdoc />
    public event EventHandler? SelectionChanged;

    public SelectionService()
    {
        // Keep the scalar SelectedNote/SelectedChord in step with the first
        // item of their collection, then fan every content-selection edit into
        // the single coarse SelectionChanged signal so views (2D/3D/sheet) can
        // subscribe once. Preview/hover collections are intentionally NOT wired
        // here — they update at pointer rates and the renderers poll them.
        SelectedNotes.CollectionChanged += (_, e) => { SelectedNote = SelectedNotes.FirstOrDefault(); DropLyricIfNoteLaneGained(SelectedNotes.Count, e); RaiseSelectionChanged(); };
        SelectedChords.CollectionChanged += (_, e) => { SelectedChord = SelectedChords.FirstOrDefault(); DropLyricIfNoteLaneGained(SelectedChords.Count, e); RaiseSelectionChanged(); };
        SelectedEventPoints.CollectionChanged += (_, _) => RaiseSelectionChanged();
        SelectedTones.CollectionChanged += (_, _) => RaiseSelectionChanged();
        SelectedHandShapes.CollectionChanged += (_, e) => { DropLyricIfNoteLaneGained(SelectedHandShapes.Count, e); RaiseSelectionChanged(); };
        SelectedAnchors.CollectionChanged += (_, e) => { DropLyricIfNoteLaneGained(SelectedAnchors.Count, e); RaiseSelectionChanged(); };
    }

    /// <summary>
    ///     A lyric and note-lane content are never selected together: selecting a note, chord,
    ///     hand shape or anchor — by click, marquee, select-all or the context menu's target —
    ///     drops the lyric, just as a lyric click clears the note lane
    ///     (<c>EventInteractionTool.SelectLyricAlone</c>). A note click must not leave the lyric
    ///     selected and highlighted, and since Delete removes a lone selected lyric, the second
    ///     Delete after deleting that note would remove the lyric too. Removing items or
    ///     clearing is not selecting something else, so it keeps the lyric.
    /// </summary>
    private void DropLyricIfNoteLaneGained(int count, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (SelectedLyric == null || count == 0 ||
            e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
        {
            return;
        }

        SelectedLyric = null;
    }

    // SelectedLyric is set directly (not via a collection), so fold its changes
    // into the same signal.
    partial void OnSelectedLyricChanged(LyricEvent? value) => RaiseSelectionChanged();

    public long SelectionVersion { get; private set; }

    private void RaiseSelectionChanged()
    {
        // Bump BEFORE raising: a handler that reads the version during the
        // event already sees the new value, so no consumer can cache stale.
        SelectionVersion++;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public ObservableCollection<GuitarNote> SelectedNotes { get; } = new BulkObservableCollection<GuitarNote>();
    public ObservableCollection<Chord> SelectedChords { get; } = new BulkObservableCollection<Chord>();
    public ObservableCollection<EventPoint> SelectedEventPoints { get; } = new BulkObservableCollection<EventPoint>();
    public ObservableCollection<ToneChange> SelectedTones { get; } = new BulkObservableCollection<ToneChange>();
    public ObservableCollection<HandShape> SelectedHandShapes { get; } = new BulkObservableCollection<HandShape>();
    public ObservableCollection<FretAnchor> SelectedAnchors { get; } = new BulkObservableCollection<FretAnchor>();

    public ObservableCollection<GuitarNote> PreviewNotes { get; } = new BulkObservableCollection<GuitarNote>();
    public ObservableCollection<Chord> PreviewChords { get; } = new BulkObservableCollection<Chord>();
    public ObservableCollection<HandShape> PreviewHandShapes { get; } = new BulkObservableCollection<HandShape>();
    public ObservableCollection<EventPoint> PreviewEventPoints { get; } = new BulkObservableCollection<EventPoint>();
    public ObservableCollection<FretAnchor> PreviewAnchors { get; } = new BulkObservableCollection<FretAnchor>();

    public void ClearSelections()
    {
        SelectedNotes.Clear();
        SelectedChords.Clear();
        SelectedEventPoints.Clear();
        SelectedTones.Clear();
        SelectedHandShapes.Clear();
        SelectedAnchors.Clear();
        // SelectedLyric is a CONTENT selection like the six collections, it is
        // just scalar. It must be dropped by every clear: after a project swap
        // (ChartEditorViewModel.ApplyProject) or a whole-project tool's snapshot
        // restore — both of which call ClearAll() precisely to drop stale
        // references — it would still point into the DETACHED vocal track, and it is
        // not inert: ChartRenderer reads it into the frame snapshot and
        // TimelineWheelAdjuster MUTATES it, so one wheel gesture would edit a lyric
        // in a project that was closed.
        SelectedLyric = null;
    }

    public void ClearPreviews()
    {
        PreviewNotes.Clear();
        PreviewChords.Clear();
        PreviewHandShapes.Clear();
        PreviewEventPoints.Clear();
        PreviewAnchors.Clear();
        // PreviewArrangement intentionally NOT reset here — the
        // ToolCoordinator owns that tag and sets it on every pointer event.
        // Tools call ClearPreviews() at the start of OnPointerMoved before
        // re-adding the new preview, so resetting the tag would cause the
        // next render to route the just-added preview to the wrong panel.
    }

    public void ClearHovers()
    {
        HoveredNote = null;
        HoveredChord = null;
        HoveredHandShape = null;
        HoveredEventPoint = null;
        HoveredTone = null;
        HoveredAnchor = null;
        HoveredLyric = null;
        HoveredTimeMs = -1.0;
        HoveredStringIndex = -1;
    }

    public void ClearAll()
    {
        ClearSelections();
        ClearPreviews();
        ClearHovers();
        // ClearPreviews() deliberately keeps PreviewArrangement (the tool
        // coordinator re-sets that tag on every pointer event, and resetting it
        // mid-gesture routes the just-added preview to the wrong panel). ClearAll
        // is the PROJECT-SWAP hook, not a per-gesture one, so it must not inherit
        // that exemption: it left a strong reference to a detached Arrangement.
        PreviewArrangement = null;
    }
}
