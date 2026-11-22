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

    /// <summary>
    ///     Applies every draft an editor holds but has not committed yet (a text box still being
    ///     typed in). A save, an export, its check and a close or replace call this first, so
    ///     they read what the user sees; each editor registers its own commit with the editor.
    /// </summary>
    void CommitPendingEdits();

    /// <summary>
    ///     An editor holds a draft that only its own Apply writes (the inspector's staged pages), which a save does
    ///     not apply: the close and replace prompts count it as unsaved work, so it is never dropped unasked.
    /// </summary>
    bool HasPendingDrafts { get; }

    /// <summary>
    ///     Writes every draft counted by <see cref="HasPendingDrafts" /> through its own Apply (one undo step each
    ///     editor): the close or replace prompt's Save runs it first, so the saved file holds what the user was asked
    ///     about.
    /// </summary>
    void ApplyPendingDrafts();

    /// <summary>
    ///     Registers an editor's "holds a draft only its own Apply writes" for <see cref="HasPendingDrafts" />, and
    ///     that Apply (one undo step) for <see cref="ApplyPendingDrafts" /> (the Song tab's unapplied song details).
    /// </summary>
    void AddPendingDraft(Func<bool> holdsDraft, Action applyDraft);
}
