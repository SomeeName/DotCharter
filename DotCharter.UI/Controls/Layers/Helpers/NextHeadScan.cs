using DotCharter.Rendering.Data.Render;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     For every note and chord note in a frame's snapshot: where the NEXT head on the same string
///     starts on screen, and its fret. The 2D timeline's labels that hang to the right of a note — a
///     slide's destination box, a bend's step label — are drawn after the heads, so without this they
///     would land on the next head and hide its fret number. Consumers:
///     <see cref="SlideLabelPolicy" /> and <see cref="BendLabelPlacement" />.
///     <para>
///         One backward pass over the single notes and the chords merged in time order (a double-stop
///         usually slides or bends into the next double-stop, so the two lists have to be walked
///         together), remembering the next head per string. Allocation-free: the caller owns every
///         array; the two per-string arrays are scratch.
///     </para>
/// </summary>
public static class NextHeadScan
{
    public static void Fill(RenderNote[] notes, int noteCount, RenderChord[] chords, int chordCount,
        RenderChordNote[] chordNotes, double viewStartMs, double zoom,
        float[] noteNextX, byte[] noteNextFret, float[] chordNoteNextX, byte[] chordNoteNextFret,
        double[] scratchTime, byte[] scratchFret)
    {
        Array.Fill(scratchTime, double.NaN);
        var i = noteCount - 1;
        var j = chordCount - 1;
        while (i >= 0 || j >= 0)
        {
            if (j >= 0 && (i < 0 || chords[j].Time >= notes[i].Time))
            {
                var chord = chords[j--];
                for (var k = chord.NoteOffset; k < chord.NoteOffset + chord.NoteCount; k++)
                {
                    var cn = chordNotes[k];
                    Visit(cn.StringIndex, cn.Fret, chord.Time, viewStartMs, zoom, scratchTime, scratchFret,
                        out chordNoteNextX[k], out chordNoteNextFret[k]);
                }
            }
            else
            {
                var n = notes[i];
                Visit(n.StringIndex, n.Fret, n.Time, viewStartMs, zoom, scratchTime, scratchFret,
                    out noteNextX[i], out noteNextFret[i]);
                i--;
            }
        }
    }

    /// <summary>Reads the string's next head for this one, then becomes that string's next head for
    /// everything earlier.</summary>
    private static void Visit(byte stringIndex, byte fret, double time, double viewStartMs, double zoom,
        double[] scratchTime, byte[] scratchFret, out float nextX, out byte nextFret)
    {
        nextX = float.PositiveInfinity;
        nextFret = 0;
        if (stringIndex >= scratchTime.Length) return;

        if (!double.IsNaN(scratchTime[stringIndex]))
        {
            nextX = (float)((scratchTime[stringIndex] - viewStartMs) * zoom);
            nextFret = scratchFret[stringIndex];
        }

        scratchTime[stringIndex] = time;
        scratchFret[stringIndex] = fret;
    }
}
