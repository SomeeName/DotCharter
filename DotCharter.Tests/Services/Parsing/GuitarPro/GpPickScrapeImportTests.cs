using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro's PICK SCRAPE imports the way the owner charts it (issues #583,
///     #599): <b>tap + tremolo + fret-hand mute + an unpitched slide in the
///     scrape's direction + ignore</b>; a scrape on several strings is ONE chord
///     split into notes (<see cref="Chord.SplitIntoNotes" />, ignored as a whole so
///     Live Play never expects it) that every export writes as one
///     <c>&lt;note&gt;</c> per string, and a one-string scrape is a single note.
///     The start fret is 24 or 12 (#586),
///     the one nearer the FHP's 4th lane toward the nut or its 1st lane toward the
///     bridge, on the track's own neck; the landing travels an octave, or six
///     frets for a scrape shorter than a quarter note, and stops at fret 5 toward
///     the nut. Both tab formats
///     carry it in the slide bitmask — gpif <c>Slide</c> flags 64 / 128 and the
///     GP5 slide byte's 0x40 / 0x80. Fixtures are built in code
///     (<see cref="PickScrapeScenes" />); nothing here reads a file outside the
///     repository.
/// </summary>
public sealed class GpPickScrapeImportTests
{
    private const int ScrapeDown = GpPickScrape.DownFlag;
    private const int ScrapeUp = GpPickScrape.UpFlag;

    // ── the mapping itself ─────────────────────────────────────────────────

    /// <summary>
    ///     The fret rule against a known hand position. The scrape starts on 24
    ///     or 12, whichever is nearer the anchor's 4th lane toward the nut or its
    ///     1st toward the bridge (a tie goes to 24); a start needs room to travel,
    ///     so toward the bridge it is never the last fret. The landing is an octave
    ///     away (six frets when short), toward the nut never below fret 5, and
    ///     every fret stays between the capo and the last fret.
    /// </summary>
    [Theory]
    [InlineData(true, 21, true, 0, 24, 24, 18)]    // Shinbatsu m197: FHP 21, eighth notes
    [InlineData(true, 21, false, 0, 24, 24, 12)]   // Shinbatsu m198 / Matasaburo m160
    [InlineData(true, 12, false, 0, 24, 12, 5)]    // Crucifix X m102 / Symbol I m75: lane 15 is nearer 12
    [InlineData(true, 12, true, 0, 24, 12, 6)]
    [InlineData(true, 5, false, 0, 24, 12, 5)]     // FHP 5: lane 8 (the old start) snaps to 12
    [InlineData(true, 14, false, 0, 24, 12, 5)]    // lane 17 is nearer 12
    [InlineData(true, 15, false, 0, 24, 24, 12)]   // lane 18 is a tie: 24
    [InlineData(true, 19, true, 0, 24, 24, 18)]    // FHP 19: lane 22 snaps to 24
    [InlineData(true, 1, false, 0, 24, 12, 5)]
    [InlineData(true, 21, false, 0, 22, 12, 5)]    // a 22-fret neck has no 24th: 12
    [InlineData(true, 7, false, 7, 24, 12, 7)]     // capo 7: lands on the capo, never below
    [InlineData(true, 12, false, 12, 24, 24, 12)]  // capo 12: 12 leaves no room below it
    [InlineData(false, 5, false, 0, 24, 12, 24)]   // toward the bridge: FHP 5 (the old start) snaps to 12
    [InlineData(false, 5, true, 0, 24, 12, 18)]
    [InlineData(false, 19, false, 0, 24, 12, 24)]  // 24 leaves no room toward the bridge
    [InlineData(false, 15, true, 0, 24, 12, 18)]
    [InlineData(false, 12, false, 0, 22, 12, 22)]  // a 22-fret neck: the landing stops at 22
    [InlineData(false, 0, false, 9, 24, 12, 24)]   // capo 9
    [InlineData(false, 15, false, 13, 24, 15, 24)] // capo 13: neither fits, the 1st lane itself
    public void Place_ChartsTheConvention(bool towardNut, int anchor, bool shortScrape, int capo, int lastFret,
        int expectedStart, int expectedTarget)
    {
        var note = new GuitarNote();

        GpPickScrape.Place(note, anchor, towardNut, shortScrape, capo, lastFret);

        Assert.Equal((expectedStart, expectedTarget), (note.Fret, note.SlideTo!.Value));
        Assert.True(note.UnpitchedSlide);
    }

