using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #777: the writer rules of the RS7X block (the rs7string mod's
///     <c>docs/sng-extension.md</c> §4, §5) on small synthetic charts, through the real
///     mapper, writer and reader: the block is written for a 7-string guitar and for
///     nothing else, its sections come in order even when empty, templates are never
///     merged on their stock columns alone, every template keeps a stock column, the
///     name's last byte stays 0, ChordNotes records follow the extended strings'
///     techniques, section string masks carry string 6, and the import reads it all
///     back (and the older name[31] column too). The byte-for-byte comparison with
///     <c>rs7chart</c>'s packages is <c>Rs7ReferenceRebuildTests</c>.
/// </summary>
public sealed partial class Rs7BlockExportTests
{
    private const int Strings = 7;
    private static readonly int LowB = Rs7StringLayout.ToDomain(6, Strings);
    private static readonly int LowE = Rs7StringLayout.ToDomain(0, Strings);
    private static readonly int A = Rs7StringLayout.ToDomain(1, Strings);

    /// <summary>What <c>rs7chart</c> writes for a 7-string chart without chords
    /// (<c>rs7c_runs_7str</c>, <c>rs7c_tech_a</c>): 65 bytes, all three sections.</summary>
    private const string EmptySevenStringBlock =
        "52533758010010004100000003000000" + "535452530100010005000000" + "0007010006" +
        "434844540100010004000000" + "00000000" + "43484e54010000000400000000000000";

    private const string PinnedEmptyBlockBody = "15ffa680956cac941291ed3feb9626662efd514b263670a9bd5f5f18bc5b66e3";
    private const string PinnedGhostGemBody = "3e279ca256442ccec9735f277782362763b00c8e68d08a676b116b388d2480d5";
    private const string PinnedSeventhStringChordBody = "1ec0ae8802c535402d08e294273ab94f7ebc031139ea998c459e2a0126389ce2";
    private const string PinnedNameByte31Body = "7a21d28ece0dbccbed961f7a54864e65b8b29ba4e44aa542fb248bfb2e7ce6d8";

    // ---- helpers ---------------------------------------------------------------

    private static (Arrangement Arrangement, List<BeatMarker> Beats) SeventhStringChordChart()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[LowB] = 2;
        template.Fingers[LowB] = 1;
        template.Frets[LowE] = 4;
        template.Fingers[LowE] = 3;
        arrangement.ChordTemplates.Add(template);
        var chord = ChordOf(beats, 4, template, sustainBeats: 2);
        var low = chord.ChordNotes.Single(cn => cn.StringIndex == LowB);
        low.SlideTo = 4;
        low.BendValues.Add(new BendValue(At(beats, 5), 1.0));
        arrangement.Levels[0].Chords.Add(chord);
        return (arrangement, beats);
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) NameByte31Chart()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[LowB] = 2;
        template.Frets[LowE] = 4;
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 4, template));
        return (arrangement, beats);
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) GhostGemChart()
    {
        var (arrangement, beats) = Chart();
        var grip = new ChordTemplate { Id = 0, Name = "E5", GhostMask = ChordTemplate.GhostBit(LowB), Pinned = true };
        grip.Frets[LowE] = 5;
        grip.Frets[LowB] = 3;
        grip.Fingers[LowE] = 3;
        grip.Fingers[LowB] = 1;
        arrangement.ChordTemplates.Add(grip);
        var level = arrangement.Levels[0];
        level.Notes.Add(Note(beats, 4, LowE, 5));
        level.Notes.Add(Note(beats, 5, LowE, 5));
        level.HandShapes.Add(new HandShape { Position = At(beats, 4), EndPosition = At(beats, 6), TemplateId = 0 });
        return (arrangement, beats);
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) Chart()
    {
        var beats = Rs7TestSupport.Beats(32);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = Strings };
        for (var i = 0; i < Strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    private static (RocksmithSng Sng, byte[] Body) Build(Arrangement arrangement, List<BeatMarker> beats)
    {
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 20_000,
            new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Unspecified)).Sng;
        return (sng, RocksmithSngWriter.WriteUnpacked(sng));
    }

    private static byte[] BodyWithoutBlock(RocksmithSng sng)
    {
        var block = sng.Extension;
        sng.Extension = null;
        try
        {
            return RocksmithSngWriter.WriteUnpacked(sng);
        }
        finally
        {
            sng.Extension = block;
        }
    }

    private static ChartProject Project(Arrangement arrangement, List<BeatMarker> beats)
    {
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static FractionalPosition At(List<BeatMarker> beats, int beat) =>
        TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, beats);

    private static GuitarNote Note(List<BeatMarker> beats, int beat, int stringIndex, int fret) =>
        new() { Position = At(beats, beat), StringIndex = stringIndex, Fret = fret };

    private static Chord ChordOf(List<BeatMarker> beats, int beat, ChordTemplate template, int sustainBeats = 0)
    {
        var position = At(beats, beat);
        var sustain = sustainBeats > 0 ? At(beats, beat + sustainBeats) - position : FractionalPosition.Zero;
        var chord = new Chord { Position = position, TemplateId = template.Id };
        foreach (var (s, fret) in template.Frets.OrderBy(kv => kv.Key))
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret, Sustain = sustain });
        return chord;
    }

    /// <summary>One hand shape per chord, as rs7chart writes them: a strum that repeats the
    /// chord before it with no hand shape of its own ships as a compact repeat without
    /// chord notes (RsChordDensity), which is not what these tests look at.</summary>
    private static void OwnHandShapes(ChartLevel level, List<BeatMarker> beats)
    {
        foreach (var chord in level.Chords)
        {
            level.HandShapes.Add(new HandShape
            {
                Position = chord.Position,
                EndPosition = TimelineMath.PositionFromMs(chord.Position.GetPositionMs(beats) + 300, beats),
                TemplateId = chord.TemplateId
            });
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
