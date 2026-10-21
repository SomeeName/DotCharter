using System.Text;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A crafted chart must not crash the exporters or wrap a value silently: the validator
///     reports each bound as a normal export error, and the writers refuse or clamp on their
///     own. Synthetic charts only.
/// </summary>
public sealed class ExportBoundsTests
{
    private static List<BeatMarker> Beats(int count = 64)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120.0, BeatsInMeasure = 4, NoteDenominator = 4 });
        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    private static Arrangement MakeArrangement(IList<BeatMarker> beats, int stringCount = 6,
        ArrangementType type = ArrangementType.Lead)
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = type, StringCount = stringCount };
        for (var i = 0; i < stringCount; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.Phrases.AddRange(["COUNT", "riff", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 1500), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 28000), 2));
        arrangement.Levels.Add(MakeLevel(beats, 0, [(0, 3)]));
        return arrangement;
    }

    private static ChartLevel MakeLevel(IList<BeatMarker> beats, int difficulty, (int String, int Fret)[] notes)
    {
        var level = new ChartLevel { DifficultyIndex = difficulty };
        level.Anchors.Add(new FretAnchor(Pos(beats, 1500), 1, 4));
        for (var i = 0; i < notes.Length; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = Pos(beats, 2000 + i * 1000.0),
                StringIndex = notes[i].String,
                Fret = notes[i].Fret
            });
        }

        return level;
    }

    private static ChartProject MakeProject(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static List<ExportValidationFinding> Errors(ChartProject project, string key) =>
        [.. new ChartExportValidator().Validate(project)
            .Where(f => f.Severity == ExportFindingSeverity.Error && f.MessageKey == key)];

    // ---- manifest DNA time ------------------------------------------------------

    private static RocksmithSng DnaSng(IEnumerable<RsDna> dnas, float songLength)
    {
        var sng = new RocksmithSng { MetaData = new RsMetaData { SongLength = songLength } };
        sng.Dnas.AddRange(dnas);
        return sng;
    }

    [Fact]
    public void DnaTime_sums_every_region_and_closes_an_open_one_at_the_song_end()
    {
        var sng = DnaSng(
        [
            new RsDna(1f, 2), new RsDna(3f, 0),
            new RsDna(5f, 2), new RsDna(6.5f, 0),
            new RsDna(8f, 2)
        ], 10f);

        Assert.Equal(5.5, RsManifestGenerator.GetDnaTime(sng, 2));
        Assert.Equal(0.0, RsManifestGenerator.GetDnaTime(sng, 3));
    }

    [Fact]
    public void DnaTime_of_a_hundred_thousand_alternating_events_returns_without_recursion()
    {
        var dnas = new List<RsDna>(100_000);
        for (var i = 0; i < 50_000; i++)
        {
            dnas.Add(new RsDna(i * 2f, 2));
            dnas.Add(new RsDna(i * 2f + 1f, 0));
        }

        var started = DateTime.UtcNow;
        var total = RsManifestGenerator.GetDnaTime(DnaSng(dnas, 200_000f), 2);

        Assert.Equal(50_000.0, total, 0);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "one forward walk, not a rescan per region");
    }

    // ---- SNG mapper -------------------------------------------------------------

    [Fact]
    public void Mapper_refuses_more_levels_than_the_string_mask_table_holds()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        for (var d = 1; d < 37; d++) arrangement.Levels.Add(MakeLevel(beats, d, [(0, 3)]));

        Assert.Equal(37, arrangement.Levels.Count);
        Assert.Throws<InvalidDataException>(() => SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000));
    }

    [Fact]
    public void Mapper_accepts_exactly_the_most_levels_the_table_holds()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        for (var d = 1; d < 36; d++) arrangement.Levels.Add(MakeLevel(beats, d, [(0, 3)]));

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng;

        Assert.Equal(36, sng.Levels.Count);
    }

    [Fact]
    public void Mapper_refuses_a_level_whose_note_ids_would_wrap_a_short()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        var level = arrangement.Levels[0];
        for (var i = 0; i < short.MaxValue + 1; i++)
            level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000 + i * 0.5), StringIndex = 0, Fret = 3 });

        Assert.Throws<InvalidDataException>(() => SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000));
    }

    [Fact]
    public void Mapper_clamps_chord_template_frets_and_fingers_instead_of_wrapping_a_sbyte()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 200;
        template.Frets[1] = 3;
        template.Fingers[0] = 9;
        template.Fingers[1] = -50;
        arrangement.ChordTemplates.Add(template);

        var chord = SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000).Sng.Chords[0];

        Assert.Equal(127, chord.Frets[0]);
        Assert.Equal(3, chord.Frets[1]);
        Assert.Equal(4, chord.Fingers[0]);
        Assert.Equal(-1, chord.Fingers[1]);
    }

    [Theory]
    [InlineData(9, ArrangementType.Lead)]
    [InlineData(12, ArrangementType.Rhythm)]
    [InlineData(7, ArrangementType.Bass)]
    public void Mapper_refuses_a_string_count_the_package_cannot_spell(int strings, ArrangementType type)
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats, strings, type);

        Assert.Throws<InvalidDataException>(() => SngFromDomainMapper.BuildInstrumental(arrangement, beats, 32000));
    }

    // ---- validator --------------------------------------------------------------

    [Fact]
    public void Validator_reports_too_many_levels_as_an_error()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        for (var d = 1; d < 37; d++) arrangement.Levels.Add(MakeLevel(beats, d, [(0, 3)]));

        var found = Errors(MakeProject(arrangement, beats), ChartExportValidator.KeyTooManyLevels);

        Assert.Equal("37", Assert.Single(found).Detail);
    }

    [Fact]
    public void Validator_reports_a_level_with_more_sounds_than_a_short_can_number()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        var level = arrangement.Levels[0];
        for (var i = 0; i < 40_000; i++)
            level.Notes.Add(new GuitarNote { Position = Pos(beats, 2000 + i * 0.5), StringIndex = 0, Fret = 3 });

        var found = Errors(MakeProject(arrangement, beats), ChartExportValidator.KeyTooManyNotes);

        Assert.Equal("40001", Assert.Single(found).Detail);
    }

    [Fact]
    public void Validator_reports_a_chord_template_fret_and_finger_out_of_range()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats);
        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 200;
        template.Fingers[0] = 9;
        arrangement.ChordTemplates.Add(template);

        var project = MakeProject(arrangement, beats);

        Assert.Equal("200", Assert.Single(Errors(project, ChartExportValidator.KeyFretOutOfRange)).Detail);
        Assert.Equal("9", Assert.Single(Errors(project, ChartExportValidator.KeyFingerOutOfRange)).Detail);
    }

    [Fact]
    public void Validator_reports_a_string_the_bass_does_not_have()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats, 4, ArrangementType.Bass);
        arrangement.Levels[0] = MakeLevel(beats, 0, [(0, 3), (5, 3)]);

        var found = Errors(MakeProject(arrangement, beats), ChartExportValidator.KeyStringBeyondTuning);

        Assert.Equal("5", Assert.Single(found).Detail);
    }

    [Fact]
    public void Validator_reports_an_unsupported_string_count()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats, 10);

        var found = Errors(MakeProject(arrangement, beats), ChartExportValidator.KeyStringCountUnsupported);

        Assert.Equal("10", Assert.Single(found).Detail);
    }

    [Fact]
    public void Validator_accepts_a_four_string_bass_on_its_own_strings()
    {
        var beats = Beats();
        var arrangement = MakeArrangement(beats, 4, ArrangementType.Bass);
        arrangement.Levels[0] = MakeLevel(beats, 0, [(0, 3), (3, 3)]);

        Assert.DoesNotContain(new ChartExportValidator().Validate(MakeProject(arrangement, beats)),
            f => f.Severity == ExportFindingSeverity.Error && f.MessageKey is
                ChartExportValidator.KeyStringBeyondTuning or ChartExportValidator.KeyStringOutOfRange);
    }

    [Fact]
    public void Guitar_Pro_validation_reports_a_string_the_tuning_lacks_and_accepts_a_clean_level()
    {
        var beats = Beats();
        var bass = MakeArrangement(beats, 4, ArrangementType.Bass);
        var bad = MakeLevel(beats, 0, [(0, 3), (4, 3)]);

        var findings = new ChartExportValidator().ValidateGuitarPro(bass, bad, beats);

        Assert.Equal("4", Assert.Single(findings, f => f.Severity == ExportFindingSeverity.Error).Detail);
        Assert.Empty(new ChartExportValidator().ValidateGuitarPro(bass, MakeLevel(beats, 0, [(0, 3), (3, 30)]), beats));
    }

    // ---- strings ----------------------------------------------------------------

    [Fact]
    public void Sng_fixed_string_never_splits_a_multibyte_character_at_the_cut()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            RocksmithSngWriter.WriteFixedString(writer, new string('a', 30) + "€", 32);

        var bytes = stream.ToArray();

        Assert.Equal(32, bytes.Length);
        Assert.All(bytes[30..], b => Assert.Equal(0, b));
        Assert.Equal(new string('a', 30), Encoding.UTF8.GetString(bytes, 0, 30));
    }

    [Fact]
    public void Sng_fixed_string_keeps_a_character_that_fits_whole()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            RocksmithSngWriter.WriteFixedString(writer, new string('a', 28) + "€", 32);

        Assert.Equal(new string('a', 28) + "€", Encoding.UTF8.GetString(stream.ToArray(), 0, 31));
    }

    [Fact]
    public void Eof_string_over_the_length_prefix_is_cut_on_a_code_point_instead_of_aborting()
    {
        var writer = new EofPackWriter();
        writer.WriteString(new string('é', 40_000));

        var bytes = writer.ToArray();
        var length = bytes[0] | (bytes[1] << 8);

        Assert.Equal(65_534, length);
        Assert.Equal(2 + length, bytes.Length);
        Assert.Equal(new string('é', 32_767), Encoding.UTF8.GetString(bytes, 2, length));
    }
}
