using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Midi;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     The two import lookups that scanned a whole list per item. Both were
///     turned into binary searches over lists that were ALREADY sorted (the
///     linear versions' own early-<c>break</c>s depended on that order), so each
///     test comes in a pair: an equivalence check against a brute-force
///     reference, and a budget the quadratic version cannot meet.
/// </summary>
public sealed class ImportHotPathTests
{
    // ---- MidiTempoMap.SegmentAt ---------------------------------------------

    /// <summary>The binary search must land on exactly the segment the backward
    /// linear scan did — including at the boundaries and before the first
    /// event, where the SMF 120 BPM default applies.</summary>
    [Fact]
    public void MidiTempoMap_AgreesWithABruteForceReferenceAtEveryTick()
    {
        const int ticksPerQuarter = 480;
        var events = new List<MidiTempoEvent>();
        for (var i = 1; i <= 40; i++)
            events.Add(new MidiTempoEvent(i * 1000L, 400_000 + i * 137));

        var map = new MidiTempoMap(ticksPerQuarter, events);

        for (long tick = 0; tick <= 45_000; tick += 37)
        {
            var expected = ReferenceMicrosecondsPerQuarterNote(events, tick);
            Assert.Equal(expected, map.MicrosecondsPerQuarterNoteAt(tick));
        }

        // Exact boundaries: at a tempo event's own tick the NEW tempo applies.
        foreach (var ev in events)
        {
            Assert.Equal(ev.MicrosecondsPerQuarterNote, map.MicrosecondsPerQuarterNoteAt(ev.Ticks));
            Assert.Equal(ReferenceMicrosecondsPerQuarterNote(events, ev.Ticks - 1),
                map.MicrosecondsPerQuarterNoteAt(ev.Ticks - 1));
        }

        // Times must stay strictly increasing across the whole map.
        var previous = -1.0;
        for (long tick = 0; tick <= 45_000; tick += 211)
        {
            var ms = map.TicksToMs(tick);
            Assert.True(ms > previous, $"tick {tick} produced {ms}, not past {previous}");
            previous = ms;
        }
    }

    /// <summary>Two tempo events on the SAME tick: the last one wins, and the
    /// search must still find it.</summary>
    [Fact]
    public void MidiTempoMap_ARepeatedTickKeepsTheLastTempo()
    {
        var map = new MidiTempoMap(480, [
            new MidiTempoEvent(0, 500_000),
            new MidiTempoEvent(960, 400_000),
            new MidiTempoEvent(960, 300_000)
        ]);

        Assert.Equal(300_000, map.MicrosecondsPerQuarterNoteAt(960));
        Assert.Equal(300_000, map.MicrosecondsPerQuarterNoteAt(100_000));
        Assert.Equal(500_000, map.MicrosecondsPerQuarterNoteAt(959));
    }

    /// <summary>
    ///     <c>SuggestBeatGrid</c> calls the lookup twice per beat for up to
    ///     100 000 beats, and the segment list grows one entry per tempo meta
    ///     event — bounded only by file size, roughly one per ten bytes. The
    ///     backward linear scan made a tempo-event-heavy <c>.mid</c> hang the
    ///     import on a file that PARSES in under a second.
    /// </summary>
    [Fact]
    public void MidiTempoMap_ADenseTempoMapBuildsItsGridInBoundedTime()
    {
        const int tempoEvents = 200_000;
        var events = new List<MidiTempoEvent>(tempoEvents);
        for (var i = 1; i <= tempoEvents; i++)
            events.Add(new MidiTempoEvent(i * 480L, 400_000 + i % 200_000));

        var map = new MidiTempoMap(480, events);

        var beatCount = 0;
        var bound = TimeSpan.FromSeconds(15);
        var fastest = BestOfTiming.Fastest(() => beatCount = map.SuggestBeatGrid(100_000L * 480).Count, bound);

        Assert.NotEqual(0, beatCount);
        // Green runs in about a second; the linear scan is ~2e10 comparisons.
        Assert.True(fastest < bound,
            $"the fastest of {BestOfTiming.Attempts} grid builds took {fastest.TotalSeconds:0.0}s — the tempo lookup is scanning again");
    }

    private static int ReferenceMicrosecondsPerQuarterNote(List<MidiTempoEvent> events, long ticks)
    {
        var result = 500_000; // SMF default before the first event
        foreach (var ev in events.OrderBy(e => e.Ticks))
        {
            if (ev.Ticks <= ticks)
                result = ev.MicrosecondsPerQuarterNote;
        }

        return ticks <= 0 ? Math.Min(result, ReferenceAtZero(events)) : result;
    }

