using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

[Collection("CwdSerialized")]
public sealed class RsXmlSongRoundtripTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly string[] Expected = ["COUNT", "verse", "chorus", "END"];

    public RsXmlSongRoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterXmlTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static ChartProject BuildRichProject()
    {
        var project = new ChartProject();
        project.Chart.SongName = "Test Song";
        project.Chart.Artist = "Test Artist";
        project.Chart.ArtistSort = "Artist, Test";
        project.Chart.AlbumName = "Test Album";
        project.Chart.AlbumYear = "2024";

        // 8 beats at 120 BPM (500 ms each), every 4th is a measure
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Capo = 0,
            CentOffset = 0,
            ToneBase = "Tone_Base",
            ToneA = "Tone_A",
            ArtistName = "Test Artist",
            ArtistNameSort = "Artist, Test",
            AlbumName = "Test Album",
            AlbumYear = 2024
        };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.StringCount = 6;

        // Phrases and iterations
        arr.Phrases.Add("default");
        arr.Phrases.Add("verse");
        arr.PhraseInfos["verse"] = new PhraseInfo(1, false);
        arr.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));

        // Chord template
        var ct = new ChordTemplate { Id = 0, Name = "Em" };
        ct.Frets[0] = 0; ct.Frets[1] = 2; ct.Frets[2] = 2;
        ct.Fingers[1] = 1; ct.Fingers[2] = 2;
        arr.ChordTemplates.Add(ct);

        // Sections and events
        arr.EventPoints.Add(new EventPoint
        {
            Position = FractionalPosition.Zero,
            Section = "intro",
            Events = "B0"
        });

        // Level with notes, chords, anchors, hand shapes
        var level = new ChartLevel { DifficultyIndex = 0 };

        // Simple note at beat 0 with sustain
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero,
            Sustain = new FractionalPosition(0, new Fraction(1, 2)),
            Fret = 5,
            StringIndex = 2
        });

        // Note with bend values
        var bendNote = new GuitarNote
        {
            Position = new FractionalPosition(1),
            Fret = 7,
            StringIndex = 1,
            Sustain = new FractionalPosition(1)
        };
        bendNote.BendValues.Add(new BendValue(new FractionalPosition(1), 1.0));
        bendNote.BendValues.Add(new BendValue(new FractionalPosition(1, new Fraction(1, 2)), 0.0));
        level.Notes.Add(bendNote);

        // Chord with chord notes
        var chord = new Chord
        {
            Position = new FractionalPosition(2),
            TemplateId = 0
        };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0, Sustain = FractionalPosition.Zero });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2, Sustain = FractionalPosition.Zero });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2, Sustain = FractionalPosition.Zero });
        level.Chords.Add(chord);

        // Anchor
        level.Anchors.Add(new FretAnchor(FractionalPosition.Zero, 1));

        // Hand shape
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(3),
            TemplateId = 0
        });

        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }

    [Fact]
    public void SaveLoad_PreservesHeaderFields()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        Assert.True(File.Exists(path));

        var loaded = ser.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(original.Chart.SongName, loaded.Chart.SongName);
        Assert.Single(loaded.Arrangements);
    }

    [Fact]
    public void SaveLoad_PreservesArrangementType()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal(ArrangementType.Lead, loaded.Arrangements[0].ArrangementType);
    }

    [Fact]
    public void SaveLoad_PreservesBeatsAndTempo()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal(original.Beats.Count, loaded.Beats.Count);
        for (var i = 0; i < original.Beats.Count; i++)
            Assert.Equal(original.Beats[i].Time, loaded.Beats[i].Time, 1.0);
    }

    [Fact]
    public void SaveLoad_PreservesBeatMeasureFlags()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        for (var i = 0; i < original.Beats.Count; i++)
            Assert.Equal(original.Beats[i].IsMeasure, loaded.Beats[i].IsMeasure);
    }

    [Fact]
    public void SaveLoad_PreservesNoteCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origLevel = original.Arrangements[0].Levels[0];
        var loadedLevel = loaded.Arrangements[0].Levels[0];
        Assert.Equal(origLevel.Notes.Count, loadedLevel.Notes.Count);
    }

    [Fact]
    public void SaveLoad_PreservesNoteFretAndString()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origNote = original.Arrangements[0].Levels[0].Notes[0];
        var loadedNote = loaded.Arrangements[0].Levels[0].Notes[0];
        Assert.Equal(origNote.Fret, loadedNote.Fret);
        Assert.Equal(origNote.StringIndex, loadedNote.StringIndex);
    }

    [Fact]
    public void SaveLoad_PreservesChordCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origLevel = original.Arrangements[0].Levels[0];
        var loadedLevel = loaded.Arrangements[0].Levels[0];
        Assert.Equal(origLevel.Chords.Count, loadedLevel.Chords.Count);
    }

    [Fact]
    public void SaveLoad_PreservesChordNoteCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origChord = original.Arrangements[0].Levels[0].Chords[0];
        var loadedChord = loaded.Arrangements[0].Levels[0].Chords[0];
        Assert.Equal(origChord.ChordNotes.Count, loadedChord.ChordNotes.Count);
    }

    [Fact]
    public void SaveLoad_PreservesAnchorCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origLevel = original.Arrangements[0].Levels[0];
        var loadedLevel = loaded.Arrangements[0].Levels[0];
        Assert.Equal(origLevel.Anchors.Count, loadedLevel.Anchors.Count);
    }

    [Fact]
    public void SaveLoad_PreservesHandShapeCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal(
            original.Arrangements[0].Levels[0].HandShapes.Count,
            loaded.Arrangements[0].Levels[0].HandShapes.Count);
    }

    [Fact]
    public void SaveLoad_PreservesChordTemplateCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal(
            original.Arrangements[0].ChordTemplates.Count,
            loaded.Arrangements[0].ChordTemplates.Count);
    }

    [Fact]
    public void SaveLoad_PreservesPhraseCount()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal(
            original.Arrangements[0].Phrases.Count,
            loaded.Arrangements[0].Phrases.Count);
    }

    [Fact]
    public void SaveLoad_PreservesSection()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var sections = loaded.Arrangements[0].EventPoints
            .Where(ep => !string.IsNullOrEmpty(ep.Section))
            .ToList();
        // The authored section, then the closing noguitar every RS export gets
        // (EOF parity, EndNoGuitarSection) — which the chart now owns.
        Assert.Equal(["intro", "noguitar"], sections.Select(s => s.Section));
    }

    [Fact]
    public void SaveLoad_PreservesToneSlotNames()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        Assert.Equal("Tone_Base", loaded.Arrangements[0].ToneBase);
        Assert.Equal("Tone_A", loaded.Arrangements[0].ToneA);
    }

    [Fact]
    public void Save_ProducesValidXml()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);

        // Must parse without exception
        var doc = XDocument.Load(path);
        Assert.Equal("song", doc.Root?.Name.LocalName);
    }

    [Fact]
    public void Save_WritesXmlDeclaration()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var text = File.ReadAllText(path);

        Assert.StartsWith("<?xml", text);
    }

    [Fact]
    public void BendValues_RoundTrip()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origNote = original.Arrangements[0].Levels[0].Notes[1];
        var loadedNote = loaded.Arrangements[0].Levels[0].Notes[1];

        Assert.Equal(origNote.BendValues.Count, loadedNote.BendValues.Count);
        Assert.Equal(origNote.BendValues[0].Step, loadedNote.BendValues[0].Step, 2);
    }

    [Fact]
    public void Save_DoesNotWriteNullChordTemplateFingers()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);

        var doc = XDocument.Load(path);
        var chordTemplate = doc.Descendants("chordTemplate").Single();

        Assert.Null(chordTemplate.Attribute("finger0"));
        Assert.Equal("1", chordTemplate.Attribute("finger1")?.Value);
        Assert.Equal("2", chordTemplate.Attribute("finger2")?.Value);
    }

    [Fact]
    public void Load_NullChordTemplateFinger_IgnoresSentinelAttribute()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();

        ser.Save(path, original);

        var xml = File.ReadAllText(path);
        xml = xml.Replace("fret0=\"0\"", "finger0=\"null\" fret0=\"0\"");
        File.WriteAllText(path, xml);

        var loaded = ser.Load(path);
        var template = loaded.Arrangements[0].ChordTemplates[0];

        Assert.False(template.Fingers.ContainsKey(0));
        Assert.Equal(1, template.Fingers[1]);
        Assert.Equal(2, template.Fingers[2]);
    }

    [Fact]
    public void Save_FallsBackArtistSortToArtistName()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var project = BuildRichProject();
        project.Chart.ArtistSort = "";
        project.Arrangements[0].ArtistNameSort = "";

        ser.Save(path, project);

        var doc = XDocument.Load(path);
        Assert.Equal("Test Artist", doc.Root?.Element("artistNameSort")?.Value);
    }

    [Fact]
    public void Save_DerivesPhraseIterationsAndMaxDifficultyFromEventPoints()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var project = new ChartProject();
        project.Chart.SongName = "Phrase Test";
        project.Chart.Artist = "Artist";

        for (var i = 0; i < 6; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(1),
            Phrase = "verse"
        });
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(3),
            Phrase = "chorus"
        });

        arrangement.Levels.Add(new ChartLevel
        {
            DifficultyIndex = 0,
            Notes =
            {
                new GuitarNote
                {
                    Position = new FractionalPosition(1, new Fraction(1, 2)),
                    StringIndex = 0,
                    Fret = 3
                }
            }
        });
        arrangement.Levels.Add(new ChartLevel
        {
            DifficultyIndex = 2,
            Notes =
            {
                new GuitarNote
                {
                    Position = new FractionalPosition(3, new Fraction(1, 2)),
                    Sustain = new FractionalPosition(1),
                    StringIndex = 1,
                    Fret = 7
                }
            }
        });

        project.Arrangements.Add(arrangement);

        ser.Save(path, project);

        var doc = XDocument.Load(path);
        var phrases = doc.Descendants("phrase")
            .Select(el => new
            {
                Name = el.Attribute("name")?.Value,
                MaxDifficulty = el.Attribute("maxDifficulty")?.Value
            })
            .ToList();
        var iterations = doc.Descendants("phraseIteration")
            .Select(el => new
            {
                Time = el.Attribute("time")?.Value,
                PhraseId = el.Attribute("phraseId")?.Value
            })
            .ToList();

        Assert.Equal(Expected, phrases.Select(p => p.Name));
        Assert.Equal("0", phrases.Single(p => p.Name == "verse").MaxDifficulty);
        Assert.Equal("2", phrases.Single(p => p.Name == "chorus").MaxDifficulty);
        Assert.Equal(4, iterations.Count);
        Assert.Equal("0.000", iterations[0].Time);
        Assert.Equal("0", iterations[0].PhraseId);
        Assert.Equal("0.500", iterations[1].Time);
        Assert.Equal("1.500", iterations[2].Time);
        Assert.Equal("2.500", iterations[3].Time);
    }

    [Fact]
    public void Save_WritesDerivedArrangementPropertiesAndAverageTempo()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var project = new ChartProject();
        project.Chart.SongName = "Property Test";
        project.Chart.Artist = "Artist";

        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i == 0 });

        var arrangement = new Arrangement();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2, [2] = 2 },
            Fingers = new Dictionary<int, int> { [1] = 1, [2] = 1 }
        });
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1,
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2 }
        });

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0),
            Sustain = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 5,
            Hopo = HopoType.HammerOn,
            Mute = MuteType.Palm,
            SlideTo = 7,
            Vibrato = true
        });
        var bendNote = new GuitarNote
        {
            Position = new FractionalPosition(1),
            StringIndex = 1,
            Fret = 7
        };
        bendNote.BendValues.Add(new BendValue(new FractionalPosition(1), 1.0));
        level.Notes.Add(bendNote);
        var chord = new Chord
        {
            Position = new FractionalPosition(2),
            TemplateId = 0
        };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        level.Chords.Add(chord);
        var doubleStop = new Chord
        {
            Position = new FractionalPosition(3),
            TemplateId = 1
        };
        doubleStop.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        doubleStop.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        level.Chords.Add(doubleStop);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        ser.Save(path, project);

        var doc = XDocument.Load(path);
        var averageTempo = double.Parse(doc.Root!.Element("averageTempo")!.Value, CultureInfo.InvariantCulture);
        var properties = doc.Root.Element("arrangementProperties");

        // 4 beats at 0/500/1000/1500 ms is 3 intervals of 500 ms = 120 BPM,
        // which is what the fixture itself declares (Bpm = 120). This asserted
        // 160 while averageTempo divided the span by beats.Count (4) instead of
        // the interval count (beats.Count - 1 = 3); the expectation encoded the
        // off-by-one rather than the musical truth. EOF has the identical bug
        // (EOF_SRC/rs.c:1779) — we deliberately do not mirror it.
        Assert.Equal(120.0, averageTempo, 3);
        Assert.Equal("1", properties?.Attribute("standardTuning")?.Value);
        Assert.Equal("1", properties?.Attribute("nonStandardChords")?.Value);
        Assert.Equal("1", properties?.Attribute("barreChords")?.Value);
        Assert.Equal("1", properties?.Attribute("openChords")?.Value);
        Assert.Equal("1", properties?.Attribute("doubleStops")?.Value);
        Assert.Equal("1", properties?.Attribute("palmMutes")?.Value);
        Assert.Equal("1", properties?.Attribute("hopo")?.Value);
        Assert.Equal("1", properties?.Attribute("slides")?.Value);
        Assert.Equal("1", properties?.Attribute("bends")?.Value);
        Assert.Equal("1", properties?.Attribute("vibrato")?.Value);
        Assert.Equal("1", properties?.Attribute("sustain")?.Value);
    }

    [Fact]
    public void Save_WritesTimeSignatureEventsFromBeatMap()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var project = new ChartProject();
        project.Chart.SongName = "Time Signature Test";
        project.Chart.Artist = "Artist";

        project.Beats.Add(new BeatMarker { Time = 250, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = true });
        project.Beats.Add(new BeatMarker { Time = 750, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.Beats.Add(new BeatMarker { Time = 1250, Bpm = 120, BeatsInMeasure = 3, NoteDenominator = 4, IsMeasure = true });
        project.Beats.Add(new BeatMarker { Time = 1750, Bpm = 120, BeatsInMeasure = 3, NoteDenominator = 4 });

        var arrangement = new Arrangement();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(arrangement);

        ser.Save(path, project);

        var doc = XDocument.Load(path);
        var events = doc.Descendants("event")
            .Select(el => $"{el.Attribute("time")?.Value}:{el.Attribute("code")?.Value}")
            .ToList();

        Assert.Equal(new[] { "0.250:TS:4/4", "1.250:TS:3/4" }, events);
    }

    /// <summary>
    /// #320: the writer emits <c>ignore</c> on a chord note and the vibrato
    /// STRENGTH (40/80/120), but the reader dropped the first and kept only a
    /// bool for the second, so every imported vibrato re-exported as 80.
    /// </summary>
    [Fact]
    public void SaveLoad_PreservesChordNoteIgnoreAndVibratoStrength()
    {
        var ser = new XmlChartSerializer();
        var path = TempPath("song.xml");
        var original = BuildRichProject();
        var level = original.Arrangements[0].Levels[0];
        level.Notes[0].Vibrato = true;
        level.Notes[0].VibratoStrength = 120;
        level.Chords[0].ChordNotes[1].Ignore = true;
        level.Chords[0].ChordNotes[2].Vibrato = true;
        level.Chords[0].ChordNotes[2].VibratoStrength = 120;

        ser.Save(path, original);
        var loaded = ser.Load(path).Arrangements[0].Levels[0];

        Assert.Equal(120, loaded.Notes[0].VibratoStrength);
        Assert.True(loaded.Chords[0].ChordNotes[1].Ignore);
        Assert.False(loaded.Chords[0].ChordNotes[0].Ignore);
        Assert.Equal(120, loaded.Chords[0].ChordNotes[2].VibratoStrength);
    }
}
