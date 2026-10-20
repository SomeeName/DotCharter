using System.Text;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>The tests of <see cref="Rs7BlockExportTests" /> that need no package reader; its fixtures are in <c>Rs7BlockExportTests.Fixtures.cs</c>.</summary>
public sealed partial class Rs7BlockExportTests
{
    [Fact]
    public void A_seven_string_chart_without_chords_gets_the_empty_block_after_MetaData()
    {
        var (arrangement, beats) = Chart();
        arrangement.Levels[0].Notes.Add(Note(beats, 4, LowB, 3));

        var (sng, body) = Build(arrangement, beats);

        Assert.Equal(Convert.FromHexString(EmptySevenStringBlock), body[^65..]);
        Assert.Equal(body.Length - 65, BodyWithoutBlock(sng).Length);
        Assert.Equal(Strings, sng.MetaData.Tuning.Length);
        SngWriterBytePinTests.AssertPinned(PinnedEmptyBlockBody, body, "7-string body without chords");
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 6)]
    [InlineData(ArrangementType.Rhythm, 6)]
    [InlineData(ArrangementType.Bass, 4)]
    [InlineData(ArrangementType.Bass, 6)] // a bass that declares six strings and plays four
    public void Six_string_guitar_and_four_string_bass_stay_stock(ArrangementType type, int strings)
    {
        var beats = Rs7TestSupport.Beats(32);
        var arrangement = new Arrangement { ArrangementType = type, Name = "Part", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        level.Notes.Add(Note(beats, 4, 0, 3));
        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 2;
        template.Frets[1] = 4;
        arrangement.ChordTemplates.Add(template);
        level.Chords.Add(ChordOf(beats, 8, template, sustainBeats: 1));
        arrangement.Levels.Add(level);

        var (sng, body) = Build(arrangement, beats);

        Assert.Null(sng.Extension);
        // The body ends at MetaData: no RS7X block anywhere in it (the reader's side of
        // "nothing after MetaData" is Rs7ExtensionBlockReaderTests).
        Assert.Equal(-1, IndexOf(body, "RS7X"u8.ToArray()));
        Assert.DoesNotContain(new ChartExportValidator().Validate(Project(arrangement, beats)), f =>
            f.MessageKey is ChartExportValidator.KeyExtendedRangeGuitar or ChartExportValidator.KeyEightStringMod);
    }

    [Fact]
    public void Templates_are_not_merged_on_their_stock_columns_alone()
    {
        // rs7chart's group 3: the same stock column (low E fret 2), the 7th string at 0/2/4.
        var (arrangement, beats) = Chart();
        for (var id = 0; id < 3; id++)
        {
            var template = new ChordTemplate { Id = id, Name = "B5" };
            template.Frets[LowB] = 2 * id;
            template.Frets[LowE] = 2;
            arrangement.ChordTemplates.Add(template);
            arrangement.Levels[0].Chords.Add(ChordOf(beats, 4 + 2 * id, template));
        }

        // Two chords with NO template: the export synthesizes one per distinct shape.
        foreach (var (beat, lowB) in new[] { (12, 1), (14, 3), (16, 1) })
        {
            var chord = new Chord { Position = At(beats, beat), TemplateId = 99 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = LowB, Fret = lowB });
            chord.ChordNotes.Add(new ChordNote { StringIndex = A, Fret = 5 });
            arrangement.Levels[0].Chords.Add(chord);
        }

        var (sng, _) = Build(arrangement, beats);

        Assert.Equal(5, sng.Chords.Count);
        Assert.All(sng.Chords.Take(3), c => Assert.Equal([2, -1, -1, -1, -1, -1], c.Frets.Select(f => (int)f)));
        Assert.Equal([0, 2, 4, 1, 3], sng.Extension!.Templates!.Select(r => (int)Assert.Single(r).Fret));
        var chordIds = sng.Levels[0].Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).Select(n => n.ChordId).ToList();
        Assert.Equal([0, 1, 2, 3, 4, 3], chordIds);
    }

    [Fact]
    public void A_template_keeps_a_stock_column()
    {
        var (arrangement, beats) = Chart();
        var lowOnly = new ChordTemplate { Id = 0, Name = "B" };
        lowOnly.Frets[LowB] = 0;
        var pair = new ChordTemplate { Id = 1, Name = "C#5" };
        pair.Frets[LowB] = 2;
        pair.Frets[LowE] = 4;
        arrangement.ChordTemplates.Add(lowOnly);
        arrangement.ChordTemplates.Add(pair);
        var level = arrangement.Levels[0];
        level.Chords.Add(ChordOf(beats, 4, lowOnly));
        level.Chords.Add(ChordOf(beats, 6, pair));
        level.HandShapes.Add(new HandShape { Position = At(beats, 4), EndPosition = At(beats, 5), TemplateId = 0 });
        level.HandShapes.Add(new HandShape { Position = At(beats, 6), EndPosition = At(beats, 7), TemplateId = 1 });

        var (sng, _) = Build(arrangement, beats);

        // The 7th-string-only template is gone with its hand shape; its chord is a single note.
        var template = Assert.Single(sng.Chords);
        Assert.Contains(template.Frets, f => f >= 0);
        Assert.Equal(new RsExtColumn(2, -1), Assert.Single(sng.Extension!.Templates![0]));
        Assert.Contains(sng.Levels[0].Notes, n => (n.Mask & RsNoteMask.Chord) == 0 && n.StringIndex == 6 && n.Fret == 0);
        Assert.Equal([0], sng.Levels[0].HandShapes.Select(h => h.ChordId));
    }

    [Fact]
    public void Names_are_short_printable_ascii_and_name_byte_31_stays_zero()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "Bm♭9/F♯ with a very long name that keeps going" };
        template.Frets[LowB] = 2;
        template.Frets[LowE] = 2;
        template.Frets[A] = 4; // three strings: a two-string shape ships unnamed by default
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 4, template));

        var (sng, body) = Build(arrangement, beats);

        var name = sng.Chords[0].Name;
        Assert.Equal("Bm9/F with a very long name th", name);
        Assert.True(name.Length <= Rs7Projection.MaxChordNameLength);
        var at = IndexOf(body, Encoding.ASCII.GetBytes(name));
        Assert.True(at > 0, "chord name not in the body");
        Assert.All(body[(at + name.Length)..(at + 32)], b => Assert.Equal(0, b));
        // The chart keeps its own name.
        Assert.StartsWith("Bm♭9", arrangement.ChordTemplates[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seventh_string_technique_alone_makes_a_ChordNotes_record()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[LowB] = 2;
        template.Frets[LowE] = 4;
        arrangement.ChordTemplates.Add(template);
        var vibrato = ChordOf(beats, 4, template);
        vibrato.ChordNotes.Single(cn => cn.StringIndex == LowB).Vibrato = true;
        var plain = ChordOf(beats, 6, template);
        arrangement.Levels[0].Chords.Add(vibrato);
        arrangement.Levels[0].Chords.Add(plain);

        var (sng, _) = Build(arrangement, beats);

        var chords = sng.Levels[0].Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).ToList();
        Assert.Equal(0, chords[0].ChordNotesId);
        Assert.NotEqual(0u, chords[0].Mask & RsNoteMask.ChordNotes);
        Assert.Equal(-1, chords[1].ChordNotesId);
        Assert.Equal(0u, chords[1].Mask & RsNoteMask.ChordNotes);
        var record = Assert.Single(sng.ChordNotes);
        Assert.All(record.Mask, m => Assert.Equal(0u, m)); // the stock half has nothing
        var tech = Assert.Single(Assert.Single(sng.Extension!.ChordNotes!));
        Assert.Equal(RsNoteMask.Vibrato, tech.Mask);
        Assert.Equal(80, tech.Vibrato);
    }

    [Fact]
    public void An_open_seventh_string_makes_a_record_as_an_open_stock_string_does()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "B5" };
        template.Frets[LowB] = 0;
        template.Frets[LowE] = 2;
        template.Frets[A] = 2;
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 4, template));
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 6, template, sustainBeats: 1));
        OwnHandShapes(arrangement.Levels[0], beats);

        var (sng, _) = Build(arrangement, beats);

        // The game draws the open-string gem from the record's OPEN bit (rs7string #18
        // stage 2), so the 7th string's OPEN alone needs one — fretted stock strings add nothing.
        var chords = sng.Levels[0].Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).ToList();
        Assert.Equal([0, 1], chords.Select(c => c.ChordNotesId));
        Assert.All(chords, c => Assert.NotEqual(0u, c.Mask & RsNoteMask.ChordNotes));
        Assert.All(sng.ChordNotes[0].Mask, m => Assert.Equal(0u, m));
        Assert.Equal([RsNoteMask.Open, RsNoteMask.Open | RsNoteMask.Sustain],
            sng.Extension!.ChordNotes!.Select(r => Assert.Single(r).Mask));
    }

    [Fact]
    public void Records_are_deduplicated_on_stock_and_extended_data()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[LowB] = 2;
        template.Frets[LowE] = 4;
        arrangement.ChordTemplates.Add(template);
        var level = arrangement.Levels[0];
        foreach (var (beat, mute) in new[] { (4, MuteType.None), (6, MuteType.Palm), (8, MuteType.None), (10, MuteType.Palm) })
        {
            var chord = ChordOf(beats, beat, template, sustainBeats: 1);
            chord.ChordNotes.Single(cn => cn.StringIndex == LowB).Mute = mute;
            level.Chords.Add(chord);
        }

        OwnHandShapes(level, beats);
        var (sng, _) = Build(arrangement, beats);

        // Same stock half (sustain on low E); the 7th string decides: two records, not one.
        Assert.Equal(2, sng.ChordNotes.Count);
        Assert.Equal(sng.ChordNotes[0].Mask, sng.ChordNotes[1].Mask);
        Assert.Equal([0, 1, 0, 1], sng.Levels[0].Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).Select(n => n.ChordNotesId));
        Assert.Equal(2, sng.Extension!.ChordNotes!.Count);
        Assert.Equal(RsNoteMask.Sustain | RsNoteMask.PalmMute, sng.Extension.ChordNotes[1][0].Mask);
    }

    [Fact]
    public void Double_stop_counts_the_seventh_string()
    {
        var (arrangement, beats) = Chart();
        var pair = new ChordTemplate { Id = 0, Name = "C#5" };
        pair.Frets[LowB] = 2;
        pair.Frets[LowE] = 4;
        var triad = new ChordTemplate { Id = 1, Name = "B5" };
        triad.Frets[LowB] = 0;
        triad.Frets[LowE] = 2;
        triad.Frets[A] = 2;
        arrangement.ChordTemplates.Add(pair);
        arrangement.ChordTemplates.Add(triad);
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 4, pair));
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 6, triad));

        var (sng, _) = Build(arrangement, beats);

        // One stock column + the 7th string IS a double stop; two stock + the 7th is not.
        var chords = sng.Levels[0].Notes.Where(n => (n.Mask & RsNoteMask.Chord) != 0).ToList();
        Assert.NotEqual(0u, chords[0].Mask & RsNoteMask.DoubleStop);
        Assert.Equal(0u, chords[1].Mask & RsNoteMask.DoubleStop);
    }

    [Fact]
    public void A_section_whose_seventh_string_is_only_in_chords_sets_string_mask_bit_6()
    {
        var (arrangement, beats) = Chart();
        var template = new ChordTemplate { Id = 0, Name = "C#5" };
        template.Frets[LowB] = 2;
        template.Frets[LowE] = 4;
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels[0].Chords.Add(ChordOf(beats, 4, template));

        var (sng, _) = Build(arrangement, beats);

        var section = sng.Sections.First(s => s.StartTime <= 3.0f && 3.0f < s.EndTime);
        Assert.Equal(1 | (1 << 6), section.StringMask[0]);
    }

    /// <summary>Official Rocksmith bass charts play a 5th string in the stock six slots
    /// (the base game has them), so a bass is never refused and never gets a block.</summary>
    [Fact]
    public void A_bass_on_its_fifth_string_keeps_the_stock_six_slot_spelling()
    {
        var beats = Rs7TestSupport.Beats(32);
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass", StringCount = 5 };
        for (var i = 0; i < 5; i++) bass.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(At(beats, 4), 1, 4));
        level.Notes.Add(Note(beats, 4, 4, 3));
        bass.Levels.Add(level);

        Assert.DoesNotContain(new ChartExportValidator().Validate(Project(bass, beats)),
            f => f.Severity == ExportFindingSeverity.Error && f.MessageKey == ChartExportValidator.KeyEightStringMod);
        var sng = SngFromDomainMapper.BuildInstrumental(bass, beats, 20_000).Sng;
        Assert.Null(sng.Extension);
        Assert.Contains(sng.Levels[0].Notes, n => n.StringIndex == 4 && n.Fret == 3);
    }

    /// <summary>#885: the bodies the reader-side tests above import, pinned on the writer side.</summary>
    [Fact]
    public void The_bodies_the_import_tests_read_are_pinned()
    {
        var (chordChart, chordBeats) = SeventhStringChordChart();
        SngWriterBytePinTests.AssertPinned(PinnedSeventhStringChordBody, Build(chordChart, chordBeats).Body, "7th-string chord body");
        var (nameChart, nameBeats) = NameByte31Chart();
        SngWriterBytePinTests.AssertPinned(PinnedNameByte31Body, Build(nameChart, nameBeats).Body, "name byte 31 body");
    }

    /// <summary>
    ///     #785: a GHOST gem on the 7th string (SNG column 6) — held by the hand shape, never
    ///     played — ships ONLY in the hand shape's template: the CHDT row carries its fret,
    ///     no note or chord plays it, and the import reads the unplayed fret back as a ghost.
    /// </summary>
    [Fact]
    public void A_ghost_gem_on_the_seventh_string_ships_only_in_its_hand_shapes_template()
    {
        var (arrangement, beats) = GhostGemChart();

        var (sng, body) = Build(arrangement, beats);

        var template = Assert.Single(sng.Chords);
        Assert.Equal(5, template.Frets[0]);
        var column = Assert.Single(Assert.Single(sng.Extension!.Templates!));
        Assert.Equal(3, column.Fret);
        Assert.Equal(1, column.Finger);
        Assert.All(sng.Levels[0].Notes, n => Assert.Equal((LowE, 5), (Rs7StringLayout.ToDomain(n.StringIndex, Strings), n.Fret)));
        Assert.DoesNotContain(sng.Levels[0].Notes, n => (n.Mask & RsNoteMask.Chord) != 0);
        SngWriterBytePinTests.AssertPinned(PinnedGhostGemBody, body, "ghost gem body");
    }
}