    /// <summary>The track's fret count bounds the start; a missing or implausible
    /// one reads as 24.</summary>
    [Theory]
    [InlineData(null, 24)]
    [InlineData(24, 24)]
    [InlineData(22, 22)]
    [InlineData(21, 21)]
    [InlineData(27, 24)]
    [InlineData(0, 24)]
    [InlineData(5, 24)]
    public void LastFret_ReadsTheTracksFretCount(int? fretCount, int expected)
        => Assert.Equal(expected, GpPickScrape.LastFret(fretCount));

    /// <summary>The parse marks the techniques and drops any mute: a dead or
    /// palm-muted tab note scrapes unmuted. Both bits read as a scrape down.</summary>
    [Theory]
    [InlineData(ScrapeDown, MuteType.None, true)]
    [InlineData(ScrapeUp, MuteType.Palm, false)]
    [InlineData(ScrapeDown | ScrapeUp, MuteType.Full, true)]
    public void Apply_MarksTapTremoloIgnore_FretHandMuted(int flags, MuteType tabMute, bool towardNut)
    {
        var note = new GuitarNote { Fret = 12, Mute = tabMute, LinkNext = true };
        var chordNote = new ChordNote { Fret = 12, Mute = tabMute, LinkNext = true };

        GpPickScrape.Apply(note, flags, capo: 0);
        GpPickScrape.Apply(chordNote, flags, capo: 0);

        foreach (var s in new IGuitarSound[] { note, chordNote })
        {
            Assert.Equal((HopoType.Tap, true, true, MuteType.Full, false),
                (s.Hopo, s.Tremolo, s.UnpitchedSlide, s.Mute, s.LinkNext));
            Assert.Equal(towardNut, s.SlideTo < s.Fret);
            Assert.True(GpPickScrape.IsScrape(s));
        }

        Assert.True(note.Ignore);
        Assert.True(chordNote.Ignore);
    }

    /// <summary>GP4's slide is ONE discrete value, not a mask: 64 means nothing
    /// there and must leave the note alone.</summary>
    [Fact]
    public void Gp4SlideValue64_IsNotAPickScrape()
    {
        var note = new GuitarNote { Fret = 12 };

        var scraped = GpLegacyNoteEffectsHelpers.ApplySlide(ScrapeDown, note, isVersion5: false);

        Assert.False(scraped);
        Assert.Null(note.SlideTo);
        Assert.False(note.UnpitchedSlide);
    }

