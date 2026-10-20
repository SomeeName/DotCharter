namespace DotCharter.Services.Editing.Tracks;

/// <summary>
/// Maintains one <see cref="TrackEditorContext"/> per known arrangement and
/// tracks which context currently has user focus. The "focused" track is
/// the one whose selection / preview / hover state UI commands and
/// keybindings (Ctrl+C, Ctrl+V, Delete, …) operate on. It is independent
/// from <c>ProjectState.ActiveArrangement</c>, which remains the
/// project-level primary arrangement (drives 3D preview, exports, etc.).
/// </summary>
public interface ITrackContextRegistry
{
    /// <summary>The track currently receiving keyboard / clipboard focus.</summary>
    TrackEditorContext? FocusedTrack { get; }

    /// <summary>
    /// Raised after <see cref="Focus"/> actually moved focus. Subscribe to it
    /// rather than to <c>ProjectState.ActiveArrangement</c>: focus moves WITHOUT
    /// the active arrangement changing (a press in a stacked panel, a
    /// send-selection-here), and anything projecting <see cref="FocusedTrack"/>
    /// is stale until this fires. <c>ChartEditorViewModel</c> is the worked
    /// example — its <c>ActiveRibbon</c> is what View ▸ Timeline Rows writes.
    /// </summary>
    event Action? FocusedTrackChanged;

    // There is deliberately NO TracksChanged event. Such an event would be unusable by construction:
    // it would be raised from
    // GetOrCreate — a lazy cache fill, not a domain change — which every
    // rebuild calls once per arrangement, so a handler that rebuilt on it would
    // re-enter itself N times. The real "the set of tracks changed" signal is
    // ChartProject.Arrangements.CollectionChanged, which
    // MultiTrackOverlayViewModel already uses.

    IReadOnlyList<TrackEditorContext> Tracks { get; }

    TrackEditorContext GetOrCreate(Arrangement arrangement);
    TrackEditorContext? Get(Arrangement arrangement);

    /// <summary>
    /// Sets the focused track to the context for <paramref name="arrangement"/>,
    /// creating it on demand. No-op if it is already focused.
    /// </summary>
    void Focus(Arrangement arrangement);
}
