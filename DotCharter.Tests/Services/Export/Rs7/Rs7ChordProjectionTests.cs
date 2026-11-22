using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #743, #777, #309: a 7-string chord on the 7th string always reaches the SNG as ONE chord (there is no
///     split option any more; rs7string 1.0 reads the RS7X block natively): its six-string columns are the
///     chord template and its 7th string is in the block. The six-string-only copy still breaks a chord up
///     (<c>Rs7ProjectionSixStringTests</c>). The byte-for-byte checks against <c>rs7chart</c>'s own packages
///     are <c>Rs7ReferenceRebuildTests</c>.
/// </summary>
public sealed class Rs7ChordProjectionTests
{
    private const int Strings = 7;
    private static readonly int LowB = Rs7StringLayout.ToDomain(6, Strings);
    private static readonly int LowE = Rs7StringLayout.ToDomain(0, Strings);
    private static readonly int A = Rs7StringLayout.ToDomain(1, Strings);

    [Fact]
    public void A_chord_keeps_the_seventh_string_in_the_block()
    {
        var (arrangement, beats) = Chart();

        var result = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 30_000);

        var notes = result.Sng.Levels.Single().Notes;
        Assert.Equal([(-1, -1, true)], At(notes, 3.0f));
        Assert.Equal([(-1, -1, true)], At(notes, 6.0f));
        Assert.Equal(3, result.Sng.Chords.Count);
        // Stock columns: the six-string projection only; the 7th string is in CHDT.
        Assert.Equal([[4, -1, -1, -1, -1, -1], [2, 2, -1, -1, -1, -1], [3, 5, -1, -1, -1, -1]],
            result.Sng.Chords.Select(c => c.Frets.Select(f => (int)f).ToArray()).ToArray());
        Assert.Equal([new RsExtColumn(2, -1), new RsExtColumn(0, -1), RsExtColumn.None],
            result.Sng.Extension!.Templates!.Select(r => Assert.Single(r)));
    }

    private static List<(int String, int Fret, bool IsChord)> At(List<RsNote> notes, float time) =>
        [.. notes.Where(n => Math.Abs(n.Time - time) < 0.001f)
            .Select(n => ((n.Mask & RsNoteMask.Chord) != 0 ? -1 : n.StringIndex,
                (n.Mask & RsNoteMask.Chord) != 0 ? -1 : n.Fret, (n.Mask & RsNoteMask.Chord) != 0))
            .OrderBy(n => n.Item3).ThenBy(n => n.Item1)];

    /// <summary>A pair at 3 s, a triad at 6 s and a native-only chord at 9 s.</summary>
    private static (Arrangement Arrangement, List<BeatMarker> Beats) Chart()
    {
        var beats = Rs7TestSupport.Beats(48);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = Strings };
        for (var i = 0; i < Strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var pair = new ChordTemplate { Id = 0, Name = "C#5" };
        pair.Frets[LowB] = 2;
        pair.Frets[LowE] = 4;
        var triad = new ChordTemplate { Id = 1, Name = "B5x" };
        triad.Frets[LowB] = 0;
        triad.Frets[LowE] = 2;
        triad.Frets[A] = 2;
        var native = new ChordTemplate { Id = 2, Name = "N5" };
        native.Frets[LowE] = 3;
        native.Frets[A] = 5;
        arrangement.ChordTemplates.Add(pair);
        arrangement.ChordTemplates.Add(triad);
        arrangement.ChordTemplates.Add(native);

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Chords.Add(Chord(beats, 4, pair));
        level.Chords.Add(Chord(beats, 8, triad));
        level.Chords.Add(Chord(beats, 12, native));
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(4 * Rs7TestSupport.BeatMs, beats), 1, 5));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    private static Chord Chord(List<BeatMarker> beats, int beat, ChordTemplate template)
    {
        var chord = new Chord
        {
            Position = TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, beats), TemplateId = template.Id
        };
        foreach (var (s, fret) in template.Frets.OrderBy(kv => kv.Key))
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        return chord;
    }
}
