using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tools ▸ Showlights: the fog, beams and lasers cue at one point of the
///     song, opened on the playhead snapped to the grid. Saving replaces the
///     known cues at that point as ONE undo step; cues the editor does not
///     know are left alone. <see cref="IDialogScreen{TResult}.Result" /> is
///     true once saved or deleted.
/// </summary>
public interface IShowlightsScreen : IDialogScreen<bool>
{
    /// <summary>Beat and fraction, e.g. "336 3/4" — the reading the charter works in.</summary>
    string TargetPositionText { get; }

    /// <summary>The same point in milliseconds, as a separate cross-check.</summary>
    string TargetMsText { get; }

    /// <summary>Moves the target to the playhead, snapped to the grid.</summary>
    ICommand UsePlayheadCommand { get; }

    /// <summary>"None" first, then every fog cue.</summary>
    IReadOnlyList<string> FogOptions { get; }

    int SelectedFogIndex { get; set; }

    IReadOnlyList<string> BeamsOptions { get; }

    int SelectedBeamsIndex { get; set; }

    IReadOnlyList<string> LasersOptions { get; }

    int SelectedLasersIndex { get; set; }

    bool HasExistingPositions { get; }

    /// <summary>Every point that already has cues, in song order.</summary>
    IReadOnlyList<IShowlightPosition> ExistingPositions { get; }

    /// <summary>Index into <see cref="ExistingPositions" />; choosing one moves the target and seeks there. −1 for none.</summary>
    int SelectedExistingIndex { get; set; }

    bool HasShowlightsAtTarget { get; }

    /// <summary>Removes every cue at the target as one undo step.</summary>
    ICommand DeleteAtPositionCommand { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}

public interface IShowlightPosition
{
    string PositionText { get; }

    /// <summary>"Fog: Green · Beams: Off", or the unknown cues by name.</summary>
    string Summary { get; }
}
