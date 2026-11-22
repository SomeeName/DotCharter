using Avalonia;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>Head and tail overlap resolution is shared by normal, compact and inverted timelines.</summary>
public sealed class ChartHitTesterHeadPriorityTests
{
    [Theory]
    [InlineData(false, false, 400)]
    [InlineData(false, true, 400)]
    [InlineData(true, false, 400)]
    [InlineData(true, true, 400)]
    [InlineData(false, false, 148)]
    [InlineData(false, true, 148)]
    [InlineData(true, false, 148)]
    [InlineData(true, true, 148)]
    public void AnyHeadOutranksTheOtherSoundsTail(bool chordHead, bool inverted, double height)
    {
        var beats = Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
        var head = new FractionalPosition(2);
        var tail = new FractionalPosition(1);
        var note = new GuitarNote { Position = chordHead ? tail : head, StringIndex = 2, Fret = 5 };
        if (chordHead) note.Sustain = head - tail;
        var chord = new Chord { Position = chordHead ? head : tail };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7, Sustain = chordHead ? FractionalPosition.Zero : head - tail });
        var pointer = new Point(995, ChartHitTester.GetLaneY(height, 20, 20, 6, 2, inverted));
        var hitNote = ChartHitTester.HitTestNote(pointer, [note], 450, 1, 1600, height, 20, 20, 6,
            beats, inverted, out var noteExact, out var noteHead);
        var hitChord = ChartHitTester.HitTestChord(pointer, [chord], 450, 1, 1600, height, 20, 20, 6,
            beats, inverted, out var chordExact, out var hitChordHead);
        Assert.True(noteExact);
        Assert.True(chordExact);
        Assert.Equal(!chordHead, noteHead);
        Assert.Equal(chordHead, hitChordHead);

        var resolved = ChartHitTester.ResolveSound(hitChord, chordExact, hitNote, noteExact, hitChordHead, noteHead);

        if (chordHead)
        {
            Assert.Same(chord, resolved.Chord);
            Assert.Null(resolved.Note);
        }
        else
        {
            Assert.Same(note, resolved.Note);
            Assert.Null(resolved.Chord);
        }
    }
}
