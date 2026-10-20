using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Snapping;

/// <summary>
///     Note magnetism over a grid snap: an existing sound closer to the raw time
///     than the grid line is wins, so a tuplet or an off-grid import stays
///     reachable without changing the divisor.
///     <para>
///         Lifted out of <c>ChartEditorViewModel.SnapToGrid</c> so the keyboard
///         pen lands on exactly the same position the pointer would. Without it
///         the two disagree in precisely the case that matters: after a
///         seek-to-next-sound the playhead sits ON an off-grid sound, and a plain
///         grid snap would place the pen's note at the nearest grid LINE instead
///         — a second sound a few ms away rather than a string added to the one
///         that is there. EOF tightens its hover tolerance in the keyboard-driven
///         input mode for the same reason (<c>editor.c:4565-4569</c>).
///     </para>
/// </summary>
public static class SoundMagnetSnap
{
    /// <summary>
    ///     <paramref name="gridSnap" /> is the result of
    ///     <c>ITempoService.SnapToGrid</c>. Returns it unchanged when no sound is
    ///     nearer to <paramref name="timeMs" />, and when there are no beats
    ///     (nothing can be measured).
    /// </summary>
    public static FractionalPosition Apply(
        double timeMs,
        FractionalPosition gridSnap,
        IList<BeatMarker> beats,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords)
    {
        if (beats.Count == 0) return gridSnap;

        var bestDelta = Math.Abs(timeMs - gridSnap.GetPositionMs(beats));
        FractionalPosition? bestPos = null;

        foreach (var note in notes)
        {
            var d = Math.Abs(note.Position.GetPositionMs(beats) - timeMs);
            if (d < bestDelta)
            {
                bestDelta = d;
                bestPos = note.Position;
            }
        }

        foreach (var chord in chords)
        {
            var d = Math.Abs(chord.Position.GetPositionMs(beats) - timeMs);
            if (d < bestDelta)
            {
                bestDelta = d;
                bestPos = chord.Position;
            }
        }

        return bestPos ?? gridSnap;
    }
}
