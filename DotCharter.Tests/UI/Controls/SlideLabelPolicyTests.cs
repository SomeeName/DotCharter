using DotCharter.Rendering.Data.Render;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The next head on each string (<see cref="NextHeadScan" />), and the slide rule built on it: a
///     slide's destination-fret box is left out when the next head on its string starts right at the
///     slide's end on the same fret — the head already says it, and the box covered it (live app,
///     2026-09-23, a run of sliding double-stops where no fret was readable).
/// </summary>
public sealed class SlideLabelPolicyTests
{
    private const double Zoom = 0.2; // px per ms

    private static RenderNote Note(double timeMs, double sustainMs, byte stringIndex, byte fret, byte slideTo = 0) =>
        new() { Time = timeMs, Sustain = sustainMs, StringIndex = stringIndex, Fret = fret, SlideTo = slideTo };

    private static RenderChordNote ChordNote(byte stringIndex, byte fret, double sustainMs, byte slideTo = 0) =>
        new() { StringIndex = stringIndex, Fret = fret, Sustain = sustainMs, SlideTo = slideTo };

    private static (float[] X, byte[] Fret) Scan(RenderNote[] notes)
    {
        var x = new float[notes.Length];
        var f = new byte[notes.Length];
        NextHeadScan.Fill(notes, notes.Length, [], 0, [], 0, Zoom, x, f, [], [], new double[16], new byte[16]);
        return (x, f);
    }

    /// <summary>Whether each single note's slide box is dropped, as NoteLayer + the sustain renderer decide it.</summary>
    private static bool[] Dropped(params RenderNote[] notes)
    {
        var (x, f) = Scan(notes);
        return [.. notes.Select((n, i) => n.SlideTo > 0 &&
            SlideLabelPolicy.NextHeadRepeatsTarget((float)((n.Time + n.Sustain) * Zoom), x[i], n.SlideTo, f[i]))];
    }

    [Fact]
    public void TheScan_FindsTheNextHeadOnTheSameString_AndNothingPastTheLast()
    {
        var (x, f) = Scan([Note(0, 0, 1, 10), Note(50, 0, 2, 7), Note(100, 0, 1, 12)]);

        Assert.Equal(20f, x[0]); // 100 ms at 0.2 px/ms
        Assert.Equal(12, f[0]);
        Assert.True(float.IsPositiveInfinity(x[1]), "the only note on its string has no next head");
        Assert.True(float.IsPositiveInfinity(x[2]));
    }

    [Fact]
    public void ASlideLandingOnItsTargetNote_DropsItsBox() => Assert.Equal([true, false], Dropped(Note(0, 300, 1, 10, slideTo: 11), Note(300, 200, 1, 11)));

    [Fact]
    public void ASlideIntoADifferentFret_KeepsItsBox() => Assert.False(Dropped(Note(0, 300, 1, 10, slideTo: 11), Note(300, 200, 1, 9))[0]);

    [Fact]
    public void ASlideWhoseNextNoteIsFarAway_KeepsItsBox() =>
        // 300 ms of gap at 0.2 px/ms is 60 px: the box sits alone in it.
        Assert.False(Dropped(Note(0, 300, 1, 10, slideTo: 11), Note(600, 200, 1, 11))[0]);

    [Fact]
    public void OnlyTheSameStringCounts() => Assert.False(Dropped(Note(0, 300, 1, 10, slideTo: 11), Note(300, 200, 2, 11))[0]);

    /// <summary>The run from the screenshot WAS double-stops, i.e. chords; the scan walks single
    /// notes and chords together, and chord notes get their own next head.</summary>
    [Fact]
    public void ARunOfSlidingDoubleStops_DropsEveryRepeatedBox()
    {
        RenderChordNote[] chordNotes =
        [
            ChordNote(1, 10, 100, slideTo: 11), ChordNote(0, 8, 100, slideTo: 9),
            ChordNote(1, 11, 60, slideTo: 10), ChordNote(0, 9, 60, slideTo: 8),
            ChordNote(1, 10, 0), ChordNote(0, 8, 0)
        ];
        RenderChord[] chords =
        [
            new() { Time = 0, NoteOffset = 0, NoteCount = 2 },
            new() { Time = 100, NoteOffset = 2, NoteCount = 2 },
            new() { Time = 160, NoteOffset = 4, NoteCount = 2 }
        ];
        var x = new float[chordNotes.Length];
        var f = new byte[chordNotes.Length];
        NextHeadScan.Fill([], 0, chords, chords.Length, chordNotes, 0, Zoom, [], [], x, f, new double[16], new byte[16]);

        var dropped = new bool[chordNotes.Length];
        foreach (var chord in chords)
        {
            for (var k = chord.NoteOffset; k < chord.NoteOffset + chord.NoteCount; k++)
            {
                var cn = chordNotes[k];
                dropped[k] = cn.SlideTo > 0 && SlideLabelPolicy.NextHeadRepeatsTarget(
                    (float)((chord.Time + cn.Sustain) * Zoom), x[k], cn.SlideTo, f[k]);
            }
        }

        Assert.Equal([true, true, true, true, false, false], dropped);
    }

    [Fact]
    public void ASingleNoteBeforeAChord_SeesTheChordNoteAsItsNextHead()
    {
        RenderNote[] notes = [Note(0, 100, 1, 10, slideTo: 11)];
        RenderChord[] chords = [new() { Time = 100, NoteOffset = 0, NoteCount = 2 }];
        RenderChordNote[] chordNotes = [ChordNote(1, 11, 0), ChordNote(0, 9, 0)];
        var x = new float[1];
        var f = new byte[1];
        NextHeadScan.Fill(notes, 1, chords, 1, chordNotes, 0, Zoom, x, f, new float[2], new byte[2], new double[16], new byte[16]);

        Assert.Equal(20f, x[0]);
        Assert.Equal(11, f[0]);
    }

    [Fact]
    public void TheRenderers_TakeTheNextHeadFromTheScan()
    {
        var root = TestPaths.RepoRoot();
        var helpers = UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Controls", "Layers", "Helpers"));
        var sustain = UiSourceTree.ReadAllText(Path.Combine(helpers, "HighwaySustainRenderer.cs"));
        var head = UiSourceTree.ReadAllText(Path.Combine(helpers, "HighwayNoteHeadRenderer.cs"));
        var layer = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Controls", "Layers", "NoteLayer.cs")));

        Assert.Contains("SlideLabelPolicy.NextHeadRepeatsTarget(ex, nextHeadX, data.SlideTo, nextHeadFret)", sustain);
        Assert.Contains("BendLabelPlacement.FitsBeforeNextHead(labelX, boxW, nextHeadX, outerR)", head);
        Assert.Contains("NextHeadScan.Fill(", layer);
        Assert.Contains("_deferredLabels, _noteNextX[i], _noteNextFret[i]", layer);
        Assert.Contains("_chordNoteNextX[chord.NoteOffset + n], _chordNoteNextFret[chord.NoteOffset + n]", layer);
        Assert.Contains("_deferredLabels, _noteNextX[i]);", layer);
        Assert.Contains("_deferredLabels, _chordNoteNextX[chord.NoteOffset + n]);", layer);
    }
}
