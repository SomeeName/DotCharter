using System.Collections.ObjectModel;

namespace DotCharter.Services.Editing;

/// <summary>
///     What the Services-side coordinators (audio playback, chart maintenance,
///     crash recovery, the project session) read and drive on the chart editor.
///     Implemented by the UI's <c>ChartEditorViewModel</c>; an interface so those
///     coordinators live in Services without naming a view model.
/// </summary>
public interface IChartEditorSurface
{
    Arrangement? ActiveArrangement { get; }
    ObservableCollection<GuitarNote> PlacedNotes { get; }
    ObservableCollection<Chord> PlacedChords { get; }
    ObservableCollection<BeatMarker> Beats { get; }

    /// <summary>The active level's fret-hand positions.</summary>
    ObservableCollection<FretAnchor> Anchors { get; }
    ObservableCollection<EventPoint> EventPoints { get; }
    ObservableCollection<HandShape> HandShapes { get; }
    ObservableCollection<LyricPhrase> LyricPhrases { get; }
    VocalTrack? ActiveVocalTrack { get; set; }
    ISelectionService Selection { get; }
    ObservableCollection<GuitarNote> SelectedNotes { get; }
    ObservableCollection<Chord> SelectedChords { get; }

    /// <summary>Replaces the editor's project (and optionally its undo history).</summary>
    void ApplyProject(ChartProject project, bool resetHistory = false);

    /// <summary>Asks every chart surface to redraw on its next frame.</summary>
    void RequestRedraw();

    /// <summary>Moves the playhead there (clamped) and re-locks the viewport onto it — a deliberate seek.</summary>
    void ExecuteSeek(double timeMs);

    /// <summary><paramref name="timeMs" /> snapped to the editor's current grid division.</summary>
    FractionalPosition SnapToGrid(double timeMs);

    /// <summary>Makes <paramref name="arrangement" /> the active one, as the arrangement picker does.</summary>
    void SetActiveArrangement(Arrangement? arrangement);

    /// <summary>
    ///     The active arrangement's name or tuning changed in place: re-publish the chart and
    ///     re-raise the arrangement, since <c>Arrangement</c> itself raises nothing.
    /// </summary>
    void RepublishActiveArrangement();
}
