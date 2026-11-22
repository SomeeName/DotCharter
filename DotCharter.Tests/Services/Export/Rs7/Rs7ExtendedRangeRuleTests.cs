using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     Extended model and native serialization rules: tuning and slot mappings,
///     template deduplication, chord techniques, masks, limits, plain chart padding,
///     and byte identity of stock guitar and bass arrangements.
/// </summary>
public sealed class Rs7ExtendedRangeRuleTests
{
    private const int Eight = 8;
    private static readonly int F = Rs7StringLayout.ToDomain(7, Eight);
    private static readonly int LowB = Rs7StringLayout.ToDomain(6, Eight);
    private static readonly int LowE = Rs7StringLayout.ToDomain(0, Eight);

    private static (RocksmithSng Sng, byte[] Body) Build(Arrangement arrangement, List<BeatMarker> beats)
    {
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 40_000,
            new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Unspecified)).Sng;
        return (sng, sng.Extension == null ? RocksmithSngWriter.WriteUnpacked(sng) : RsxrSngWriter.WriteUnpacked(sng));
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) EightString()
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = Eight };
        for (var i = 0; i < Eight; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    private static FractionalPosition At(List<BeatMarker> beats, int beat) =>
        TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, beats);

    private static void AddChord(Arrangement arrangement, List<BeatMarker> beats, int beat, params (int String, int Fret)[] columns)
    {
        var template = new ChordTemplate { Id = arrangement.ChordTemplates.Count, Name = "X" };
        var chord = new Chord { Position = At(beats, beat), TemplateId = template.Id };
        foreach (var (s, fret) in columns)
        {
            template.Frets[s] = fret;
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        }

        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(chord);
    }

    [Fact]
    public void Eight_string_tuning_has_eight_entries_in_package_order()
    {
        var (arrangement, beats) = EightString();
        arrangement.TuningPitches = [.. new[] { -1, -2, 0, 0, 0, 0, 0, 0 }.Select(o => new TuningOffset(o))];
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = F, Fret = 2 });

        var (sng, _) = Build(arrangement, beats);

        // Chart order is lowest first (F# B E A D G B e); the package puts E..e first, then B1, then F#1.
        Assert.Equal([0, 0, 0, 0, 0, 0, -2, -1], sng.MetaData.Tuning.Select(t => (int)t));
        Assert.Contains(sng.Levels[0].Notes, n => n.StringIndex == 7 && n.Fret == 2);
    }

    [Fact]
    public void Section_string_masks_carry_bits_6_and_7()
    {
        var (arrangement, beats) = EightString();
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = F, Fret = 2 });
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 5), StringIndex = LowB, Fret = 2 });
        AddChord(arrangement, beats, 8, (F, 0), (LowE, 2));

        var (sng, _) = Build(arrangement, beats);

        var mask = sng.Sections.Aggregate(0, (m, s) => m | (s.StringMask[0] & 0xFF));
        Assert.Equal(1 | (1 << 6) | (1 << 7), mask);
    }

    [Fact]
    public void The_internal_model_lists_both_extra_guitar_columns()
    {
        var (arrangement, beats) = EightString();
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = F, Fret = 2 });
        var (sng, _) = Build(arrangement, beats);
        Assert.Equal(8, sng.Extension!.StringCount);
        Assert.Equal(new[] { 6, 7 }, sng.Extension.Strings);
        Assert.Empty(sng.Extension.Templates!);
        Assert.Empty(sng.Extension.ChordNotes!);
    }

    [Fact]
    public void Templates_differing_only_in_the_eighth_string_stay_distinct_and_equal_ones_merge()
    {
        var (arrangement, beats) = EightString();
        // Same stock column (low E fret 2) and string 6; the F# column is open / fret 2 / open again.
        AddChord(arrangement, beats, 8, (LowE, 2), (LowB, 2), (F, 0));
        AddChord(arrangement, beats, 12, (LowE, 2), (LowB, 2), (F, 2));
        var again = new Chord { Position = At(beats, 16), TemplateId = 0 };
        foreach (var note in arrangement.Levels[0].Chords[0].ChordNotes)
            again.ChordNotes.Add(new ChordNote { StringIndex = note.StringIndex, Fret = note.Fret });
        arrangement.Levels[0].Chords.Add(again);

        var (sng, _) = Build(arrangement, beats);

        Assert.Equal(2, sng.Chords.Count);
        Assert.Equal(2, sng.Extension!.Templates!.Count);
        Assert.Equal(new RsExtColumn(0, -1), sng.Extension.Templates[0][1]);
        Assert.Equal(new RsExtColumn(2, -1), sng.Extension.Templates[1][1]);
        // The six stock columns are a valid projection: strings 6 and 7 never reach the stock slots.
        Assert.All(sng.Chords, c => Assert.Equal(6, c.Frets.Length));
        Assert.All(sng.Chords, c => Assert.Equal(2, c.Frets[0]));
        Assert.All(sng.Chords, c => Assert.All(c.Frets.Skip(1), f => Assert.Equal(-1, f)));
    }

    [Fact]
    public void An_open_eighth_string_gets_a_ChordNotes_record_and_CHNT_has_one_row_per_record()
    {
        var (arrangement, beats) = EightString();
        AddChord(arrangement, beats, 8, (LowE, 2), (F, 0));

        var (sng, _) = Build(arrangement, beats);

        Assert.Single(sng.ChordNotes);
        var row = Assert.Single(sng.Extension!.ChordNotes!);
        Assert.Equal(2, row.Length);
        Assert.Equal(0u, row[0].Mask);
        Assert.Equal(RsNoteMask.Open, row[1].Mask & RsNoteMask.Open);
        Assert.NotEqual(0u, sng.Levels[0].Notes.Single().Mask & RsNoteMask.ChordNotes);
    }

    [Fact]
    public void The_double_stop_bit_counts_the_extended_strings()
    {
        var (arrangement, beats) = EightString();
        AddChord(arrangement, beats, 8, (F, 2), (LowE, 2));
        AddChord(arrangement, beats, 12, (F, 2), (LowB, 2), (LowE, 2));

        var (sng, _) = Build(arrangement, beats);

        var notes = sng.Levels[0].Notes;
        Assert.NotEqual(0u, notes[0].Mask & RsNoteMask.DoubleStop);
        Assert.Equal(0u, notes[1].Mask & RsNoteMask.DoubleStop);
    }

    [Fact]
    public void The_plain_extended_chart_wraps_its_positional_body_and_padding()
    {
        var (arrangement, beats) = EightString();
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = F, Fret = 2 });
        var (sng, body) = Build(arrangement, beats);
        using var output = new MemoryStream();
        RsxrSngWriter.WritePlain(sng, output);
        Assert.Equal(body, Rsxr.RsxrTestSupport.Inflate(output.ToArray()));
        Assert.Equal(new byte[] { 83, 78, 88, 82, 1, 0, 8, 0 }, body[..8]);
        Assert.All(body[^256..], b => Assert.Equal(0, b));
    }

    // ---- bass --------------------------------------------------------------

    private static (Arrangement Arrangement, List<BeatMarker> Beats) Bass(int strings)
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass", StringCount = strings };
        // A standard B E A D G [C], measured from the chart's reference row.
        int[] standard = [-5, -5, -5, -5, -4, -4];
        arrangement.TuningPitches = [.. standard.Take(strings).Select(o => new TuningOffset(o))];
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void The_internal_bass_model_keeps_six_slots_and_its_string_count(int strings)
    {
        var (arrangement, beats) = Bass(strings);
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 0, Fret = 3 });
        var (sng, _) = Build(arrangement, beats);
        Assert.Equal(6, sng.MetaData.Tuning.Length);
        Assert.Equal(RsExtensionBlock.InstrumentBass, sng.Extension!.Instrument);
        Assert.Equal(strings, sng.Extension.StringCount);
        Assert.Empty(sng.Extension.Strings);
        Assert.Null(sng.Extension.Templates);
        Assert.Null(sng.Extension.ChordNotes);
    }

    [Fact]
    public void A_bass_puts_the_low_B_on_string_4_and_the_high_C_on_string_5()
    {
        var (arrangement, beats) = Bass(6);
        var notes = arrangement.Levels[0].Notes;
        notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 0, Fret = 3 }); // B
        notes.Add(new GuitarNote { Position = At(beats, 5), StringIndex = 1, Fret = 3 }); // E
        notes.Add(new GuitarNote { Position = At(beats, 6), StringIndex = 4, Fret = 3 }); // G
        notes.Add(new GuitarNote { Position = At(beats, 7), StringIndex = 5, Fret = 3 }); // C
        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 2;
        template.Frets[5] = 4;
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = At(beats, 10), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 4 });
        arrangement.Levels[0].Chords.Add(chord);

        var (sng, _) = Build(arrangement, beats);

        Assert.Equal([4, 0, 3, 5], sng.Levels[0].Notes.Where(n => n.ChordId < 0).Select(n => (int)n.StringIndex));
        Assert.Equal(1 | (1 << 3) | (1 << 4) | (1 << 5), sng.Sections.Aggregate(0, (m, s) => m | (s.StringMask[0] & 0xFF)));
        // #309: the chord stays in the stock six slots; low B (MIDI 23 + 2) and high C (48 + 4) in notes[].
        var stock = Assert.Single(sng.Chords);
        Assert.Equal([-1, -1, -1, -1, 2, 4], stock.Frets);
        Assert.Equal([-1, -1, -1, -1, 25, 52], stock.Notes);
    }

    [Fact]
    public void A_bass_tuning_keeps_the_sounding_pitch()
    {
        var (arrangement, beats) = Bass(5);
        arrangement.TuningPitches[0] = new TuningOffset(-7); // low B down a whole step: A#0
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 0, Fret = 0 });

        var (sng, _) = Build(arrangement, beats);

        Assert.Equal([0, 0, 0, 0, -2, 0], sng.MetaData.Tuning.Select(t => (int)t));
    }

    // ---- validator ------------------------------------------------------------

    private static IReadOnlyList<ExportValidationFinding> Validate(Arrangement arrangement, List<BeatMarker> beats)
    {
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return new ChartExportValidator().Validate(project);
    }

    [Theory]
    [InlineData(Eight, 7, false)]
    [InlineData(Eight, 8, true)]
    public void A_guitar_string_past_the_eighth_is_an_error(int strings, int stringIndex, bool error)
    {
        var (arrangement, beats) = EightString();
        arrangement.StringCount = strings;
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = stringIndex, Fret = 2 });

        var findings = Validate(arrangement, beats);

        Assert.Equal(error, findings.Any(f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange));
        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyStringCountUnsupported);
        Assert.Contains(findings, f => f.MessageKey == ChartExportValidator.KeyExtendedRangeGuitar);
    }

    [Theory]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, false)]
    [InlineData(6, 6, true)]
    public void A_bass_string_past_its_own_count_is_an_error(int strings, int stringIndex, bool error)
    {
        var (arrangement, beats) = Bass(strings);
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = stringIndex, Fret = 2 });

        var findings = Validate(arrangement, beats);

        Assert.Equal(error, findings.Any(f => f.MessageKey == ChartExportValidator.KeyStringOutOfRange));
        Assert.Contains(findings, f => f.MessageKey == ChartExportValidator.KeyExtendedRangeBass && f.Severity == ExportFindingSeverity.Warning);
    }

    [Fact]
    public void A_seven_string_bass_and_a_nine_string_guitar_are_refused()
    {
        var (bass, beats) = Bass(6);
        bass.StringCount = 7;
        bass.Levels[0].Notes.Add(new GuitarNote { Position = At(beats, 4), StringIndex = 0, Fret = 2 });
        Assert.Contains(Validate(bass, beats), f => f.MessageKey == ChartExportValidator.KeyStringCountUnsupported);
        Assert.NotNull(SngFormatLimits.ExportRefusal(bass));

        var (guitar, guitarBeats) = EightString();
        guitar.StringCount = 9;
        guitar.Levels[0].Notes.Add(new GuitarNote { Position = At(guitarBeats, 4), StringIndex = 0, Fret = 2 });
        Assert.Contains(Validate(guitar, guitarBeats), f => f.MessageKey == ChartExportValidator.KeyStringCountUnsupported);
        Assert.NotNull(SngFormatLimits.ExportRefusal(guitar));
    }

    // ---- stock stays stock -------------------------------------------------------

    // #565: full-body pins include the XML/SNG one-millisecond end guard.
    private const string PinnedStockGuitarBody = "b3e1436600948c89daef17a6a6c12ebbd81c959e8fe6649d550f2630edb5e0c1";
    private const string PinnedStockBassBody = "c8356f215b026a37049b0fa3c58e7ab03c3b8fc000f906f4bcd602d35b60c1b0";

    [Theory]
    [InlineData(ArrangementType.Lead, 6, PinnedStockGuitarBody)]
    [InlineData(ArrangementType.Bass, 4, PinnedStockBassBody)]
    public void Stock_arrangements_have_no_block_and_a_pinned_body(ArrangementType type, int strings, string pinned)
    {
        var (arrangement, beats) = StockChart(type, strings);
        var (sng, body) = Build(arrangement, beats);

        Assert.Null(sng.Extension);
        Assert.Equal(6, sng.MetaData.Tuning.Length);
        Assert.Equal(-1, body.AsSpan().IndexOf("RS7X"u8));
        SngWriterBytePinTests.AssertPinned(pinned, body, $"stock {type} {strings}-string body");
    }

    private static (Arrangement Arrangement, List<BeatMarker> Beats) StockChart(ArrangementType type, int strings)
    {
        var beats = Rs7TestSupport.Beats(40);
        var arrangement = new Arrangement { ArrangementType = type, Name = "Part", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        for (var s = 0; s < strings; s++)
            level.Notes.Add(new GuitarNote { Position = At(beats, 4 + s), StringIndex = s, Fret = 3 });
        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 2;
        template.Frets[1] = 4;
        arrangement.ChordTemplates.Add(template);
        level.Chords.Add(new Chord
        {
            Position = At(beats, 12), TemplateId = 0,
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 2 }, new ChordNote { StringIndex = 1, Fret = 4 } }
        });
        arrangement.Levels.Add(level);
        return (arrangement, beats);
    }
}
