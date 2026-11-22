using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
/// Verifies <see cref="RsXmlImporter"/> merges song / vocals / showlights RS-XML
/// documents into an existing <see cref="ChartProject"/> without disturbing
/// the host's audio, beats or other arrangements.
/// </summary>
public class RsXmlImporterTests
{
    private const string MissingSongXml =
        "No RS-XML song fixture: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplySong_PreservesSourceTimes_OnTheLiveHostGrid(bool emptyHostAtPrepare)
    {
        const string xml =
            """
            <song>
              <arrangement>Lead</arrangement>
              <ebeats>
                <ebeat time="0.000" measure="0" />
                <ebeat time="1.000" measure="-1" />
                <ebeat time="2.000" measure="-1" />
                <ebeat time="3.000" measure="-1" />
              </ebeats>
              <phrases><phrase name="intro" maxDifficulty="0" /></phrases>
              <phraseIterations><phraseIteration time="1.000" phraseId="0" /></phraseIterations>
              <chordTemplates><chordTemplate chordName="Test" fret0="3" fret1="5" /></chordTemplates>
              <tones><tone time="1.000" name="clean" /></tones>
              <events><event time="1.000" code="B0" /></events>
              <levels><level difficulty="0">
                <notes><note time="1.000" string="2" fret="7" sustain="1.000">
                  <bendValues><bendValue time="1.500" step="1" /></bendValues>
                </note></notes>
                <chords><chord time="2.000" chordId="0">
                  <chordNote time="2.000" string="0" fret="3" sustain="1.000">
                    <bendValues><bendValue time="2.500" step="1" /></bendValues>
                  </chordNote>
                  <chordNote time="2.000" string="1" fret="5" sustain="0.500" />
                </chord></chords>
                <anchors><anchor time="1.000" fret="3" width="4" /></anchors>
                <fretHandMutes><fretHandMute time="1.000" /></fretHandMutes>
                <handShapes><handShape startTime="1.000" endTime="3.000" chordId="0" /></handShapes>
              </level></levels>
            </song>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rsxml_host_grid_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        try
        {
            var host = new ChartProject();
            if (!emptyHostAtPrepare) host.Beats.Add(new BeatMarker { Time = 0 });
            var plan = RsXmlImporter.Prepare(path, host.Beats);
            // Apply must use the live grid, even if it appeared after Prepare.
            host.Beats.Clear();
            foreach (var time in new[] { 0.0, 500.0, 1000.0, 2000.0, 3000.0 })
                host.Beats.Add(new BeatMarker { Time = time });
            var existing = new Arrangement { Name = "Existing" };
            existing.Levels.Add(new ChartLevel());
            existing.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(1, Fraction.Zero) });
            host.Arrangements.Add(existing);

            var result = new RsXmlImporter().Apply(host, plan);

            Assert.False(result.ReplacedHostBeats);
            Assert.Equal(new[] { 0.0, 500.0, 1000.0, 2000.0, 3000.0 }, host.Beats.Select(b => b.Time));
            Assert.Equal(1, existing.Levels[0].Notes[0].Position.BeatId);
            var imported = host.Arrangements[1];
            var level = Assert.Single(imported.Levels);
            var note = Assert.Single(level.Notes);
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(note.Position, host.Beats));
            Assert.Equal(2000, TimelineMath.CalculateTimeMs(note.EndPosition, host.Beats));
            Assert.Equal(1500, TimelineMath.CalculateTimeMs(Assert.Single(note.BendValues).Position, host.Beats));
            var chord = Assert.Single(level.Chords);
            Assert.Equal(2000, TimelineMath.CalculateTimeMs(chord.Position, host.Beats));
            Assert.Equal(3000, TimelineMath.CalculateTimeMs(chord.Position + chord.ChordNotes[0].Sustain, host.Beats));
            Assert.Equal(2500, TimelineMath.CalculateTimeMs(chord.Position + chord.ChordNotes[1].Sustain, host.Beats));
            Assert.Equal(2500, TimelineMath.CalculateTimeMs(Assert.Single(chord.ChordNotes[0].BendValues).Position, host.Beats));
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(Assert.Single(level.Anchors).Position, host.Beats));
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(Assert.Single(level.FretHandMutes).Position, host.Beats));
            var shape = Assert.Single(level.HandShapes);
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(shape.Position, host.Beats));
            Assert.Equal(3000, TimelineMath.CalculateTimeMs(shape.EndPosition, host.Beats));
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(Assert.Single(imported.EventPoints).Position, host.Beats));
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(Assert.Single(imported.ToneChanges).Position, host.Beats));
            Assert.Equal(1000, TimelineMath.CalculateTimeMs(Assert.Single(imported.PhraseIterations).Position, host.Beats));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportInto_SongXml_AppendsArrangement()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        var host = new ChartProject { AudioFileName = "host.ogg" };
        host.Arrangements.Add(new Arrangement { Name = "Existing" });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Song, result.Kind);
        Assert.Equal(2, host.Arrangements.Count);
        Assert.Equal("Existing", host.Arrangements[0].Name);
        // Beats came from the imported XML because host had none.
        Assert.True(result.ReplacedHostBeats);
        Assert.NotEmpty(host.Beats);
        // Audio name preserved.
        Assert.Equal("host.ogg", host.AudioFileName);
    }

    [Fact]
    public void ImportInto_SongXml_PreservesHostBeatsWhenAlreadySet()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        var host = new ChartProject();
        host.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        host.Beats.Add(new BeatMarker { Time = 500 });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Song, result.Kind);
        Assert.False(result.ReplacedHostBeats);
        Assert.Equal(2, host.Beats.Count);
    }

    [Fact]
    public void ImportInto_SongXml_DisambiguatesNameCollision()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        // Pre-load the same file via reader to learn the name it would import as.
        var sampleProject = new XmlChartSerializer().Load(path);
        var importedName = sampleProject.Arrangements.First().Name;

        var host = new ChartProject();
        host.Arrangements.Add(new Arrangement { Name = importedName });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(2, host.Arrangements.Count);
        Assert.NotEqual(importedName, host.Arrangements[1].Name);
        Assert.Contains("(2)", host.Arrangements[1].Name);
        Assert.Equal(host.Arrangements[1].Name, result.AddedArrangementName);
    }

    [Theory]
    [InlineData(MinNoteDistanceMode.Milliseconds, 62.5, 62.5)]
    [InlineData(MinNoteDistanceMode.MeasureFraction, 23.4375, 39.0625)]
    public void ImportVocalsUsesHostSpacingAcrossMeterChanges(MinNoteDistanceMode mode,
        double firstMeasureGapMs, double secondMeasureGapMs)
    {
        var host = new ChartProject();
        for (var i = 0; i < 8; i++)
        {
            host.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, IsMeasure = i is 0 or 3, BeatsInMeasure = i < 3 ? 3 : 5
            });
        }
        var config = new AppConfig { MinNoteDistanceMode = mode, MinNoteDistanceMs = 62.5, MinNoteDistanceDivisor = 64 };
        const string xml = """
            <vocals>
              <vocal time="0.5" length="0.125" lyric="a" />
              <vocal time="1" length="0.125" lyric="b" />
              <vocal time="1.5" length="0.125" lyric="c" />
              <vocal time="2" length="0.125" lyric="d+" />
            </vocals>
            """;

        ImportVocals(host, xml, config);

        var lyrics = Assert.Single(Assert.Single(host.VocalTracks).Phrases).Lyrics;
        Assert.Equal([500, 1000, 1500, 2000], lyrics.Select(lyric => (int)lyric.Position.GetPositionMs(host.Beats)));
        Assert.Equal(1000 - firstMeasureGapMs, lyrics[0].EndPosition.GetPositionMs(host.Beats), 1.0);
        Assert.Equal(1500 - firstMeasureGapMs, lyrics[1].EndPosition.GetPositionMs(host.Beats), 1.0);
        Assert.Equal(2000 - secondMeasureGapMs, lyrics[2].EndPosition.GetPositionMs(host.Beats), 1.0);
        Assert.Equal(2125, lyrics[3].EndPosition.GetPositionMs(host.Beats), 1.0);
    }

    [Fact]
    public void ImportVocalsWithWordSpanningOffKeepsWordFinalEndsAndSpansInsideWords()
    {
        var host = new ChartProject();
        for (var i = 0; i < 8; i++)
            host.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });
        const string xml = """
            <vocals>
              <vocal time="0" length="0.125" lyric="a" />
              <vocal time="0.5" length="0.125" lyric="vo-" />
              <vocal time="1" length="0.125" lyric="cal+" />
            </vocals>
            """;

        ImportVocals(host, xml, new AppConfig { LyricSpanWordGap = false });

        var lyrics = Assert.Single(Assert.Single(host.VocalTracks).Phrases).Lyrics;
        Assert.Equal(125, lyrics[0].EndPosition.GetPositionMs(host.Beats), 1.0);
        Assert.Equal(968.75, lyrics[1].EndPosition.GetPositionMs(host.Beats), 1.0);
    }

    [Fact]
    public void ImportVocalsWithoutHostBeatsUsesTheReadersSecondsGrid()
    {
        var host = new ChartProject();
        const string xml = """
            <vocals>
              <vocal time="0" length="0.125" lyric="a" />
              <vocal time="1" length="0.125" lyric="b+" />
            </vocals>
            """;
        var config = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds, MinNoteDistanceMs = 125 };

        ImportVocals(host, xml, config);

        var lyrics = Assert.Single(Assert.Single(host.VocalTracks).Phrases).Lyrics;
        Assert.Equal(new FractionalPosition(new Fraction(7, 8)), lyrics[0].EndPosition);
    }

    private static void ImportVocals(ChartProject host, string xml, AppConfig config)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rsxml_vocals_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        try
        {
            var plan = RsXmlImporter.Prepare(path, host.Beats, config);
            var result = new RsXmlImporter().Apply(host, plan);
            Assert.Equal(RsXmlImportKind.Vocals, result.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportInto_VocalsXml_AddsVocalTrack()
    {
        var path = FindFirstVocalFile();
        if (path == null)
            Assert.Skip("No RS-XML vocals fixture: TestData/EOF/**/*VOCAL*.xml is absent (TestData/ is git-ignored).");

        var host = new ChartProject();
        // Provide beats so vocals are positioned correctly.
        for (var i = 0; i < 2000; i++)
            host.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Vocals, result.Kind);
        Assert.Single(host.VocalTracks);
        var track = host.VocalTracks[0];
        Assert.NotEmpty(track.Phrases);
        // The first phrase should aggregate at least one lyric event.
        Assert.NotEmpty(track.Phrases[0].Lyrics);
        // With external beats provided, at least one lyric lands beyond beat 0.
        Assert.Contains(track.Phrases[0].Lyrics, e => e.Position.BeatId > 0);
    }

    [Fact]
    public void ImportInto_ShowlightsXml_AppendsShowlights()
    {
        var path = FindFirstFile("auto_showlights.xml", excludeVocals: false)
                   ?? FindFirstFile("*showlights*.xml", excludeVocals: false);
        if (path == null)
            Assert.Skip("No RS-XML showlights fixture: TestData/EOF/**/*showlights*.xml is absent (TestData/ is git-ignored).");

        var host = new ChartProject();
        for (var i = 0; i < 2000; i++)
            host.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });
        var existingCount = host.Showlights.Count;

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Showlights, result.Kind);
        Assert.True(host.Showlights.Count > existingCount);
        Assert.Equal(host.Showlights.Count - existingCount, result.AddedShowlights);
    }

    /// <summary>
    ///     A NON-ABRIDGED <c>&lt;chordTemplate&gt;</c> — what DLC Builder writes
    ///     whenever <c>UseAbridgedXml</c> is false — spells an unused string as
    ///     <c>fret{i}="-1"</c> rather than omitting the attribute. That is a
    ///     sentinel, not a fret: stored verbatim it makes <c>Frets.Count</c> 6, so
    ///     the E05 rule's "declares nothing" guard can never fire,
    ///     <c>GetLowestFret()</c> returns −1 into the anchor and fingering paths,
    ///     and <c>ChordTemplateResolver.Resolve</c>'s frets-keyed find-or-create
    ///     can never match — so every edit mints a duplicate template.
    ///     <para>
    ///         The EOF corpus omits the attributes entirely, which is exactly why
    ///         no fixture-driven test in this file has ever seen the case.
    ///     </para>
    /// </summary>
    [Fact]
    public void ImportInto_NonAbridgedChordTemplate_DropsTheUnusedStringSentinels()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <song>
              <title>Non-abridged</title>
              <arrangement>Lead</arrangement>
              <ebeats count="2">
                <ebeat time="0.000" measure="1" />
                <ebeat time="0.500" measure="-1" />
              </ebeats>
              <chordTemplates count="1">
                <chordTemplate chordName="A5" displayName="A5"
                               finger0="-1" finger1="1" finger2="3"
                               finger3="-1" finger4="-1" finger5="-1"
                               fret0="-1" fret1="0" fret2="2"
                               fret3="-1" fret4="-1" fret5="-1" />
              </chordTemplates>
            </song>
            """;

        var tempPath = Path.Combine(Path.GetTempPath(), $"rsxml_nonabridged_{Guid.NewGuid():N}.xml");
        File.WriteAllText(tempPath, xml);
        try
        {
            var host = new ChartProject();
            var result = new RsXmlImporter().ImportInto(host, tempPath);

            Assert.Equal(RsXmlImportKind.Song, result.Kind);
            var template = Assert.Single(host.Arrangements[^1].ChordTemplates);

            Assert.Equal(new Dictionary<int, int> { [1] = 0, [2] = 2 }, template.Frets);
            // −1 would have been the answer while the sentinel was stored, and it
            // is what fed the anchor/fingering paths and the E05 message.
            Assert.Equal(0, template.GetLowestFret());
            // The fingering half of the same element has always filtered its own
            // sentinel; assert the two stay symmetric.
            Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3 }, template.Fingers);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ImportInto_UnknownRoot_Throws()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rsxml_unknown_{Guid.NewGuid():N}.xml");
        File.WriteAllText(tempPath, "<bogus />");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                new RsXmlImporter().ImportInto(new ChartProject(), tempPath));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string? FindFirstFile(string pattern, bool excludeVocals)
    {
        var root = FindTestDataRoot();
        if (root == null) return null;
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return null;

        foreach (var file in Directory.EnumerateFiles(eof, pattern, SearchOption.AllDirectories))
        {
            if (file.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
            if (excludeVocals && file.Contains("VOCAL", StringComparison.OrdinalIgnoreCase)) continue;
            return file;
        }
        return null;
    }

    private static string? FindFirstVocalFile()
    {
        var root = FindTestDataRoot();
        if (root == null) return null;
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return null;

        foreach (var file in Directory.EnumerateFiles(eof, "*VOCAL*.xml", SearchOption.AllDirectories))
        {
            if (file.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
            // Confirm root is <vocals>
            try
            {
                var first = File.ReadLines(file).Take(20).FirstOrDefault(l => l.Contains("<vocals"));
                if (first != null) return file;
            }
            catch { /* skip */ }
        }
        return null;
    }

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