    /// <summary>
    ///     A scrape is a sound of its own: a multi-string scrape is ONE chord split
    ///     into notes and ignored as a whole; a one-string scrape is a note; strings
    ///     of the same strum that do not scrape stay a chord, or a note when one is
    ///     left. Every scrape string ends fret-hand muted, whatever the tab said.
    /// </summary>
    [Fact]
    public void IsolateScrapes_GivesEveryScrapeASoundOfItsOwn()
    {
        var level = new ChartLevel();
        var both = ChordAt(1, Scrape(1), Scrape(2));
        var power = ChordAt(2, Scrape(0), new ChordNote { StringIndex = 1, Fret = 7 },
            new ChordNote { StringIndex = 2, Fret = 9 });
        var pair = ChordAt(3, Scrape(0), new ChordNote { StringIndex = 1, Fret = 7 });
        var mixed = ChordAt(4, Scrape(0), Scrape(1), new ChordNote { StringIndex = 2, Fret = 9 });
        both.ChordNotes[1].Mute = MuteType.Palm;
        foreach (var chord in new[] { both, power, pair, mixed }) level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), Fret = 5 });

        GpPickScrape.IsolateScrapes(level);

        // Beat 1 stays the same chord, now split and ignored; beat 4's two scrape
        // strings leave the strum as a split chord of their own.
        Assert.Same(both, level.Chords[0]);
        Assert.Equal(new[] { (1, true), (2, false), (4, true) },
            level.Chords.Select(c => (c.Position.BeatId, c.SplitIntoNotes)).ToArray());
        Assert.All(level.Chords.Where(c => c.SplitIntoNotes), c =>
        {
            Assert.True(c.Ignore);
            Assert.True(GpPickScrape.IsScrapeChord(c));
            Assert.All(c.ChordNotes, cn => Assert.Equal(MuteType.Full, cn.Mute));
        });
        Assert.Equal(new[] { 1, 2 }, level.Chords[1].ChordNotes.Select(n => n.StringIndex).ToArray());
        Assert.Equal(new[] { 0, 1 }, level.Chords[2].ChordNotes.Select(n => n.StringIndex).ToArray());
        Assert.False(level.Chords[1].Ignore);

        // Beat 2's lone scrape string, both strings of beat 3 and beat 4's
        // remaining 9 are notes; only the scrapes are ignored.
        Assert.Equal(new[] { (2, 0, true), (3, 0, true), (3, 1, false), (4, 2, false), (5, 0, false) },
            level.Notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex)
                .Select(n => (n.Position.BeatId, n.StringIndex, n.Ignore)).ToArray());
        Assert.All(level.Notes.Where(GpPickScrape.IsScrape), n => Assert.Equal(MuteType.Full, n.Mute));

        static Chord ChordAt(int beat, params ChordNote[] notes)
        {
            var chord = new Chord { Position = new FractionalPosition(beat) };
            foreach (var n in notes) chord.ChordNotes.Add(n);
            return chord;
        }

        static ChordNote Scrape(int str)
        {
            var cn = new ChordNote { StringIndex = str, Fret = 12 };
            GpPickScrape.Apply(cn, ScrapeDown, 0);
            return cn;
        }
    }

    // ── gpif (.gp) end to end ─────────────────────────────────────────────

    /// <summary>The Shinbatsu m197 pattern: FHP 21, three A+D eighth-note scrapes
    /// toward the nut 24 → 18, then an E+A half-note scrape 24 → 12.</summary>
    [Fact]
    public void Gpif_ShinbatsuScene_ScrapesFromThe4thLaneOfFhp21()
        => AssertShinbatsu(PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu())));

    /// <summary>Toward the bridge off FHP 5 the scrape starts on 12 — the old
    /// 1st-lane rule started it on 5.</summary>
    [Fact]
    public void Gpif_ScrapeTowardTheBridge_StartsOn12()
    {
        var level = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.TowardBridge())).Levels[0];

        Assert.Equal(5, AnchorAt(level, new FractionalPosition(4)));
        var quarter = Assert.Single(level.Notes, n => n.Position == new FractionalPosition(4));
        var eighth = Assert.Single(level.Notes, n => n.Position == new FractionalPosition(5));
        Assert.Equal((0, 12, 24), (quarter.StringIndex, quarter.Fret, quarter.SlideTo!.Value));
        Assert.Equal((0, 12, 18), (eighth.StringIndex, eighth.Fret, eighth.SlideTo!.Value));
        Assert.All(new[] { quarter, eighth }, AssertScrapeTechniques);
    }

    /// <summary>
    ///     On a 22-fret track (gpif <c>FretCount</c>) the Shinbatsu scrapes off
    ///     FHP 21 start on 12, not on the 24th fret the neck does not have.
    /// </summary>
    [Fact]
    public void Gpif_22FretTrack_ScrapesStartOn12()
        => Assert22Fret(PickScrapeScenes.Import(PickScrapeScenes.GpifWithFrets(22, PickScrapeScenes.Shinbatsu())));

    /// <summary>
    ///     The scrapes are lifted out while the FHP is generated: the tab wrote
    ///     them at fret 12 / open, which is not where the hand is, and the
    ///     anchors must be exactly those of the same score without them.
    /// </summary>
    [Fact]
    public void Gpif_ScrapesDoNotMoveTheGeneratedFhp()
    {
        var with = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu())).Levels[0];
        var without = PickScrapeScenes.Import(PickScrapeScenes.Gpif(PickScrapeScenes.Shinbatsu(withScrapes: false)))
            .Levels[0];

        Assert.Equal(without.Anchors.Select(a => (a.Position, a.Fret, a.Width)),
            with.Anchors.Select(a => (a.Position, a.Fret, a.Width)));
    }

    // ── GP5 binary end to end ─────────────────────────────────────────────

    private static Arrangement ImportLegacy(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The same Shinbatsu scene as a GP5 file: the binary reader builds
    /// the A+D strum as a chord first, so it must carry the ignore across.</summary>
    [Fact]
    public void Gp5_ShinbatsuScene_ScrapesFromThe4thLaneOfFhp21()
        => AssertShinbatsu(ImportLegacy(LegacyGpFixtureBuilder.WriteFile("5.10", Gp5Shinbatsu())));

    /// <summary>The GP5 track header's fret count bounds the start the same way.</summary>
    [Fact]
    public void Gp5_22FretTrack_ScrapesStartOn12()
        => Assert22Fret(ImportLegacy(LegacyGpFixtureBuilder.WriteFile("5.10", Gp5Shinbatsu(), frets: 22)));

    private static FxMeasure[] Gp5Shinbatsu()
    {
        static FxBeat AdScrape() => FxBeat.Chord(1,
            new FxNote(1, 12, Slide: ScrapeDown, Dead: true),
            new FxNote(2, 12, Slide: ScrapeDown, Dead: true));

        var lick = new FxMeasure().AddVoice(FxBeat.Note(0, 3, 21), FxBeat.Note(0, 3, 23), FxBeat.Note(0, 2, 22),
            FxBeat.Note(0, 3, 24));
        var scrapes = new FxMeasure().AddVoice(AdScrape(), FxBeat.RestBeat(1), AdScrape(), FxBeat.RestBeat(1),
            AdScrape(), FxBeat.RestBeat(1), FxBeat.RestBeat(0));
        var longScrape = new FxMeasure().AddVoice(
            FxBeat.Chord(-1,
                new FxNote(0, 0, Slide: ScrapeDown, Dead: true),
                new FxNote(1, 0, Slide: ScrapeDown, PalmMute: true)),
            FxBeat.RestBeat(-1));
        return [lick, scrapes, longScrape];
    }

    private static void Assert22Fret(Arrangement arrangement)
    {
        var level = Assert.Single(arrangement.Levels);
        Assert.Equal(21, AnchorAt(level, new FractionalPosition(4)));
        Assert.Equal(4, level.Chords.Count);
        foreach (var chord in level.Chords)
        {
            var far = chord.Position.BeatId == 8;
            int[] strings = far ? [0, 1] : [1, 2];
            var landing = far ? 5 : 6;
            AssertScrapeChord(arrangement, chord, strings, 12, landing);
        }
    }

    private static void AssertShinbatsu(Arrangement arrangement)
    {
        var level = Assert.Single(arrangement.Levels);
        Assert.Empty(level.HandShapes);
        Assert.Equal(21, AnchorAt(level, new FractionalPosition(4)));

        // Every sound at a scrape's time is its split chord: no loose string is
        // left for Live Play's detection to expect (#586), and only the lick's
        // four notes are notes.
        Assert.Equal(new[] { 0, 1, 2, 3 }, level.Notes.Select(n => n.Position.BeatId).ToArray());
        Assert.Equal(new[] { 4, 5, 6, 8 }, level.Chords.Select(c => c.Position.BeatId).ToArray());
        foreach (var chord in level.Chords)
        {
            var far = chord.Position.BeatId == 8;
            int[] strings = far ? [0, 1] : [1, 2];
            var landing = far ? 12 : 18;
            AssertScrapeChord(arrangement, chord, strings, 24, landing);
        }
    }

    /// <summary>One split, ignored scrape chord on <paramref name="strings" />, every
    /// note a scrape from <paramref name="fret" /> to <paramref name="landing" />,
    /// playing a FINGERLESS template of exactly its frets.</summary>
    private static void AssertScrapeChord(Arrangement arrangement, Chord chord, int[] strings, int fret, int landing)
    {
        Assert.True(chord.SplitIntoNotes, $"the scrape at {chord.Position} is not split into notes");
        Assert.True(chord.Ignore, $"the scrape at {chord.Position} is not ignored");
        Assert.Equal(strings.Select(s => (s, fret, landing)).ToArray(),
            chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => (n.StringIndex, n.Fret, n.SlideTo!.Value))
                .ToArray());
        Assert.All(chord.ChordNotes, AssertScrapeTechniques);
        var template = Assert.Single(arrangement.ChordTemplates, t => t.Id == chord.TemplateId);
        Assert.True(template.DescribesChordNotes(chord.ChordNotes));
        Assert.Empty(template.Fingers);
    }

    private static void AssertScrapeTechniques(IGuitarSound n)
    {
        var ignored = n switch
        {
            GuitarNote note => note.Ignore,
            ChordNote chordNote => chordNote.Ignore,
            _ => false
        };
        Assert.Equal((HopoType.Tap, true, true, true, MuteType.Full, false),
            (n.Hopo, n.Tremolo, n.UnpitchedSlide, ignored, n.Mute, n.LinkNext));
        Assert.True(n.Sustain.CompareTo(FractionalPosition.Zero) > 0, "the scrape sustains over its duration");
    }

    private static int AnchorAt(ChartLevel level, FractionalPosition position)
        => level.Anchors.Last(a => a.Position.CompareTo(position) <= 0).Fret;

    // ── the owner's reference: RS XML export, .rscp round trip ────────────

    /// <summary>
    ///     The owner's hand-charted <i>Shinbatsu wo Tadaori</i> m197–198 as the RS XML
    ///     export writes it (issue #599, the project's <c>1_Lead_Main_RS2.xml</c>):
    ///     three A+D eighth-note scrapes and one E+A scrape off FHP 21, each string
    ///     its own <c>&lt;note&gt;</c>. Copied verbatim; only these eight lines.
    /// </summary>
    private static readonly string[] ReferenceNotes =
    [
        """<note time="198.204" string="1" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="198.204" string="2" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="198.564" string="1" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="198.564" string="2" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="198.924" string="1" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="198.924" string="2" fret="24" sustain="0.120" tremolo="1" mute="1" tap="1" slideUnpitchTo="18" ignore="1" />""",
        """<note time="199.164" string="0" fret="24" sustain="0.449" tremolo="1" mute="1" tap="1" slideUnpitchTo="12" ignore="1" />""",
        """<note time="199.164" string="1" fret="24" sustain="0.449" tremolo="1" mute="1" tap="1" slideUnpitchTo="12" ignore="1" />"""
    ];

    /// <summary>
    ///     The GP import of the reference-shaped score exports EXACTLY the owner's
    ///     <c>&lt;note&gt;</c> lines — the same attributes and values, nothing else —
    ///     with no <c>&lt;chord&gt;</c> and no hand shape. One documented difference:
    ///     the E+A scrape's sustain. The tab says a half note, 0.480 s at the song's
    ///     250 BPM; the owner trimmed it by hand to 449 ms in EOF (the <c>.eof</c>
    ///     stores that length), which no note value a tab can write produces.
    /// </summary>
    [Fact]
    public void ReferenceScene_ExportsTheOwnersNoteLines_FieldForField()
    {
        var doc = new RsXmlSongWriter().Write(PickScrapeScenes.ReferenceProject());

        Assert.Empty(doc.Descendants("chord"));
        Assert.Empty(doc.Descendants("handShape"));
        var written = doc.Descendants("note").Where(n => n.Attribute("ignore")?.Value == "1")
            .Select(Fields).ToArray();
        var expected = ReferenceNotes.Select(XElement.Parse).Select(Fields)
            .Select(f => f["time"] == "199.164" ? new SortedDictionary<string, string>(f) { ["sustain"] = "0.480" } : f)
            .ToArray();

        Assert.Equal(expected.Length, written.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], written[i]);

        static SortedDictionary<string, string> Fields(XElement note)
            => new(note.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), StringComparer.Ordinal);
    }

    /// <summary>
    ///     A round trip through RS XML keeps every scrape string and technique. Read
    ///     as a DotCharter v2 project's own arrangement (merge off) the scrapes stay
    ///     the eight loose notes the file holds; IMPORTED, each comes back as the one
    ///     split, ignored chord it was (#621, <c>RsXmlSplitChordMerge</c>: notes at one
    ///     time, inside no hand shape, no link landing on them) - and that import
    ///     writes the same file again.
    /// </summary>
    [Fact]
    public void ReferenceScene_RsXmlRoundTrip_KeepsEveryScrapeString()
    {
        var doc = new RsXmlSongWriter().Write(PickScrapeScenes.ReferenceProject());

        var loose = new RsXmlSongReader().ReadDocument(new XDocument(doc), "scrape.xml");
        var looseLevel = Assert.Single(Assert.Single(loose.Arrangements).Levels);
        Assert.Empty(looseLevel.Chords);
        var read = looseLevel.Notes.Where(n => n.Ignore).ToList();
        Assert.Equal(8, read.Count);
        Assert.All(read, AssertScrapeTechniques);

        var imported = new RsXmlSongReader { MergeSimultaneousNotes = true }.ReadDocument(new XDocument(doc), "scrape.xml");
        var arrangement = Assert.Single(imported.Arrangements);
        var level = Assert.Single(arrangement.Levels);
        Assert.DoesNotContain(level.Notes, n => n.Ignore);
        Assert.Equal(new[] { 4, 5, 7, 8 }, level.Chords.Select(c => c.Position.BeatId).ToArray());
        foreach (var chord in level.Chords)
        {
            var far = chord.Position.BeatId == 8;
            int[] strings = far ? [0, 1] : [1, 2];
            AssertScrapeChord(arrangement, chord, strings, 24, far ? 12 : 18);
        }

        Assert.Equal(Unclocked(doc), Unclocked(new RsXmlSongWriter().Write(imported)));

        static string Unclocked(XDocument written)
        {
            var copy = new XDocument(written);
            copy.Root!.Element("lastConversionDateTime")?.Remove();
            return copy.ToString();
        }
    }

    /// <summary>The <c>.rscp</c> — the project — keeps a scrape a split, ignored
    /// chord with every technique on every string.</summary>
    [Fact]
    public void ReferenceScene_RscpRoundTrip_KeepsTheSplitScrapeChords()
    {
        var dir = Directory.CreateTempSubdirectory("dotcharter_scrape_rscp_").FullName;
        try
        {
            var path = Path.Combine(dir, "scrape.rscp");
            new RscpChartSerializer().Save(path, PickScrapeScenes.ReferenceProject());
            var loaded = new RscpChartSerializer().Load(path);

            var arrangement = Assert.Single(loaded.Arrangements);
            var level = Assert.Single(arrangement.Levels);
            Assert.Equal(new[] { 4, 5, 7, 8 }, level.Chords.Select(c => c.Position.BeatId).ToArray());
            Assert.Empty(level.HandShapes);
            foreach (var chord in level.Chords)
            {
                var far = chord.Position.BeatId == 8;
                int[] strings = far ? [0, 1] : [1, 2];
                var landing = far ? 12 : 18;
                AssertScrapeChord(arrangement, chord, strings, 24, landing);
            }
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }
}

