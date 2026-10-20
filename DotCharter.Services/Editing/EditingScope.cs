using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing;

public sealed class EditingScope : IEditingScope
{
    // Empty fallbacks mirror ChartDocumentState's pattern so accidental writes
    // never leak into the wrong arrangement when nothing is targeted yet.
    // PER-INSTANCE, exactly like ChartDocumentState's — they must not be
    // `static readonly`: an
    // AddNotesAction executed with no active level would add to a PROCESS-WIDE
    // collection that every other scope would then see, and that would survive across test
    // cases.
    private readonly ObservableCollection<GuitarNote> _emptyNotes = [];
    private readonly ObservableCollection<Chord> _emptyChords = [];
    private readonly ObservableCollection<HandShape> _emptyHandShapes = [];
    private readonly ObservableCollection<FretAnchor> _emptyAnchors = [];
    private readonly ObservableCollection<EventPoint> _emptyEventPoints = [];

    private readonly ProjectState _projectState;
    private Func<EditingTarget?>? _resolver;

    public EditingScope(ProjectState projectState) => _projectState = projectState;

    public void SetResolver(Func<EditingTarget?> resolver) => _resolver = resolver;

    public ChartProject? Project => _projectState.ActiveProject;
    public Arrangement? Arrangement => Resolve()?.Arrangement ?? _projectState.ActiveArrangement;
    public ChartLevel? Level => Resolve()?.Level ?? _projectState.ActiveLevel;

    public ObservableCollection<GuitarNote> Notes => Level?.Notes ?? _emptyNotes;
    public ObservableCollection<Chord> Chords => Level?.Chords ?? _emptyChords;
    public ObservableCollection<HandShape> HandShapes => Level?.HandShapes ?? _emptyHandShapes;
    public ObservableCollection<FretAnchor> Anchors => Level?.Anchors ?? _emptyAnchors;
    public ObservableCollection<EventPoint> EventPoints => Arrangement?.EventPoints ?? _emptyEventPoints;
    public ObservableCollection<BeatMarker> Beats => _projectState.Beats;

    private EditingTarget? Resolve() => _resolver?.Invoke();
}
