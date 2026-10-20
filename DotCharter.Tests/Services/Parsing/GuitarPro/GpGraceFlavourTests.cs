using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro distinguishes BEFORE-beat from ON-beat grace ornaments and the
///     difference is audible: a before-beat grace steals time ahead of the beat and
///     leaves the target where it was authored, while an on-beat grace sits ON the
///     beat and pushes the target late (what EOF and Log Charter both emit).
///     <para>
///     The GPIF (.gp) importer has always honoured the flavour. The LEGACY binary
///     reader read GP5's grace flag byte and threw it away, then materialized every
///     ornament before the beat — so one score exported as <c>.gp</c> and as
///     <c>.gp5</c> imported with its ornaments in different places, and a
///     before-beat grace with no room ahead of it silently lost its slide. Log
///     Charter's <c>GP5FileReader.readGrace()</c> is the reference for the byte:
///     it reads the flag byte only for <c>version &gt;= 500</c> and spells the
///     meaning <c>graceBefore = (flags &amp; 0x02) == 0</c>, so bit <c>0x02</c> set
///     means ON-beat and BEFORE-beat is both the cleared-bit meaning and the
///     pre-GP5 default for GP3/GP4, which carry no such byte at all.
///     </para>
/// </summary>
public sealed class GpGraceFlavourTests
{
    // ---- binary (.gp3/.gp4/.gp5) fixtures ---------------------------------

    private static ChartLevel ImportLevel(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(Assert.Single(result.Arrangements).Levels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     One bar whose SECOND quarter carries the ornament. The first quarter is
    ///     deliberately on a DIFFERENT string: the before-beat placement rule only
    ///     reaches back as far as the end of the previous sound on the ornament's
    ///     own string, so this leaves genuine room ahead of the beat and the two
    ///     flavours produce visibly different output. (With the previous note on the
    ///     same string there is no room and both flavours legitimately collapse onto
    ///     the beat — that fallback is covered by the chord test in
    ///     <c>GpLegacySlideAndFeelTests</c>.)
    /// </summary>
    private static FxMeasure GracedBar(FxNote graced) =>
        new FxMeasure().AddVoice(
            FxBeat.Note(0, 1, 3),
            FxBeat.Chord(0, graced));

    /// <summary>The same bar with no ornament at all — the control that says where
    /// the target sits when nothing moves it.</summary>
    private static FxMeasure PlainBar() =>
        new FxMeasure().AddVoice(
            FxBeat.Note(0, 1, 3),
            FxBeat.Chord(0, new FxNote(0, 7)));

    /// <summary>The authored (unmoved) position of the graced beat, taken from the
    /// ornament-free control import so the assertions never hard-code the
    /// importer's internal rhythm arithmetic.</summary>
    private static FractionalPosition ControlTargetPosition(string version)
    {
        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile(version, new[] { PlainBar() }));
        return Assert.Single(level.Notes, n => n.Fret == 7).Position;
    }

    // ---- the flavours, on the binary path ---------------------------------

    /// <summary>
    ///     A BEFORE-beat "sl." grace (GP5 flag bit 0x02 CLEARED) is squeezed in
    ///     ahead of the beat: the ornament starts early and the target note stays
    ///     exactly where the score put it.
    /// </summary>
    [Fact]
    public void BeforeBeatGrace_PlacesTheOrnamentAheadOfTheBeat()
    {
        var authored = ControlTargetPosition("5.10");
        var bar = GracedBar(new FxNote(0, 7, GraceFret: 5, GraceSlide: true, GraceOnBeat: false));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { bar }));

        var origin = Assert.Single(level.Notes, n => n.Fret == 5);
        var target = Assert.Single(level.Notes, n => n.Fret == 7);

