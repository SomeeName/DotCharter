using Avalonia.Input;

namespace DotCharter.UI.Input;

/// <summary>What a right-press on the note highway does.</summary>
public enum HighwayRightClickVerb
{
    /// <summary>Toggle the clicked string of the chord: remove that string's note, or add one.</summary>
    EditChordString,

    /// <summary>Remove the single note under the pointer.</summary>
    RemoveNote,

    /// <summary>Place a note at the snapped time on the clicked string.</summary>
    PlaceNote,

    /// <summary>Arm the timeline context menu (shown on the release).</summary>
    OpenMenu
}

/// <summary>
///     The highway's right-click, as a pure decision plus the one place that
///     applies it — so the charting gesture is testable without a
///     <c>ToolContext</c> (which needs a whole <c>MainWindowViewModel</c>).
///     <para>
///         A plain right-click is the INSTANT charting verb, as it was until
///         0.59: on a chord it toggles the clicked string (so one note leaves
///         the chord), on a note it removes the note, over an empty lane it
///         places one. Alt+right-click does the same (0.59–0.63 muscle memory).
///         <b>Shift+right-click</b> opens the context menu: Shift is the
///         editor's "more of the same" modifier on the left button and means
/// nothing on the right, so it steals no gesture.
///     </para>
/// </summary>
public static class HighwayRightClick
{
    public static HighwayRightClickVerb Resolve(
        Chord? hitChord, GuitarNote? hitNote, int stringIndex, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift)) return HighwayRightClickVerb.OpenMenu;
        if (hitChord != null) return HighwayRightClickVerb.EditChordString;
        if (hitNote != null && hitNote.StringIndex == stringIndex) return HighwayRightClickVerb.RemoveNote;
        return HighwayRightClickVerb.PlaceNote;
    }

    /// <summary>
    ///     Applies an instant verb through the editing service. Returns
    ///     <c>false</c> for <see cref="HighwayRightClickVerb.OpenMenu" />, which
    ///     the caller arms itself.
    /// </summary>
    public static bool Apply(
        IHighwayEditingService editing, HighwayRightClickVerb verb,
        Chord? hitChord, GuitarNote? hitNote, FractionalPosition snapped, int stringIndex)
    {
        switch (verb)
        {
            case HighwayRightClickVerb.EditChordString when hitChord != null:
                editing.HandleRightClickChord(hitChord, stringIndex);
                return true;
            case HighwayRightClickVerb.RemoveNote when hitNote != null:
                editing.RemoveNote(hitNote);
                return true;
            case HighwayRightClickVerb.PlaceNote:
                editing.ApplyPlacement(snapped, stringIndex);
                return true;
            default:
                return false;
        }
    }
}
