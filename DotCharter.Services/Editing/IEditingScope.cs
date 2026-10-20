using System.Collections.ObjectModel;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing;

/// <summary>
/// Resolves the arrangement / level that editing services should currently
/// target. By default this follows <see cref="ProjectState.ActiveArrangement"/>,
/// but the UI layer can install a resolver so a click in a stacked timeline
/// retargets edits at THAT arrangement without changing the global active.
/// Editing services consume the collection accessors so they don't need to know
/// which arrangement is "live" — they just write through the scope.
/// </summary>
public interface IEditingScope
{
    /// <summary>The project the arrangement belongs to: what an edit the history cannot place marks as edited.</summary>
    ChartProject? Project { get; }

    Arrangement? Arrangement { get; }
    ChartLevel? Level { get; }

    ObservableCollection<GuitarNote> Notes { get; }
    ObservableCollection<Chord> Chords { get; }
    ObservableCollection<HandShape> HandShapes { get; }
    ObservableCollection<FretAnchor> Anchors { get; }
    ObservableCollection<EventPoint> EventPoints { get; }
    ObservableCollection<BeatMarker> Beats { get; }

    /// <summary>
    /// Install a resolver that returns the (arrangement, level) pair edits
    /// should currently target. Returning <c>null</c> from the resolver falls
    /// back to <see cref="ProjectState.ActiveArrangement"/> /
    /// <see cref="ProjectState.ActiveLevel"/>.
    /// </summary>
    void SetResolver(Func<EditingTarget?> resolver);
}

public readonly record struct EditingTarget(Arrangement Arrangement, ChartLevel Level);