/// <summary>
///     Pick-scrape scores built in code, shared by the import tests and the GPU
///     captures (<c>HeadlessPickScrapeRenderTests</c>). The main one is shaped
///     like the owner's <i>Shinbatsu wo Tadaori</i> measure 197: a lick at frets
///     21–24 (FHP 21), three dead A+D eighth notes scraped DOWN, then a dead E+A
///     half note scraped DOWN. The tab writes the scrapes at fret 12 / open — a
///     tab's scrape fret is not a hand position.
/// </summary>
internal static class PickScrapeScenes
{
    public static readonly ScrapeNote[] AdScrape =
        [new(1, 12, GpPickScrape.DownFlag), new(2, 12, GpPickScrape.DownFlag)];

    public static readonly ScrapeNote[] EaScrape =
        [new(0, 0, GpPickScrape.DownFlag), new(1, 0, GpPickScrape.DownFlag)];

    /// <summary>Bar 1 the lick, bar 2 the eighth-note scrapes on beats 4, 5, 6,
    /// bar 3 the half-note scrape on beat 8.</summary>
    public static ScrapeBeat[][] Shinbatsu(bool withScrapes = true)
    {
        ScrapeBeat Scrape(string value, ScrapeNote[] notes) => withScrapes ? new(value, notes) : new(value, []);
        return
        [
            [new("Quarter", [new(3, 21)]), new("Quarter", [new(3, 23)]), new("Quarter", [new(2, 22)]),
                new("Quarter", [new(3, 24)])],
            [Scrape("Eighth", AdScrape), new("Eighth", []), Scrape("Eighth", AdScrape), new("Eighth", []),
                Scrape("Eighth", AdScrape), new("Eighth", []), new("Quarter", [])],
            [Scrape("Half", EaScrape), new("Half", [])]
        ];
    }

