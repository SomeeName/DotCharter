using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Editing.Tracks;

/// <summary>
/// Editing scope for a single arrangement. Owns the per-arrangement state
/// that would otherwise live on the global <c>ChartEditorViewModel</c> /
/// <see cref="ISelectionService"/> singletons (selection, preview, hover,
/// focus area, current level). Tools, clipboard and history will eventually
/// read/write through a context instead of the global singletons so multiple
/// timelines can each carry their own selection.
///
/// In Phase 1/2 the <see cref="Selection"/> instance for every track is the
/// same global <see cref="ISelectionService"/> the rest of the app already
/// uses; later phases give every track its own selection state. This keeps
/// these phases pure introduction-of-seams refactors with zero behaviour
/// change.
/// </summary>
public partial class TrackEditorContext : ObservableObject
{
    private static readonly ObservableCollection<GuitarNote> EmptyNotes = [];
    private static readonly ObservableCollection<Chord> EmptyChords = [];
    private static readonly ObservableCollection<HandShape> EmptyHandShapes = [];
    private static readonly ObservableCollection<FretAnchor> EmptyAnchors = [];
    private static readonly ObservableCollection<BeatMarker> EmptyBeats = [];

    [ObservableProperty] private EditorFocusArea _focusArea = EditorFocusArea.NoteHighway;
    [ObservableProperty] private ChartLevel? _level;
    [ObservableProperty] private ChartProject? _project;

    public RibbonVisibility Ribbon { get; } = new();

    public TrackEditorContext(Arrangement arrangement, ISelectionService selection)
    {
        Arrangement = arrangement;
        Selection = selection;
        // The hardest level is the whole song (ChartLevels); level 0 is the
        // thinnest reduction of a levelled chart.
        _level = ChartLevels.Hardest(arrangement);
    }

    public Arrangement Arrangement { get; }

    public ISelectionService Selection { get; }

    public ObservableCollection<GuitarNote> Notes => Level?.Notes ?? EmptyNotes;
    public ObservableCollection<Chord> Chords => Level?.Chords ?? EmptyChords;
    public ObservableCollection<HandShape> HandShapes => Level?.HandShapes ?? EmptyHandShapes;
    public ObservableCollection<FretAnchor> Anchors => Level?.Anchors ?? EmptyAnchors;

    public ObservableCollection<EventPoint> EventPoints => Arrangement.EventPoints;
    public ObservableCollection<ToneChange> Tones => Arrangement.ToneChanges;
    public ObservableCollection<ChordTemplate> ChordTemplates => Arrangement.ChordTemplates;
    public string[] Tuning => Arrangement.Tuning;
    public int StringCount => Arrangement.Tuning.Length;

    public ObservableCollection<BeatMarker> Beats => Project?.Beats ?? EmptyBeats;

    partial void OnLevelChanged(ChartLevel? value)
    {
        _ = value;
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Chords));
        OnPropertyChanged(nameof(HandShapes));
        OnPropertyChanged(nameof(Anchors));
    }

    partial void OnProjectChanged(ChartProject? value)
    {
        _ = value;
        OnPropertyChanged(nameof(Beats));
    }
}

