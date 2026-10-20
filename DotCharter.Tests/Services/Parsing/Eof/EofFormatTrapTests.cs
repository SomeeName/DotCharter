using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     Guards the EOF format's traps (plan 6.8): the block-ID-2 size
///     exception, the conditional pro guitar note fields, OGG profile
///     synthesis, beat-index vs. floating text-event anchoring, the bend
///     strength encoding, and the ConversionReport's lossy detection.
/// </summary>
public class EofFormatTrapTests
{
    private static ChartProject MinimalProject(int beatCount = 8)
    {
        var project = new ChartProject();
        for (var i = 0; i < beatCount; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120.0, IsMeasure = i % 4 == 0 });
        return project;
    }

    // ------------------------------------------------------------------
    // Trap 1: the ID-2 global custom block's size EXCLUDES the 4-byte block
    // ID, unlike every other custom data block (eof_save_song writes the raw
    // temp-file size of the timing strings only).

    [Fact]
    public void FpBeatTimingBlock_SizeExcludesTheBlockId()
    {
        var project = MinimalProject(beatCount: 3);
        var reader = new EofPackReader(EofExporter.Export(project).Bytes);

        // Walk to the global custom data blocks exactly like the importer.
        var session = new EofParseSession(reader);
        session.ReadHeaderAndProperties();
        session.ReadIniBlocks();
        session.ReadOggProfiles();
        session.ReadBeats();
        session.ReadTextEvents();

        var blockCount = reader.ReadU32();
        Assert.Equal(1u, blockCount);
        var size = reader.ReadU32();
        var id = reader.ReadU32();
        Assert.Equal(2u, id);

        // The payload must be exactly `size` bytes of length-prefixed strings —
        // if the size included the ID the last string would run 4 bytes short.
        var start = reader.Position;
        for (var i = 0; i < 3; i++) reader.ReadString();
        Assert.Equal((int)size, reader.Position - start);

        // And the stream must land exactly on the track count (14: the
        // canonical scaffold with the empty bonus track omitted).
        Assert.Equal(14u, reader.ReadU32());
    }

    [Fact]
    public void FpBeatTimingBlock_PreservesFractionalBeatTimes()
    {
        var project = MinimalProject(beatCount: 2);
        project.Beats[1].Time = 499.9375; // Not representable in the u32 ms field.

        var path = EofExporterTests.WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, []);
            Assert.Equal(499.9375, result.Metadata.Beats[1].TimeMs, 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------
    // Trap 2: conditional pro guitar note fields — slide end fret and bend
    // strength only exist under RS_NOTATION (+ their flags), the unpitched
    // end fret only under UNPITCH_SLIDE.

    [Fact]
    public void ProGuitarNote_PlainNote_WritesNoConditionalFields()
    {
        var raw = new EofRawProGuitarNote { NoteBitmask = 0b100, PositionMs = 1000, LengthMs = 250 };
        raw.Frets[2] = 5;

        var w = new EofPackWriter();
        EofProGuitarNoteWriter.Write(w, raw);
        var bytes = w.ToArray();

        // name(2) + chordnum(1) + diff(1) + note(1) + ghost(1) + 1 fret + legacy(1) + pos(4) + len(4) + flags(4)
        Assert.Equal(20, bytes.Length);
    }

    [Theory]
    [InlineData(EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideUp, true, false, false)]
    [InlineData(EofConstants.PgFlagRsNotation | EofConstants.PgFlagBend, false, true, false)]
    [InlineData(EofConstants.PgFlagUnpitchedSlide, false, false, true)]
    [InlineData(EofConstants.PgFlagRsNotation | EofConstants.PgFlagSlideDown | EofConstants.PgFlagBend
                | EofConstants.PgFlagUnpitchedSlide, true, true, true)]
    public void ProGuitarNote_ConditionalFields_RoundTripThroughReader(uint flags, bool slide, bool bend,
        bool unpitched)
    {
        var raw = new EofRawProGuitarNote
        {
            NoteBitmask = 0b101,
            PositionMs = 2000,
            LengthMs = 500,
            Flags = flags,
            SlideEndFret = slide ? (byte)7 : (byte)0,
            BendStrength = bend ? (byte)0x83 : (byte)0,
            UnpitchedSlideEndFret = unpitched ? (byte)3 : (byte)0
        };
        raw.Frets[0] = 3;
        raw.Frets[2] = 5;

        var w = new EofPackWriter();
        EofProGuitarNoteWriter.Write(w, raw);
        var reader = new EofPackReader(w.ToArray());
        var read = EofProGuitarNoteReader.Read(reader);

        Assert.True(reader.Eof); // Byte-exact: nothing extra, nothing missing.
        Assert.Equal(raw.Flags, read.Flags);
        Assert.Equal(raw.SlideEndFret, read.SlideEndFret);
        Assert.Equal(raw.BendStrength, read.BendStrength);
        Assert.Equal(raw.UnpitchedSlideEndFret, read.UnpitchedSlideEndFret);
        Assert.Equal(raw.Frets[0], read.Frets[0]);
        Assert.Equal(raw.Frets[2], read.Frets[2]);
    }

    // ------------------------------------------------------------------
    // Trap 3: bend strength encoding — whole half-steps plain, quarter steps
    // with the MSB set (song.c eof_pro_guitar_track_get_active_bendstrength).

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(2.0, 2)]
    [InlineData(0.5, 0x81)]
    [InlineData(1.5, 0x83)]
    [InlineData(0.0, 0)]
    [InlineData(-1.0, 0)]
    public void EncodeBendStrength_MatchesEofByteFormat(double semitones, byte expected) => Assert.Equal(expected, EofNoteBuilder.EncodeBendStrength(semitones));

    // ------------------------------------------------------------------
    // Trap 4: text events anchor to beat INDICES; only off-grid events carry
    // the FLOATING_POS flag with a millisecond position.

    [Fact]
    public void TextEvents_OnBeat_UseBeatIndexAnchoring()
    {
        var project = MinimalProject();
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = EofExportTestCharts.Pos(1), StringIndex = 0, Fret = 1 });
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = EofExportTestCharts.Pos(2), Section = "verse"
        });
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = EofExportTestCharts.Pos(2, 96), Phrase = "offbeat"
        });
        arrangement.Phrases.Add("offbeat");
        project.Arrangements.Add(arrangement);

        var path = EofExporterTests.WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, [9]);

            var onBeat = Assert.Single(result.Metadata.TextEvents, e => e.Text == "verse");
            Assert.False(onBeat.IsMs);
            Assert.Equal(2u, onBeat.BeatPos); // Beat index, not milliseconds.

            var offBeat = Assert.Single(result.Metadata.TextEvents, e => e.Text == "offbeat");
            Assert.True(offBeat.IsMs);
            Assert.Equal(1250u, offBeat.BeatPos); // Beat 2.5 at 120 BPM.
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------
    // Trap 5: OGG profile synthesis.

    [Theory]
    [InlineData("", "guitar.ogg")]
    [InlineData("mysong.wav", "mysong.ogg")]
    [InlineData(@"C:\audio\track.flac", "track.ogg")]
    public void OggProfile_SynthesizedFromProjectAudio(string audioFileName, string expected)
    {
        var project = MinimalProject();
        project.AudioFileName = audioFileName;

        var path = EofExporterTests.WriteTempEof(project);
        try
        {
            var result = new EofImporter().ImportTracksWithMetadata(path, []);
            Assert.Equal(expected, result.Metadata.AudioFileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------
    // ConversionReport: lossy content is reported, never thrown.

    [Fact]
    public void Report_VocalArrangementAndOverflow_AreDropped()
    {
        var project = MinimalProject();
        project.Arrangements.Add(new Arrangement { Name = "Voice", ArrangementType = ArrangementType.Vocal });
        for (var i = 0; i < 4; i++)
            project.Arrangements.Add(new Arrangement { Name = $"Guitar {i}", ArrangementType = ArrangementType.Lead });

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Dropped && n.Subject == "Voice");
        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Dropped && n.Subject == "Guitar 3");
    }

    [Fact]
    public void Report_ThirdBassArrangement_ApproximatedIntoGuitarSlot()
    {
        var project = MinimalProject();
        for (var i = 0; i < 3; i++)
            project.Arrangements.Add(new Arrangement { Name = $"Bass {i}", ArrangementType = ArrangementType.Bass });

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Approximated && n.Subject == "Bass 2");
    }

    [Fact]
    public void ComplexBendCurve_IsNoLongerAnApproximation()
    {
        // A multi-point curve used to fold to its peak with an "Approximated"
        // report; it now rides the tech-note custom block (ID 7) in full, so
        // the export is lossless for bend shape and timing.
        var project = MinimalProject();
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var note = new GuitarNote
        {
            Position = EofExportTestCharts.Pos(1), Sustain = EofExportTestCharts.Pos(2),
            StringIndex = 2, Fret = 7
        };
        // Three-point pre-bend-release curve.
        note.BendValues.Add(new BendValue(EofExportTestCharts.Pos(1), 1.0));
        note.BendValues.Add(new BendValue(EofExportTestCharts.Pos(2), 2.0));
        note.BendValues.Add(new BendValue(EofExportTestCharts.Pos(3), 0.0));
        level.Notes.Add(note);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var result = EofExporter.Export(project);

        Assert.DoesNotContain(result.Report.Notes,
            n => n.Kind == EofConversionKind.Approximated && n.Message.Contains("Bend curve"));
    }

    [Fact]
    public void Report_OpenStringSlide_Dropped()
    {
        var project = MinimalProject();
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = EofExportTestCharts.Pos(1), StringIndex = 0, Fret = 0, SlideTo = 5
        });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Dropped && n.Message.Contains("open"));
    }

    [Fact]
    public void Report_MeasureFlagsNotDerivableFromTimeSignatures_Approximated()
    {
        var project = MinimalProject();
        project.Beats[5].IsMeasure = true; // 4/4 grid, but a measure start mid-bar.

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Approximated && n.Subject == "beats");
    }

    /// <summary>An EOF tremolo phrase covers EVERY string of a chord (its phrase
    /// status marks the whole note), so a chord tremolo on some strings only has no
    /// EOF form. One tremolo on every string does (#609).</summary>
    [Fact]
    public void Report_ChordTremoloOnSomeStrings_Dropped()
    {
        var project = MinimalProject();
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var chord = new Chord { Position = EofExportTestCharts.Pos(1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Tremolo = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Tremolo = false });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes,
            n => n.Kind == EofConversionKind.Dropped && n.Message.Contains("tremolo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Report_UnplacedPhraseAndUnusedTone_Reported()
    {
        var project = MinimalProject();
        var arrangement = new Arrangement { Name = "Lead", StartingTone = "ghost" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = EofExportTestCharts.Pos(1), StringIndex = 0, Fret = 1 });
        arrangement.Levels.Add(level);
        arrangement.Phrases.Add("never_placed");
        arrangement.ToneNames.Add("unused_tone");
        project.Arrangements.Add(arrangement);

        var result = EofExporter.Export(project);

        Assert.Contains(result.Report.Notes, n => n.Message.Contains("never_placed"));
        Assert.Contains(result.Report.Notes, n => n.Message.Contains("unused_tone"));
        Assert.Contains(result.Report.Notes, n => n.Message.Contains("ghost"));
    }
}
