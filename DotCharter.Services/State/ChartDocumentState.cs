using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

public sealed class ChartDocumentState
{
    private readonly ObservableCollection<FretAnchor> _emptyAnchors = [];

    // Per-instance empty fallbacks: returning a shared static collection would allow
    // accidental mutations (e.g. PlacedNotes.Add when no level is active) to leak
    // across ChartDocumentState instances and across test cases.
    private readonly ObservableCollection<BeatMarker> _emptyBeats = [];
    private readonly ObservableCollection<Chord> _emptyChords = [];
    private readonly ObservableCollection<EventPoint> _emptyEventPoints = [];
    private readonly ObservableCollection<HandShape> _emptyHandShapes = [];
    private readonly ObservableCollection<GuitarNote> _emptyNotes = [];

    public ChartProject? ActiveProject { get; set; }
    public Arrangement? ActiveArrangement { get; set; }
    public ChartLevel? ActiveLevel { get; set; }
    public string CurrentProjectPath { get; set; } = "";

    /// <summary>
    ///     Stable score-store identity for songs that have no project path —
    ///     Library loads set "lib:&lt;SongKey&gt;" (<c>LibrarySongIdentity</c>) so bests/grades
    ///     survive across sessions instead of piling up under "untitled".
    ///     Empty for regular project files (their path is the identity).
    /// </summary>
    public string CurrentSongIdentity { get; set; } = "";

    /// <summary>
    ///     The project whose Chart quality report opens with its Editor on Fire round trip expanded:
    /// set by File ▸ Export ▸ Editor on Fire and by expanding the section, cleared by
    ///     collapsing it. Held weakly, so a closed project is not kept alive. The <c>.eof</c> the
    ///     save companion writes does not count: it is on by default, so it says nothing about
    ///     whether this charter uses Editor on Fire.
    /// </summary>
    public ChartProject? EofRoundTripProject
    {
        get => _eofRoundTripProject is not null && _eofRoundTripProject.TryGetTarget(out var project) ? project : null;
        set => _eofRoundTripProject = value is null ? null : new WeakReference<ChartProject>(value);
    }

    private WeakReference<ChartProject>? _eofRoundTripProject;

    public string CurrentAudioFileName { get; set; } = "";
    public double TotalAudioLengthMs { get; set; }
    public WaveformSamples? WaveformData { get; set; }

    public ObservableCollection<BeatMarker> Beats => ActiveProject?.Beats ?? _emptyBeats;
    public ObservableCollection<EventPoint> EventPoints => ActiveArrangement?.EventPoints ?? _emptyEventPoints;
    public ObservableCollection<GuitarNote> PlacedNotes => ActiveLevel?.Notes ?? _emptyNotes;
    public ObservableCollection<Chord> PlacedChords => ActiveLevel?.Chords ?? _emptyChords;
    public ObservableCollection<HandShape> HandShapes => ActiveLevel?.HandShapes ?? _emptyHandShapes;
    public ObservableCollection<FretAnchor> Anchors => ActiveLevel?.Anchors ?? _emptyAnchors;
}
