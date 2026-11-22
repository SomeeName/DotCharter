using System.Text.Json;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #305/#309: rs7string's 2026-11-01 rule clarifications (RS7X spec §4, §7, §9). A 5-/6-string bass keeps
///     its low B and high C (strings 4/5) as chord columns 4/5 of one chord, in the SNG and in the XML (#309
///     reversed the forced split of #305). A 7-/8-string guitar's ChordNotes
///     record is written for an open or techniqued string, extra or stock, and the corrected §7 8-string
///     example is pinned byte for byte. Bass StringMask bits 4/5, the stock-MIDI-slot rule and the
///     manifest keys are checked too. The comparison with rs7chart is <see cref="Rs7ExtendedRangeGoldenTests" />.
/// </summary>
public sealed class Rs7Rules1101Tests
{
    private const int Eight = 8;
    private static readonly int F = Rs7StringLayout.ToDomain(7, Eight);
    private static readonly int LowB = Rs7StringLayout.ToDomain(6, Eight);
    private static readonly int LowE = Rs7StringLayout.ToDomain(0, Eight);
    private static readonly int A = Rs7StringLayout.ToDomain(1, Eight);

    // Chart order of a bass: B E A D G [C].
    private const int BassLowB = 0;
    private const int BassA = 2;
    private const int BassD = 3;
    private const int BassHighC = 5;