    private static int ReferenceAtZero(List<MidiTempoEvent> events)
    {
        var atZero = events.Where(e => e.Ticks == 0).ToList();
        return atZero.Count > 0 ? atZero[^1].MicrosecondsPerQuarterNote : 500_000;
    }

    // ---- EofProGuitarTrackParser.FindApplicableTechNote ----------------------

    /// <summary>The binary skip must return exactly what the from-index-0 scan
    /// returned: the LAST tech note inside the note's window on that string, at
    /// the note's own difficulty.</summary>
    [Fact]
    public void EofTechNoteLookup_AgreesWithABruteForceReference()
    {
        var techNotes = new List<EofRawProGuitarNote>();
        var random = new Random(20260725);
        for (var i = 0; i < 400; i++)
        {
            techNotes.Add(new EofRawProGuitarNote
            {
                PositionMs = (uint)(i * 25),
                LengthMs = 10,
                Difficulty = (byte)(i % 3),
                NoteBitmask = (byte)(1 + random.Next(0, 63))
            });
        }

        foreach (var start in new uint[] { 0, 25, 60, 500, 4_000, 9_975, 20_000 })
        {
            foreach (var length in new uint[] { 0, 10, 100, 1_000 })
            {
                foreach (var difficulty in new byte[] { 0, 1, 2 })
                {
                    foreach (var stringIndex in new[] { 0, 3, 5 })
                    {
                        var raw = new EofRawProGuitarNote
                        { PositionMs = start, LengthMs = length, Difficulty = difficulty };

                        var expected = ReferenceTechNote(techNotes, raw, stringIndex);
                        var actual = EofProGuitarTrackParser.FindApplicableTechNote(techNotes, raw, stringIndex);

                        Assert.Same(expected, actual);
                    }
                }
            }
        }
    }

    [Fact]
    public void EofTechNoteLookup_HandlesTheEmptyAndOutOfRangeCases()
    {
        var raw = new EofRawProGuitarNote { PositionMs = 100, LengthMs = 100 };
        Assert.Null(EofProGuitarTrackParser.FindApplicableTechNote(null, raw, 0));
        Assert.Null(EofProGuitarTrackParser.FindApplicableTechNote([], raw, 0));
        Assert.Null(EofProGuitarTrackParser.FindApplicableTechNote(
            [new EofRawProGuitarNote { PositionMs = 100, NoteBitmask = 0xFF }], raw, -1));
        Assert.Null(EofProGuitarTrackParser.FindApplicableTechNote(
            [new EofRawProGuitarNote { PositionMs = 100, NoteBitmask = 0xFF }], raw, 8));
    }

    /// <summary>
    ///     The lookup runs once per single note and once per STRING of every
    ///     chord, and the old version restarted at index 0 and <c>continue</c>d
    ///     past everything before the note's onset — so the cost per call grew
    ///     with how far INTO the song the note sits. This is that shape on a
    ///     bend-heavy chart: ~10^10 comparisons before, ~10^6 after.
    /// </summary>
    [Fact]
    public void EofTechNoteLookup_ScalesToABendHeavyChart()
    {
        const int techCount = 200_000;
        const int noteCount = 20_000;

        var techNotes = new List<EofRawProGuitarNote>(techCount);
        for (var i = 0; i < techCount; i++)
        {
            techNotes.Add(new EofRawProGuitarNote
            { PositionMs = (uint)(i * 4), LengthMs = 2, NoteBitmask = 0x3F });
        }

        var bound = TimeSpan.FromSeconds(5);
        var fastest = BestOfTiming.Fastest(() =>
        {
            for (var n = 0; n < noteCount; n++)
            {
                var raw = new EofRawProGuitarNote { PositionMs = (uint)(n * 40), LengthMs = 30 };
                for (var s = 0; s < 6; s++)
                    EofProGuitarTrackParser.FindApplicableTechNote(techNotes, raw, s);
            }
        }, bound);

        Assert.True(fastest < bound,
            $"the fastest of {BestOfTiming.Attempts} lookup sweeps took {fastest.TotalSeconds:0.0}s — it is scanning from index 0 again");
    }

    private static EofRawProGuitarNote? ReferenceTechNote(
        List<EofRawProGuitarNote> techNotes, EofRawProGuitarNote raw, int stringIndex)
    {
        if (stringIndex is < 0 or > 7) return null;
        var stringBit = 1 << stringIndex;
        var noteEnd = (ulong)raw.PositionMs + raw.LengthMs;
        EofRawProGuitarNote? match = null;

        foreach (var tech in techNotes)
        {
            if (tech.Difficulty != raw.Difficulty) continue;
            if (tech.PositionMs < raw.PositionMs) continue;
            if (tech.PositionMs > noteEnd) break;
            if ((tech.NoteBitmask & stringBit) == 0) continue;
            match = tech;
        }

        return match;
    }
}