    /// <summary>
    ///     The owner's reference, <i>Shinbatsu wo Tadaori</i> m197–198 (#599), after
    ///     the same lick: three dead A+D eighth notes scraped DOWN a dotted quarter
    ///     apart (beats 4, 5.5 and 7), then a dead E+A half note scraped DOWN (beat 8).
    /// </summary>
    public static ScrapeBeat[][] Reference() =>
    [
        Shinbatsu()[0],
        [new("Eighth", AdScrape), new("Quarter", []), new("Eighth", AdScrape), new("Quarter", []),
            new("Eighth", AdScrape), new("Eighth", [])],
        [new("Half", EaScrape), new("Half", [])]
    ];

    /// <summary>
    ///     <see cref="Reference" /> imported into a project on the song's own grid:
    ///     250 BPM (a beat is 240 ms) with m197's downbeat at 198.204 s, as in the
    ///     owner's RS XML.
    /// </summary>
    public static ChartProject ReferenceProject()
    {
        var project = new ChartProject();
        project.Arrangements.Add(Import(Gpif(Reference())));
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = 197_244 + i * 240, Bpm = 250, IsMeasure = i % 4 == 0 });
        return project;
    }

    /// <summary>An A5 riff at fret 5 (FHP 5), then a dead low E scraped UP for a
    /// quarter (beat 4) and an eighth (beat 5).</summary>
    public static ScrapeBeat[][] TowardBridge() =>
    [
        [new("Quarter", [new(0, 5), new(1, 7)]), new("Quarter", [new(1, 7)]), new("Quarter", [new(0, 5)]),
            new("Quarter", [new(0, 5), new(1, 7)])],
        [new("Quarter", [new(0, 0, GpPickScrape.UpFlag)]), new("Eighth", [new(0, 0, GpPickScrape.UpFlag)]),
            new("Eighth", []), new("Half", [])]
    ];

    /// <summary>A one-track 4/4 gpif score, one beat list per bar.</summary>
    public static string Gpif(params ScrapeBeat[][] bars) => GpifWithFrets(null, bars);

    /// <summary>
    ///     <see cref="Gpif" /> with the staff's <c>FretCount</c> property set to
    ///     <paramref name="fretCount" /> (left out when null).
    /// </summary>
    public static string GpifWithFrets(int? fretCount, params ScrapeBeat[][] bars)
    {
        var rhythms = new[] { "Eighth", "Quarter", "Half" };
        var masterBars = new StringBuilder();
        var barNodes = new StringBuilder();
        var voices = new StringBuilder();
        var beats = new StringBuilder();
        var notes = new StringBuilder();
        var beatId = 0;
        var noteId = 0;
        for (var b = 0; b < bars.Length; b++)
        {
            masterBars.Append(CultureInfo.InvariantCulture, $"<MasterBar><Time>4/4</Time><Bars>{b}</Bars></MasterBar>");
            barNodes.Append(CultureInfo.InvariantCulture, $"<Bar id=\"{b}\"><Voices>{b} -1 -1 -1</Voices></Bar>");
            var ids = new List<int>();
            foreach (var beat in bars[b])
            {
                var noteIds = new List<int>();
                foreach (var n in beat.Notes)
                {
                    notes.Append(Note(noteId, n));
                    noteIds.Add(noteId++);
                }

                var noteList = noteIds.Count > 0 ? $"<Notes>{string.Join(' ', noteIds)}</Notes>" : "";
                beats.Append(CultureInfo.InvariantCulture,
                    $"<Beat id=\"{beatId}\"><Rhythm ref=\"{Array.IndexOf(rhythms, beat.Value)}\" />{noteList}</Beat>");
                ids.Add(beatId++);
            }

            voices.Append(CultureInfo.InvariantCulture, $"<Voice id=\"{b}\"><Beats>{string.Join(' ', ids)}</Beats></Voice>");
        }

        var frets = fretCount is { } count
            ? string.Create(CultureInfo.InvariantCulture,
                $"""<Property name="FretCount"><Number>{count}</Number></Property>""")
            : "";
        var rhythmNodes = string.Concat(rhythms.Select((r, i) =>
            string.Create(CultureInfo.InvariantCulture, $"<Rhythm id=\"{i}\"><NoteValue>{r}</NoteValue></Rhythm>")));
        return $"""
            <GPIF>
              <Score><Title>Pick Scrape Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Guitar</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                    {frets}
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>{masterBars}</MasterBars>
              <Bars>{barNodes}</Bars>
              <Voices>{voices}</Voices>
              <Beats>{beats}</Beats>
              <Notes>{notes}</Notes>
              <Rhythms>{rhythmNodes}</Rhythms>
            </GPIF>
            """;
    }

    private static string Note(int id, ScrapeNote n)
    {
        var scrape = n.Flags == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"""<Property name="Muted"><Enable /></Property><Property name="Slide"><Flags>{n.Flags}</Flags></Property>""");
        return string.Create(CultureInfo.InvariantCulture, $"""
            <Note id="{id}"><Properties>
              <Property name="String"><String>{n.String}</String></Property>
              <Property name="Fret"><Fret>{n.Fret}</Fret></Property>
              {scrape}
            </Properties></Note>
            """);
    }

    /// <summary>Imports <paramref name="gpif" /> through the real <see cref="GpImporter" />.</summary>
    public static Arrangement Import(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpscrape-{Guid.NewGuid():N}.gp");
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
                writer.Write(gpif);

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>One note of a scene: string (low E = 0), tab fret, slide flags.</summary>
    internal readonly record struct ScrapeNote(int String, int Fret, int Flags = 0);

    /// <summary>One beat: its rhythm (<c>Eighth</c>, <c>Quarter</c>, <c>Half</c>)
    /// and its notes; none is a rest.</summary>
    internal readonly record struct ScrapeBeat(string Value, ScrapeNote[] Notes);
}