    private static RocksmithSng Build(Arrangement arrangement, List<BeatMarker> beats) =>
        SngFromDomainMapper.BuildInstrumental(arrangement, beats, 40_000,
            new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Unspecified)).Sng;

    private static FractionalPosition At(List<BeatMarker> beats, int beat) =>
        TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, beats);

    private static float Seconds(int beat) => (float)(beat * Rs7TestSupport.BeatMs / 1000.0);

    private static (Arrangement Arrangement, List<BeatMarker> Beats) Guitar()
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = Eight };
        for (var i = 0; i < Eight; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) Bass(int strings)
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass", StringCount = strings };
        int[] standard = [-5, -5, -5, -5, -4, -4];
        arrangement.TuningPitches = [.. standard.Take(strings).Select(o => new TuningOffset(o))];
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    private static ChordTemplate AddChord(Arrangement arrangement, List<BeatMarker> beats, int beat, string name,
        Action<ChordNote>? tweak, params (int String, int Fret)[] columns)
    {
        var template = new ChordTemplate { Id = arrangement.ChordTemplates.Count, Name = name };
        var chord = new Chord { Position = At(beats, beat), TemplateId = template.Id };
        foreach (var (s, fret) in columns)
        {
            template.Frets[s] = fret;
            var chordNote = new ChordNote { StringIndex = s, Fret = fret };
            tweak?.Invoke(chordNote);
            chord.ChordNotes.Add(chordNote);
        }

        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(chord);
        return template;
    }

    private static IEnumerable<RsNote> At(RocksmithSng sng, int beat) =>
        sng.Levels[0].Notes.Where(n => Math.Abs(n.Time - Seconds(beat)) < 0.001f);

    // ---- bass: the low B and high C are chord columns 4 and 5 (#309) -----------------------

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void A_bass_chord_keeps_the_low_B_and_high_C_as_columns_4_and_5_of_one_template(int strings)
    {
        var (arrangement, beats) = Bass(strings);
        var columns = strings == 6
            ? new[] { (BassLowB, 2), (BassA, 3), (BassD, 5), (BassHighC, 4) }
            : [(BassLowB, 2), (BassA, 3), (BassD, 5)];
        var template = AddChord(arrangement, beats, 8, "Bx", null, columns);
        template.Fingers[BassLowB] = 1;

        var sng = Build(arrangement, beats);

        // One chord, no loose single notes at its time; columns 4/5 carry fret and finger.
        var atChord = At(sng, 8).ToList();
        var chordNote = Assert.Single(atChord);
        Assert.NotEqual(0u, chordNote.Mask & RsNoteMask.Chord);
        var written = Assert.Single(sng.Chords);
        Assert.Equal(strings == 6 ? [-1, 3, 5, -1, 2, 4] : [-1, 3, 5, -1, 2, -1], written.Frets.Select(f => (int)f));
        Assert.Equal(1, written.Fingers[4]);
        Assert.NotEqual(-1, written.Notes[4]);
        // The bass block only marks the instrument.
        Assert.Null(sng.Extension!.Templates);
    }

    [Fact]
    public void A_bass_chord_technique_on_strings_4_and_5_goes_in_the_stock_ChordNotes_slots()
    {
        var (arrangement, beats) = Bass(6);
        AddChord(arrangement, beats, 8, "Bx",
            n => n.Sustain = At(beats, 9) - At(beats, 8), (BassLowB, 2), (BassHighC, 4));

        var sng = Build(arrangement, beats);

        var record = Assert.Single(sng.ChordNotes);
        Assert.NotEqual(0u, record.Mask[4] & RsNoteMask.Sustain);
        Assert.NotEqual(0u, record.Mask[5] & RsNoteMask.Sustain);
        Assert.Equal(0u, record.Mask[0]);
        Assert.Single(At(sng, 8));
    }

    [Fact]
    public void A_bass_chord_on_the_extra_strings_alone_is_one_chord_and_sets_their_StringMask_bits()
    {
        var (arrangement, beats) = Bass(6);
        AddChord(arrangement, beats, 8, "Bx", null, (BassLowB, 2), (BassHighC, 4));

        var sng = Build(arrangement, beats);

        Assert.Equal([-1, -1, -1, -1, 2, 4], Assert.Single(sng.Chords).Frets.Select(f => (int)f));
        Assert.Single(At(sng, 8), n => (n.Mask & RsNoteMask.Chord) != 0);
        var mask = sng.Sections.Aggregate(0, (m, s) => m | (s.StringMask[0] & 0xFF));
        Assert.Equal((1 << 4) | (1 << 5), mask);
    }

    [Fact]
    public void The_bass_XML_preserves_the_chord_in_positional_order()
    {
        var (arrangement, beats) = Bass(6);
        AddChord(arrangement, beats, 8, "Bx", null, (BassLowB, 2), (BassA, 3), (BassD, 5), (BassHighC, 4));
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        var song = new RsXmlSongWriter().Write(project, 30_000).Root!;

        var template = Assert.Single(song.Descendants("chordTemplate"));
        Assert.Equal(2, (int)template.Attribute("fret0")!);
        Assert.Equal(4, (int)template.Attribute("fret5")!);
        var chord = Assert.Single(song.Descendants("chord"));
        Assert.Equal(new[] { (0, 2), (2, 3), (3, 5), (5, 4) }.OrderBy(x => x),
            chord.Elements("chordNote").Select(cn => ((int)cn.Attribute("string")!, (int)cn.Attribute("fret")!)).OrderBy(x => x));
        var time = (double)chord.Attribute("time")!;
        Assert.DoesNotContain(song.Element("levels")!.Descendants("note"),
            n => Math.Abs((double)n.Attribute("time")! - time) < 0.001);
    }

    [Fact]
    public void A_stock_bass_xml_with_a_chord_loads_as_four_strings_and_keeps_the_chord()
    {
        var (arrangement, beats) = Bass(4);
        AddChord(arrangement, beats, 8, "Bx", null, (1, 2), (BassA, 3));
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        var song = new RsXmlSongWriter().Write(project, 30_000);
        var read = new RsXmlSongReader().ReadDocument(song, "bass.xml").Arrangements[0];

        Assert.Equal(4, read.StringCount);
        Assert.Single(read.Levels[0].Chords);
    }

    // ---- guitar: a chord of extra strings only stays a chord (#309) -------------------------

    [Fact]
    public void An_eight_string_dyad_of_the_two_extra_strings_is_one_chord_with_its_stock_columns_unused()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "Dy", null, (F, 2), (LowB, 3));

        var sng = Build(arrangement, beats);

        var chord = Assert.Single(sng.Chords);
        Assert.All(chord.Frets, f => Assert.Equal(-1, f));
        var row = Assert.Single(sng.Extension!.Templates!);
        Assert.Equal([new RsExtColumn(3, -1), new RsExtColumn(2, -1)], row);
        var atChord = Assert.Single(At(sng, 8));
        Assert.NotEqual(0u, atChord.Mask & RsNoteMask.Chord);
        Assert.Equal(sng.Extension.ChordNotes!.Count, sng.ChordNotes.Count);
    }

    [Fact]
    public void An_extra_only_chord_with_a_technique_fills_its_CHNT_row()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "Dy", n => n.Sustain = At(beats, 9) - At(beats, 8), (F, 2), (LowB, 3));

        var sng = Build(arrangement, beats);

        Assert.Single(sng.Chords);
        var record = Assert.Single(sng.ChordNotes);
        Assert.All(record.Mask, m => Assert.Equal(0u, m));
        var row = Assert.Single(sng.Extension!.ChordNotes!);
        Assert.All(row, tech => Assert.NotEqual(0u, tech.Mask & RsNoteMask.Sustain));
    }

    [Fact]
    public void A_seven_string_chord_of_the_one_extra_string_alone_is_a_single_note()
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 7 };
        for (var i = 0; i < 7; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        AddChord(arrangement, beats, 8, "One", null, (0, 2));

        var sng = Build(arrangement, beats);

        Assert.Empty(sng.Chords);
        Assert.Equal((6, 2), Assert.Single(At(sng, 8).Select(n => ((int)n.StringIndex, (int)n.Fret))));
    }

    [Fact]
    public void The_eight_string_xml_keeps_an_extra_only_chord_as_a_chord()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "Dy", null, (F, 2), (LowB, 3));
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        var song = new RsXmlSongWriter().Write(project, 30_000).Root!;

        var chord = Assert.Single(song.Descendants("chord"));
        Assert.Equal([(0, 2), (1, 3)], chord.Elements("chordNote")
            .Select(cn => ((int)cn.Attribute("string")!, (int)cn.Attribute("fret")!)).OrderBy(x => x));
        var time = (double)chord.Attribute("time")!;
        Assert.DoesNotContain(song.Element("levels")!.Descendants("note"),
            n => Math.Abs((double)n.Attribute("time")! - time) < 0.001);
    }

    // ---- guitar: ChordNotes records and the corrected section 7 example -----------------

    [Fact]
    public void The_eight_string_example_keeps_its_columns_and_open_technique()
    {
        var (arrangement, beats) = Guitar();
        // F#5: string 7 open, string 6 fret 2 finger 2, stock column s0 fret 2.
        var template = AddChord(arrangement, beats, 8, "F#5", null, (F, 0), (LowB, 2), (LowE, 2));
        template.Fingers[LowB] = 2;

        var sng = Build(arrangement, beats);

        Assert.Equal(new[] { new RsExtColumn(2, 2), new RsExtColumn(0, -1) }, Assert.Single(sng.Extension!.Templates!));
        var row = Assert.Single(sng.Extension.ChordNotes!);
        Assert.Equal(RsExtTech.Empty, row[0]);
        Assert.Equal(RsNoteMask.Open, row[1].Mask);
        Assert.Single(sng.ChordNotes);
    }

    [Fact]
    public void An_open_extra_string_gets_exactly_the_open_mask_in_its_CHNT_entry()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "x", null, (F, 0), (LowE, 2));

        var sng = Build(arrangement, beats);

        var row = Assert.Single(sng.Extension!.ChordNotes!);
        Assert.Equal(RsExtTech.Empty, row[0]);
        Assert.Equal(RsNoteMask.Open, row[1].Mask);
    }

    [Fact]
    public void An_open_stock_string_with_a_fretted_extra_string_still_gets_a_record()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "x", null, (LowB, 2), (LowE, 0));

        var sng = Build(arrangement, beats);

        var record = Assert.Single(sng.ChordNotes);
        Assert.Equal(RsNoteMask.Open, record.Mask[0]);
        Assert.Equal([RsExtTech.Empty, RsExtTech.Empty], Assert.Single(sng.Extension!.ChordNotes!));
    }

    [Fact]
    public void A_technique_on_an_extra_string_alone_gets_a_record_with_its_CHNT_entry()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "x", n => n.Sustain = n.StringIndex == F ? At(beats, 9) - At(beats, 8) : FractionalPosition.Zero, (F, 2), (LowE, 2));

        var sng = Build(arrangement, beats);

        Assert.Single(sng.ChordNotes);
        var row = Assert.Single(sng.Extension!.ChordNotes!);
        Assert.Equal(0u, row[0].Mask);
        Assert.NotEqual(0u, row[1].Mask & RsNoteMask.Sustain);
    }

    // ---- the rest of section 9 -----------------------------------------------------------

    [Fact]
    public void The_extra_strings_never_land_in_a_free_stock_MIDI_slot_and_chord_sections_set_bits_6_and_7()
    {
        var (arrangement, beats) = Guitar();
        AddChord(arrangement, beats, 8, "x", null, (F, 3), (LowB, 2), (LowE, 2));
        AddChord(arrangement, beats, 12, "y", null, (F, 0), (LowE, 5), (A, 5));

        var sng = Build(arrangement, beats);

        Assert.All(sng.Chords, c =>
        {
            Assert.Equal(6, c.Notes.Length);
            for (var s = 0; s < 6; s++) Assert.Equal(c.Frets[s] < 0, c.Notes[s] < 0);
        });
        Assert.Equal([40 + 2, -1, -1, -1, -1, -1], sng.Chords[0].Notes);
        // No single note exists: bits 0, 1, 6 and 7 come from the chords alone.
        var mask = sng.Sections.Aggregate(0, (m, s) => m | (s.StringMask[0] & 0xFF));
        Assert.Equal(1 | (1 << 1) | (1 << 6) | (1 << 7), mask);
    }

    [Theory]
    [InlineData(7, "string0 string1 string2 string3 string4 string5 string6")]
    [InlineData(8, "string0 string1 string2 string3 string4 string5 string6 string7")]
    public void The_manifest_tuning_numbers_every_guitar_string_by_position(int strings, string keys)
    {
        var (arrangement, beats) = Guitar();
        arrangement.StringCount = strings;
        arrangement.TuningPitches = [.. arrangement.TuningPitches.Take(strings)];
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 0, Fret = 3 });

        Assert.Equal(keys.Split(' '), TuningKeys(arrangement, beats));
    }

    [Theory]
    [InlineData(5, "string0 string1 string2 string3 string4")]
    [InlineData(6, "string0 string1 string2 string3 string4 string5")]
    public void The_manifest_tuning_numbers_every_bass_string_by_position(int strings, string keys)
    {
        var (arrangement, beats) = Bass(strings);
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 1, Fret = 3 });

        Assert.Equal(keys.Split(' '), TuningKeys(arrangement, beats));
    }

    private static List<string> TuningKeys(Arrangement arrangement, List<BeatMarker> beats)
    {
        var build = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32_000);
        var meta = new RsPackageMetadata { DlcKey = "DCRs7Keys", Title = "t", Artist = "a", AverageTempo = 120f };
        var packageArr = new RsPackageArrangement
        {
            ArrangementName = arrangement.ArrangementType.ToString(),
            PartitionName = arrangement.ArrangementType.ToString().ToLowerInvariant(),
            PersistentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            MasterId = 1, Sng = build.Sng, NoteCounts = build.NoteCounts, Domain = arrangement
        };

        using var doc = JsonDocument.Parse(RsManifestGenerator.CreateArrangementManifest(meta, packageArr));
        return [.. doc.RootElement.GetProperty("Entries").GetProperty("11111111222233334444555555555555")
            .GetProperty("Attributes").GetProperty("Tuning").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .OrderBy(k => k.StartsWith("string", StringComparison.Ordinal) ? 0 : 1)];
    }
}
