using DotCharter.Services.Parsing.Midi;
using static DotCharter.Tests.Services.Parsing.Midi.MidiTestBytes;

namespace DotCharter.Tests.Services.Parsing.Midi;

/// <summary>
///     #333: a MIDI in an odd eighth-note meter got one beat marker per QUARTER
///     and a bar rounded to whole quarters — 7/8 a bar line every 4 quarters
///     instead of 3.5, 5/8 every 2 instead of 2.5 — so the bar lines drifted half
///     a beat per bar, and every beat claimed /4. The grid is now laid in the
///     signature's own beat unit (as the Guitar Pro import lays it), and the
///     notes are placed on THAT grid.
/// </summary>
public sealed class MidiOddMeterGridTests
{
    private const int Ppq = 480;
    private const int Eighth = Ppq / 2;

    [Theory]
    [InlineData(7)]
    [InlineData(5)]
    [InlineData(11)]
    public void AnOddEighthMeter_PutsABarLineEveryNumeratorEighths(int numerator)
    {
        var map = new MidiTempoMap(Ppq, []); // 120 BPM: an eighth is 250 ms
        var beats = map.SuggestBeatGrid(numerator * Eighth * 8L, [new MidiTimeSignatureEvent(0, numerator, 8)]);

        var barLines = beats.Select((b, i) => (b, i)).Where(x => x.b.IsMeasure).Select(x => x.i).ToList();
        Assert.True(barLines.Count >= 9, $"only {barLines.Count} bar lines");
        for (var bar = 0; bar < 9; bar++)
        {
            Assert.Equal(bar * numerator, barLines[bar]);
            Assert.Equal(bar * numerator * 250.0, beats[barLines[bar]].Time, 6);
        }

        Assert.All(beats, b => Assert.Equal(numerator, b.BeatsInMeasure));
        Assert.All(beats, b => Assert.Equal(8, b.NoteDenominator));
        Assert.All(beats, b => Assert.Equal(120.0, b.Bpm, 6));
    }

    /// <summary>
    ///     4/4 → one bar of 7/8 → 4/4. The second 4/4 starts half a quarter off the
    ///     quarter grid (tick 3,600 = 7.5 quarters), which a quarter grid has no
    ///     marker for.
    /// </summary>
    [Fact]
    public void AMeterChange_KeepsEveryBarLineOnItsSignatureChange()
    {
        const int sevenEightStart = 4 * Ppq;
        const int backToFourFour = sevenEightStart + 7 * Eighth;
        var map = new MidiTempoMap(Ppq, []);
        var beats = map.SuggestBeatGrid(backToFourFour + 4 * Ppq,
        [
            new MidiTimeSignatureEvent(0, 4, 4),
            new MidiTimeSignatureEvent(sevenEightStart, 7, 8),
            new MidiTimeSignatureEvent(backToFourFour, 4, 4)
        ]);

        var barLineMs = beats.Where(b => b.IsMeasure).Select(b => b.Time).ToList();
        Assert.Equal(0.0, barLineMs[0], 6);
        Assert.Equal(map.TicksToMs(sevenEightStart), barLineMs[1], 6);
        Assert.Equal(map.TicksToMs(backToFourFour), barLineMs[2], 6);
        Assert.Equal(map.TicksToMs(backToFourFour + 4 * Ppq), barLineMs[3], 6);

        // 4 quarters, 7 eighths, then quarters again.
        Assert.Equal([4, 4, 4, 4, 7, 7, 7, 7, 7, 7, 7, 4, 4, 4, 4], beats.Take(15).Select(b => b.BeatsInMeasure));
        Assert.Equal([4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8, 4, 4, 4, 4], beats.Take(15).Select(b => b.NoteDenominator));
    }

    /// <summary>
    ///     End to end from SMF bytes: a riff in 7/8 with a note on every downbeat
    ///     and one a sixteenth after the second. Every downbeat note must sit ON a
    ///     bar line of the suggested grid, at the time the MIDI says it sounds.
    /// </summary>
    [Fact]
    public void ASevenEightMidi_PutsEveryDownbeatNoteOnABarLine()
    {
        const int bars = 8;
        const int barTicks = 7 * Eighth;
        var events = new List<byte[]> { SetTempo(0, 500_000), TimeSignature(0, 7, 3) };
        long rest = 0; // ticks since the last event, carried into the next delta
        for (var bar = 0; bar < bars; bar++)
        {
            events.Add(NoteOn(rest, 0, 40, 100));
            if (bar == 1)
            {
                // A sixteenth after the downbeat, an eighth long.
                events.Add(NoteOn(Eighth / 2, 0, 45, 100));
                events.Add(NoteOff(Eighth / 2, 0, 40));
                events.Add(NoteOff(Eighth / 2, 0, 45));
                rest = barTicks - 3 * Eighth / 2;
            }
            else
            {
                events.Add(NoteOff(Eighth, 0, 40));
                rest = barTicks - Eighth;
            }
        }

        events.Add(EndOfTrack());
        var midi = MidiFileParser.Parse(SmfFile(0, Ppq, Track([.. events])));
        var map = new MidiTempoMap(midi.TicksPerQuarterNote, midi.TempoEvents);

        var draft = MidiToDraftConverter.Convert(midi);
        var beats = draft.SuggestedBeats.ToList();

        var downbeats = draft.Notes.Where(n => n.Midi == 40).ToList();
        Assert.Equal(bars, downbeats.Count);
        for (var bar = 0; bar < bars; bar++)
        {
            var note = downbeats[bar].Note;
            Assert.Equal(Fraction.Zero, note.Position.Fraction);
            Assert.True(beats[note.Position.BeatId].IsMeasure, $"bar {bar + 1}'s downbeat is not on a bar line");
            Assert.Equal(map.TicksToMs((long)bar * barTicks), note.Position.GetPositionMs(beats), 6);
            // One eighth long = one beat of this grid.
            Assert.Equal(new FractionalPosition(1), note.Sustain);
        }

        var offbeat = Assert.Single(draft.Notes, n => n.Midi == 45).Note;
        // Bar 2 starts at beat 7; a sixteenth is half an eighth beat.
        Assert.Equal(new FractionalPosition(7, new Fraction(1, 2)), offbeat.Position);
        Assert.Equal(map.TicksToMs(barTicks + Eighth / 2), offbeat.Position.GetPositionMs(beats), 6);
    }
}
