using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #743, #777: the two ways a 7-string chord on the 7th string reaches the SNG. Split
///     writes it the way <c>rs7chart</c>'s split mode does — native columns as a chord
///     (a single note when only one is left), every extra string a simultaneous single
///     note, a template only such chords played left out, and an RS7X block whose
///     template columns are all -1 — and projection (the default) keeps the chord whole,
///     its 7th string in the block. The byte-for-byte checks against <c>rs7chart</c>'s
///     own packages are <c>Rs7ReferenceRebuildTests</c>.
/// </summary>
public sealed class Rs7SplitChordModeTests
{
    private const int Strings = 7;
    private static readonly int LowB = Rs7StringLayout.ToDomain(6, Strings);
    private static readonly int LowE = Rs7StringLayout.ToDomain(0, Strings);
    private static readonly int A = Rs7StringLayout.ToDomain(1, Strings);

    [Fact]
    public void Split_writes_extra_strings_as_single_notes_beside_the_native_chord()
    {
        var (arrangement, beats) = Chart();

        var result = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 30_000, rs7ChordMode: Rs7ChordMode.Split);

        var notes = result.Sng.Levels.Single().Notes;
        // The pair (low B 2 + low E 4): two single notes, no chord.
        Assert.Equal([(0, 4, false), (6, 2, false)], At(notes, 3.0f));
        // The triad (low B 0 + low E 2 + A 2): the native two as a chord, low B a single note.
        Assert.Equal([(6, 0, false), (-1, -1, true)], At(notes, 6.0f));
        // The native-only chord is untouched.
        Assert.Equal([(-1, -1, true)], At(notes, 9.0f));

        // The pair's template is gone; the triad's and the native chord's stay, six wide.
        Assert.Equal([[2, 2, -1, -1, -1, -1], [3, 5, -1, -1, -1, -1]],
            result.Sng.Chords.Select(c => c.Frets.Select(f => (int)f).ToArray()).ToArray());
        // The block is there (7 strings), but no template claims the 7th string, and the
        // chart itself keeps all three templates.
        var block = Assert.IsType<RsExtensionBlock>(result.Sng.Extension);
        Assert.Equal([RsExtColumn.None, RsExtColumn.None], block.Templates!.Select(r => Assert.Single(r)));
        Assert.Empty(block.ChordNotes!);
        Assert.Equal(3, arrangement.ChordTemplates.Count);
        // String 6 plays, so the sections say so.
        Assert.Contains(result.Sng.Sections, s => (s.StringMask[0] & (1 << 6)) != 0);
    }

    [Fact]
    public void Projection_stays_the_default_and_keeps_the_seventh_string_in_the_block()
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