        Assert.Equal(7, origin.SlideTo);
        Assert.True(origin.LinkNext);
        Assert.True(origin.Position.CompareTo(authored) < 0, "the ornament must start before the beat");
        Assert.Equal(authored, target.Position); // the target did NOT move
        Assert.Null(target.GraceFret);           // the hint was consumed
    }

    /// <summary>
    ///     An ON-beat "sl." grace (GP5 flag bit 0x02 SET) takes the front of the
    ///     beat and pushes the target late. This is the case the binary reader used
    ///     to get wrong on every file: the bit was discarded, so the ornament was
    ///     placed ahead of the beat instead.
    /// </summary>
    [Fact]
    public void OnBeatGrace_SitsOnTheBeatAndPushesTheTarget()
    {
        var authored = ControlTargetPosition("5.10");
        var bar = GracedBar(new FxNote(0, 7, GraceFret: 5, GraceSlide: true, GraceOnBeat: true));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { bar }));

        var origin = Assert.Single(level.Notes, n => n.Fret == 5);
        var target = Assert.Single(level.Notes, n => n.Fret == 7);

        Assert.Equal(7, origin.SlideTo);
        Assert.True(origin.LinkNext);
        Assert.Equal(authored, origin.Position); // the ornament sits ON the beat...
        Assert.True(target.Position.CompareTo(authored) > 0, "the target must be pushed late");
    }

    /// <summary>
    ///     GP3 and GP4 store no grace flag byte — Log Charter reads it only inside
    ///     its <c>version &gt;= 500</c> branch and initializes
    ///     <c>graceBefore = true</c> — so every pre-GP5 grace is BEFORE-beat by
    ///     format definition, not by our choice. The fixture writer omits the byte
    ///     for those versions exactly as the reader does, so asking for an on-beat
    ///     ornament in a .gp4 is a no-op rather than a byte-stream desync.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gp4Grace_HasNoFlavourByteAndIsAlwaysBeforeBeat(bool requestOnBeat)
    {
        var authored = ControlTargetPosition("4.06");
        var bar = GracedBar(new FxNote(0, 7, GraceFret: 5, GraceSlide: true, GraceOnBeat: requestOnBeat));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("4.06", new[] { bar }));

        var origin = Assert.Single(level.Notes, n => n.Fret == 5);
        var target = Assert.Single(level.Notes, n => n.Fret == 7);

        Assert.True(origin.Position.CompareTo(authored) < 0);
        Assert.Equal(authored, target.Position);
    }

    /// <summary>
    ///     The flavour reaches the CHORD ornament path too
    ///     (<c>GpLegacyImporter.ApplyChordGraces</c>, which materializes a whole
    ///     origin chord at beat flush rather than in the post-processor): an on-beat
    ///     strum ornament pushes the strum, a before-beat one does not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChordGrace_HonoursTheAuthoredFlavour(bool onBeat)
    {
        // Strings 2/3 carry the first quarter, leaving strings 0/1 (the graced
        // ones) free so a before-beat ornament genuinely has room.
        FxMeasure Bar(bool graced)
        {
            int? graceFret = graced ? 5 : null;
            return new FxMeasure().AddVoice(
                FxBeat.Chord(0, new FxNote(2, 3), new FxNote(3, 3)),
                FxBeat.Chord(0,
                    new FxNote(0, 7, GraceFret: graceFret, GraceSlide: graced, GraceOnBeat: onBeat),
                    new FxNote(1, 7, GraceFret: graceFret, GraceSlide: graced, GraceOnBeat: onBeat)));
        }

        var control = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { Bar(graced: false) }));
        var authored = control.Chords.OrderBy(c => c.Position).Last().Position;

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { Bar(graced: true) }));
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(3, chords.Count); // the first quarter, the ornament, the strum

        var origin = chords[1];
        var target = chords[2];
        Assert.All(origin.ChordNotes, cn => Assert.Equal(5, cn.Fret));
        Assert.All(origin.ChordNotes, cn => Assert.Equal(7, cn.SlideTo));

        if (onBeat)
        {
            Assert.Equal(authored, origin.Position);
            Assert.True(target.Position.CompareTo(authored) > 0, "an on-beat ornament pushes the strum");
        }
        else
        {
            Assert.True(origin.Position.CompareTo(authored) < 0, "a before-beat ornament precedes the strum");
            Assert.Equal(authored, target.Position);
        }
    }

    // ---- the no-grace guarantee -------------------------------------------

    /// <summary>
    ///     A chart with no grace ornaments must be completely indifferent to the
    ///     flavour carrier — the parameter is only ever read behind a grace hint, so
    ///     adding it cannot change a single byte of output for the overwhelming
    ///     majority of files. Proved the hard way: the same level is run through the
    ///     materializer with NO carrier (the pre-0.33.2 code path, since a null set
    ///     means before-beat everywhere) and then with a carrier claiming EVERY note
    ///     is an on-beat grace. Both leave the snapshot identical.
    /// </summary>
    [Fact]
    public void AGraceFreeChart_IsUnchangedByTheFlavourCarrier()
    {
        var bar = new FxMeasure().AddVoice(
            FxBeat.Note(0, 1, 3),
            FxBeat.Chord(0, new FxNote(0, 7)),
            FxBeat.Chord(0, new FxNote(2, 5), new FxNote(3, 5)),
            FxBeat.Note(0, 0, 9));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { bar }));
        var expected = Snapshot(level);
        Assert.False(expected.Length == 0, "the fixture must actually produce content");

        GpPostProcessor.MaterializeGraceSlides(level);
        Assert.Equal(expected, Snapshot(level));

        GpPostProcessor.MaterializeGraceSlides(level, new HashSet<GuitarNote>(level.Notes));
        Assert.Equal(expected, Snapshot(level));
    }

    /// <summary>Every playable field a grace ornament could possibly disturb,
    /// rendered in a stable order.</summary>
    private static string Snapshot(ChartLevel level)
    {
        var sb = new StringBuilder();
        foreach (var n in level.Notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex))
        {
            sb.Append($"N {n.Position} s{n.StringIndex} f{n.Fret} d{n.Sustain} " +
                      $"sl{n.SlideTo} ln{n.LinkNext} g{n.GraceFret}\n");
        }

        foreach (var c in level.Chords.OrderBy(c => c.Position))
        {
            foreach (var cn in c.ChordNotes.OrderBy(cn => cn.StringIndex))
            {
                sb.Append($"C {c.Position} s{cn.StringIndex} f{cn.Fret} d{cn.Sustain} " +
                      $"sl{cn.SlideTo} ln{cn.LinkNext} g{cn.GraceFret}\n");
            }
        }

        return sb.ToString();
    }

    // ---- the GPIF path must not move --------------------------------------

    private static ChartLevel ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpflavour-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
        {
            writer.Write(gpif);
        }

        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(arrangements).Levels[0];
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A one-bar gpif score; <paramref name="graceBeat"/> is spliced in
    /// between the first quarter and the graced quarter (empty ⇒ the control).</summary>
    private static string GpifScore(string graceBeat)
    {
        var hasGrace = graceBeat.Length > 0;
        var voiceBeats = hasGrace ? "0 1 2" : "0 2";
        return $"""
            <GPIF>
              <Score><Title>Grace Flavour Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Guitar</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                    <Property name="CapoFret"><Fret>0</Fret></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>{voiceBeats}</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                {graceBeat}
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties>
                  <Property name="String"><String>2</String></Property>
                  <Property name="Fret"><Fret>9</Fret></Property>
                </Properties></Note>
                <Note id="1"><Properties>
                  <Property name="String"><String>1</String></Property>
                  <Property name="Fret"><Fret>3</Fret></Property>
                  <Property name="Slide"><Flags>2</Flags></Property>
                </Properties></Note>
                <Note id="2"><Properties>
                  <Property name="String"><String>1</String></Property>
                  <Property name="Fret"><Fret>5</Fret></Property>
                </Properties></Note>
              </Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }

    /// <summary>
    ///     Pins the GPIF path's grace placement, which must NOT move: it is the
    ///     importer measured note-for-note against Editor-on-Fire and Log Charter,
    ///     so it is the reference the binary reader was just brought into line with,
    ///     not the other way round. Both flavours are asserted, because the binary
    ///     fix threads a new optional argument through the shared
    ///     <c>GpPostProcessor.Run</c> that this path leaves unset.
    /// </summary>
    [Theory]
    [InlineData("BeforeBeat")]
    [InlineData("OnBeat")]
    public void GpifGracePlacement_IsUnaffected(string flavour)
    {
        var authored = Assert.Single(ImportGpif(GpifScore("")).Notes, n => n.Fret == 5).Position;

        var graceBeat = $"""
            <Beat id="1"><GraceNotes>{flavour}</GraceNotes><Rhythm ref="1" /><Notes>1</Notes></Beat>
            """;
        var level = ImportGpif(GpifScore(graceBeat));

        var origin = Assert.Single(level.Notes, n => n.Fret == 3);
        var target = Assert.Single(level.Notes, n => n.Fret == 5);
        Assert.Equal(5, origin.SlideTo);

        if (flavour == "OnBeat")
        {
            Assert.Equal(authored, origin.Position);
            Assert.True(target.Position.CompareTo(authored) > 0);
        }
        else
        {
            Assert.True(origin.Position.CompareTo(authored) < 0);
            Assert.Equal(authored, target.Position);
        }
    }
}
