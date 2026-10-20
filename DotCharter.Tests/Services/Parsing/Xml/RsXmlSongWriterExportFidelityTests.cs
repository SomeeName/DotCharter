using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
/// Pins the RS2 XML export defects found by diffing DotCharter's output against
/// EOF's for the same song (TestData/ImportCompare). TestData is gitignored, so
/// every fixture here is built in code.
/// </summary>
public sealed class RsXmlSongWriterExportFidelityTests
{
    private const double MsPerBeat = 500.0; // 120 BPM

    private static ChartProject BuildProject(int beatCount = 16)
    {
        var project = new ChartProject();
        for (var i = 0; i < beatCount; i++)
            project.Beats.Add(new BeatMarker { Time = i * MsPerBeat, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(arr);
        return project;
    }

    private static Arrangement Arr(ChartProject project) => project.Arrangements[0];

    private static XElement Write(ChartProject project, double? audioLengthMs = null)
        => new RsXmlSongWriter().Write(project, audioLengthMs).Root!;

    private static string Value(XElement song, string name) => song.Element(name)!.Value;

    private static ChartLevel AddLevel(ChartProject project)
    {
        var level = new ChartLevel { DifficultyIndex = 0 };
        Arr(project).Levels.Add(level);
        return level;
    }

    /// <summary>A plain 3-string chord with no technique and no sustain.</summary>
    private static Chord PlainChord(int beat, int templateId, params (int StringIndex, int Fret)[] frets)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (stringIndex, fret) in frets)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = stringIndex,
                Fret = fret,
                Sustain = FractionalPosition.Zero
            });
        }

        return chord;
    }

    private static Chord PlainChord(FractionalPosition position, int templateId,
        params (int StringIndex, int Fret)[] frets)
    {
        var chord = PlainChord(0, templateId, frets);
        chord.Position = position;
        return chord;
    }

    private static List<XElement> Chords(XElement song)
        => song.Element("levels")!.Element("level")!.Element("chords")!.Elements("chord").ToList();

    private static List<XElement> Anchors(XElement song)
        => song.Element("levels")!.Element("level")!.Element("anchors")!.Elements("anchor").ToList();

    private static List<XElement> Notes(XElement song)
        => song.Element("levels")!.Element("level")!.Element("notes")!.Elements("note").ToList();

    // ── link landings ────────────────────────────────────────────────────

    /// <summary>
    ///     A chord tied or slid into the next chord is ordinary guitar and stays
    ///     authorable, but RS reads a &lt;chord&gt; as a STRUM and a shape you
    ///     slid into was never re-picked — so the landing is written as loose
    ///     notes. EOF does exactly this, in its own words: "Identify chords that
    ///     follow a note with linkNext status, which will need to be broken into
    ///     single notes so that they display correctly in-game"
    ///     (EOF_SRC/rs.c:2010). Across the 98 hand-corrected exports in
    ///     TestData/EOF, 493 chord notes carry linkNext and not one lands on a
    ///     chord.
    /// </summary>
    [Fact]
    public void AChordLandedOnByALink_IsWrittenAsLooseNotes()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        var source = PlainChord(2, 0, (0, 5), (1, 7));
        foreach (var chordNote in source.ChordNotes)
        {
            chordNote.Sustain = new FractionalPosition(1);
            chordNote.LinkNext = true;
        }

        level.Chords.Add(source);
        level.Chords.Add(PlainChord(3, 1, (0, 5), (1, 7)));

        var song = Write(project);

        // The SOURCE strum survives; only the landing is split.
        var chord = Assert.Single(Chords(song));
        Assert.Equal("1.000", chord.Attribute("time")!.Value);

        var notes = Notes(song);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal("1.500", n.Attribute("time")!.Value));
        Assert.Equal(new[] { "5", "7" }, notes.Select(n => n.Attribute("fret")!.Value).Order());
    }

    [Fact]
    public void TheDomainCollectionsAreNotMutatedByTheSplit()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        var source = PlainChord(2, 0, (0, 5));
        source.ChordNotes[0].Sustain = new FractionalPosition(1);
        source.ChordNotes[0].LinkNext = true;
        level.Chords.Add(source);
        level.Chords.Add(PlainChord(3, 1, (0, 5)));

        Write(project);

        Assert.Equal(2, level.Chords.Count);
        Assert.Empty(level.Notes);
    }

    [Fact]
    public void AChordNoLinkLandsOn_StaysAChord()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        // Same shapes, no link at all.
        level.Chords.Add(PlainChord(2, 0, (0, 5), (1, 7)));
        level.Chords.Add(PlainChord(3, 1, (0, 5), (1, 7)));

        var song = Write(project);

        Assert.Equal(2, Chords(song).Count);
        Assert.Empty(Notes(song));
    }

    [Fact]
    public void ALinkThatStopsShortOfTheChord_LeavesItAChord()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        var source = PlainChord(2, 0, (0, 5));
        source.ChordNotes[0].Sustain = new FractionalPosition(0, new Fraction(1, 2));
        source.ChordNotes[0].LinkNext = true;
        level.Chords.Add(source);
        level.Chords.Add(PlainChord(3, 1, (0, 5)));

        var song = Write(project);

        Assert.Equal(2, Chords(song).Count);
    }

    // ── 1. albumName ─────────────────────────────────────────────────────

    [Fact]
    public void ChartData_MetadataSentinels_AreEmptyNotPlaceholders()
    {
        var chart = new ChartData();

        // The 19-space albumName and the "Unknown Artist" artist/sort defaults
        // passed every blank check downstream and got exported verbatim.
        Assert.Equal("", chart.AlbumName);
        Assert.Equal("", chart.AlbumYear);
        Assert.Equal("", chart.Artist);
        Assert.Equal("", chart.ArtistSort);
    }

    [Fact]
    public void AlbumName_WhitespaceOnly_ExportsEmpty()
    {
        var project = BuildProject();
        project.Chart.AlbumName = "                   "; // the historical 19-space sentinel
        Arr(project).AlbumName = "";

        var song = Write(project);

        Assert.Equal("", Value(song, "albumName"));
    }

    [Fact]
    public void AlbumName_BlankOnArrangement_FallsBackToChart()
    {
        var project = BuildProject();
        project.Chart.AlbumName = "Real Album";
        Arr(project).AlbumName = "   ";

        var song = Write(project);

        Assert.Equal("Real Album", Value(song, "albumName"));
    }

    [Fact]
    public void AlbumName_PrefersArrangementOverChart()
    {
        var project = BuildProject();
        project.Chart.AlbumName = "Chart Album";
        Arr(project).AlbumName = "Arrangement Album";

        Assert.Equal("Arrangement Album", Value(Write(project), "albumName"));
    }

    // ── 2. artistNameSort ────────────────────────────────────────────────

    [Fact]
    public void ArtistNameSort_WithNoSortAuthored_FallsBackToArtistName()
    {
        var project = BuildProject();
        project.Chart.Artist = "Test";
        // ChartData.ArtistSort now defaults to empty, so the artistName
        // fallback in the writer actually fires.

        var song = Write(project);

        Assert.Equal("Test", Value(song, "artistName"));
        Assert.Equal("Test", Value(song, "artistNameSort"));
    }

    [Fact]
    public void ArtistNameSort_UsesAuthoredSortWhenPresent()
    {
        var project = BuildProject();
        project.Chart.Artist = "The Beatles";
        project.Chart.ArtistSort = "Beatles, The";

        var song = Write(project);

        Assert.Equal("The Beatles", Value(song, "artistName"));
        Assert.Equal("Beatles, The", Value(song, "artistNameSort"));
    }

    // ── 3. songLength ────────────────────────────────────────────────────

    [Fact]
    public void SongLength_UsesAudioLengthMinusOneMs_WhenAudioIsKnown()
    {
        // 16 beats -> last beat at 7500 ms. Audio is longer, so the chart's own
        // length wins; make audio shorter to prove it is honoured.
        var project = BuildProject();

        Assert.Equal("6.999", Value(Write(project, 7000), "songLength"));
    }

    [Fact]
    public void SongLength_KeepsTheChartLength_WhenTheAudioOutlastsTheChart()
    {
        // 16 beats -> last beat at 7500 ms. min(chart, audio) means a long
        // audio file must NOT stretch the chart's end (EOF_SRC\rs.c:590-593).
        var project = BuildProject();

        Assert.Equal("7.499", Value(Write(project, 600_000), "songLength"));
    }

    [Fact]
    public void SongLength_FallsBackToChartLength_WithNoFiveSecondPad()
    {
        var project = BuildProject(); // last beat 7500 ms

        var songLength = Value(Write(project), "songLength");

        Assert.Equal("7.499", songLength);
        // The old writer emitted lastBeat + 5000 = "12.500".
        Assert.NotEqual("12.500", songLength);
    }

    [Fact]
    public void SongLength_CoversContentThatOutlastsTheLastBeat()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        // A note starting on the last beat (7500 ms) with a 1-beat sustain
        // ends at 8000 ms, past the beat grid.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(15),
            Sustain = new FractionalPosition(1),
            StringIndex = 0,
            Fret = 5
        });

        Assert.Equal("7.999", Value(Write(project), "songLength"));
    }

    // ── 4. averageTempo ──────────────────────────────────────────────────

    [Fact]
    public void AverageTempo_DividesBySpanIntervals_NotBeatCount()
    {
        // 16 beats at exactly 500 ms -> 15 intervals over 7500 ms -> 120 BPM.
        var project = BuildProject();

        var averageTempo = Value(Write(project), "averageTempo");

        Assert.Equal("120.000", averageTempo);
        // Dividing by beats.Count (16) instead of 15 yielded 128.000.
        Assert.NotEqual("128.000", averageTempo);
    }

    [Fact]
    public void AverageTempo_SingleBeat_FallsBackToDefault()
    {
        var project = BuildProject(1);

        Assert.Equal("120.000", Value(Write(project), "averageTempo"));
    }

    // ── 5. anchors ───────────────────────────────────────────────────────

    [Fact]
    public void Anchors_ConsecutiveSameFretAndWidth_CollapseToTheEarliestTime()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        // A true duplicate: the hand neither moved nor changed its span.
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 6, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 6, 4));

        var anchors = Anchors(Write(project));

        var anchor = Assert.Single(anchors);
        Assert.Equal("1.000", anchor.Attribute("time")!.Value);
        Assert.Equal("6", anchor.Attribute("fret")!.Value);
        Assert.Equal("4.000", anchor.Attribute("width")!.Value);
    }

    [Fact]
    public void Anchors_SameFretDifferentWidth_AreKeptVerbatim()
    {
        // A width change IS a hand-span change: Rocksmith derives the finger as
        // 1 + (fret - anchor.fret) - (anchor.width - 4), so collapsing these and
        // keeping the widest width both dropped an authored anchor and shifted
        // the finger the game expects — which is how the export invented a
        // "finger used changes during slide" error DLC Builder then reported.
        // EOF's own exports carry 60 such pairs across the TestData/EOF corpus.
        var project = BuildProject();
        var level = AddLevel(project);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 6, 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 6, 4));

        var anchors = Anchors(Write(project));

        Assert.Equal(2, anchors.Count);
        Assert.Equal("1.000", anchors[0].Attribute("time")!.Value);
        Assert.Equal("5.000", anchors[0].Attribute("width")!.Value);
        Assert.Equal("1.500", anchors[1].Attribute("time")!.Value);
        Assert.Equal("4.000", anchors[1].Attribute("width")!.Value);
    }

    [Fact]
    public void Anchors_DifferentFrets_AreNeverCollapsed()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 6, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 7, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(4), 6, 4));

        Assert.Equal(3, Anchors(Write(project)).Count);
    }

    [Fact]
    public void Anchors_SameFretAcrossAPhraseBoundary_AreKept()
    {
        // RS needs a fret-hand position at the start of every phrase iteration,
        // so a same-fret restatement there is load-bearing, not redundant.
        // The two must share a WIDTH, or the pair would survive on the width
        // rule alone and this would stop testing the phrase boundary at all.
        var project = BuildProject();
        var arr = Arr(project);
        arr.Phrases.Add("COUNT");
        arr.Phrases.Add("verse");
        arr.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(3), 1));

        var level = AddLevel(project);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 6, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 6, 4));

        var anchors = Anchors(Write(project));

        Assert.Equal(2, anchors.Count);
        Assert.Equal("1.000", anchors[0].Attribute("time")!.Value);
        Assert.Equal("1.500", anchors[1].Attribute("time")!.Value);
    }

    [Fact]
    public void Anchors_DedupeIsWriteTimeOnly_AndNeverMutatesTheDomain()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 6, 4));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 6, 14));

        Write(project);

        Assert.Equal(2, level.Anchors.Count);
        Assert.Equal(4, level.Anchors[0].Width);
        Assert.Equal(new FractionalPosition(3), level.Anchors[1].Position);
    }

    // ── Part B: compact repeats ──────────────────────────────────────────────

    /// <summary>
    /// Four identical chords under one handshape — EOF's canonical high-density
    /// run. The first chord in the handshape stays low density.
    /// </summary>
    private static ChartProject BuildRepeatedChordRun(int chordCount = 4)
    {
        var project = BuildProject();
        var arr = Arr(project);
        var template = new ChordTemplate { Id = 1, Name = "F" };
        template.Frets[0] = 1;
        template.Frets[1] = 3;
        template.Frets[2] = 3;
        arr.ChordTemplates.Add(template);

        var level = AddLevel(project);
        for (var i = 0; i < chordCount; i++)
            level.Chords.Add(PlainChord(new FractionalPosition(i), 1, (0, 1), (1, 3), (2, 3)));

        level.HandShapes.Add(new HandShape
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(chordCount),
            TemplateId = 1
        });

        return project;
    }

    /// <summary>
    ///     Whether this chord ships as a COMPACT REPEAT - which the game decides
    ///     from the absence of <c>&lt;chordNote&gt;</c> children, NOT from the
    ///     <c>highDensity</c> attribute.
    ///     <para>
    ///         This predicate used to read the attribute, and every test below
    ///         passed while the shipped charts rendered every repeat as a full
    ///         panel with its chord name. Rocksmith2014.NET sets its
    ///         <c>ChordPanel</c> mask from <c>chord.HasChordNotes</c>
    ///         (<c>XmlToSngNote.fs</c>) and only translates the attribute for XML
    ///         older than version 8 (<c>PackageBuilder.fs</c> then
    ///         <c>FixHighDensity</c>) - and we write version 8. Measured on the
    ///         owner's own chart: 474 chords, 335 of them repeats, 474 shipping
    ///         with chord notes.
    ///     </para>
    /// </summary>
    private static bool IsCompactRepeat(XElement chord) => chord.Element("chordNote") is null;

    [Fact]
    public void CompactRepeat_RepeatedChordsInAHandShape_AreCompactedExceptTheFirst()
    {
        var chords = Chords(Write(BuildRepeatedChordRun()));

        Assert.Equal(4, chords.Count);
        Assert.False(IsCompactRepeat(chords[0]), "the first chord in a handshape is always the full box");
        Assert.True(IsCompactRepeat(chords[1]));
        Assert.True(IsCompactRepeat(chords[2]));
        Assert.True(IsCompactRepeat(chords[3]));
    }

    /// <summary>
    ///     The compaction drops the chord notes and writes NO <c>highDensity</c>
    ///     attribute - the spelling official files use, and the only one that
    ///     survives a version-8 build.
    ///     <para>
    ///         EOF keeps the subtags AND writes the attribute (rs.c:2982), which is
    ///         correct for EOF because EOF writes version 7 and DLC Builder then
    ///         converts. DotCharter writes version 8, so it has to do the
    ///         conversion itself - which is exactly what EOF's own
    ///         <c>eof_rs2_export_version_8</c> preference does (rs.c:2957).
    ///     </para>
    ///     <para>
    ///         Nothing is lost: every technique that lives on a
    ///         <c>&lt;chordNote&gt;</c> already disqualifies a chord from
    ///         compacting, and the shape itself lives in the chord template.
    ///     </para>
    /// </summary>
    [Fact]
    public void CompactRepeat_ShipsWithNoChordNotesAndNoHighDensityAttribute()
    {
        var chords = Chords(Write(BuildRepeatedChordRun()));

        Assert.Empty(chords[1].Elements("chordNote"));
        Assert.Null(chords[1].Attribute("highDensity"));
    }

    /// <summary>The other side: a chord that is NOT a repeat keeps every child.</summary>
    [Fact]
    public void AFullChordPanelKeepsItsChordNoteChildren()
    {
        var chords = Chords(Write(BuildRepeatedChordRun()));

        Assert.Equal(3, chords[0].Elements("chordNote").Count());
    }

    /// <summary>
    ///     The version this all hinges on. DLC Builder only rewrites the
    ///     <c>highDensity</c> convention when <c>xml.Version &lt; 8</c>
    ///     (<c>PackageBuilder.fs</c>), so if this ever drops below 8 the writer
    ///     must go back to emitting the attribute instead.
    /// </summary>
    [Fact]
    public void TheExportDeclaresVersionEight() => Assert.Equal("8", Write(BuildRepeatedChordRun()).Attribute("version")!.Value);

    [Fact]
    public void CompactRepeat_IsNotEmittedWhenTheChordIdChanges()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        level.Chords[2].TemplateId = 2; // same frets, different template

        var chords = Chords(Write(project));

        Assert.True(IsCompactRepeat(chords[1]));
        Assert.False(IsCompactRepeat(chords[2]), "a chordId change forces the full chord panel");
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedForAChordThatNeedsChordNoteData()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        level.Chords[2].ChordNotes[0].Vibrato = true;

        var chords = Chords(Write(project));

        Assert.True(IsCompactRepeat(chords[1]));
        Assert.False(IsCompactRepeat(chords[2]));
    }

    /// <summary>
    ///     EOF's explicit sustain STATUS holds a repeat at its full panel
    ///     (EOF_SRC/rs.c:5826 - <c>EOF_PRO_GUITAR_NOTE_EFLAG_SUSTAIN</c>).
    /// </summary>
    [Fact]
    public void CompactRepeat_IsNotEmittedForAChordWithExplicitSustainStatus()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        level.Chords[2].ChordNotes[0].Sustain = new FractionalPosition(1);
        level.Chords[2].ChordNotes[0].ExplicitSustain = true;

        Assert.False(IsCompactRepeat(Chords(Write(project))[2]));
    }

    /// <summary>
    ///     ...and an ORDINARY sustain does not, which is the half that was wrong.
    ///     EOF's flag is a per-note status a charter sets (song.h:131, the note
    ///     dialog's "Sustain" checkbox), not "length &gt; 0"; reading it off the
    ///     length made every charted chord ineligible, because they all have some.
    /// </summary>
    [Fact]
    public void AnOrdinarySustainDoesNotBlockCompaction()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        foreach (var chordNote in level.Chords[2].ChordNotes)
            chordNote.Sustain = new FractionalPosition(1);

        Assert.True(IsCompactRepeat(Chords(Write(project))[2]));
    }

    /// <summary>
    ///     A chord the charter marked "only box" ships the same way. The SNG
    ///     exporter has honoured <c>ForceNoNotes</c> since it was added; the XML
    ///     writer never read it, so one chart exported two different ways
    ///     depending on which artifact you built.
    /// </summary>
    [Fact]
    public void ForceNoNotesShipsWithoutChordNotesToo()
    {
        var project = BuildRepeatedChordRun();
        Arr(project).Levels[0].Chords[0].ForceNoNotes = true;

        Assert.True(IsCompactRepeat(Chords(Write(project))[0]));
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedAfterAChordSlide()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        level.Chords[1].ChordNotes[0].SlideTo = 5;

        var chords = Chords(Write(project));

        // The slide chord itself needs chordNote data...
        Assert.False(IsCompactRepeat(chords[1]));
        // ...and the chord following a chord slide is forced low density too.
        Assert.False(IsCompactRepeat(chords[2]));
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedWhenTheFretsDiffer()
    {
        var project = BuildRepeatedChordRun();
        var level = Arr(project).Levels[0];
        level.Chords[2].ChordNotes[0].Fret = 2;

        Assert.False(IsCompactRepeat(Chords(Write(project))[2]));
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedForASingleNoteWideChord()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        level.Chords.Add(PlainChord(0, 1, (0, 1)));
        level.Chords.Add(PlainChord(1, 1, (0, 1)));

        var chords = Chords(Write(project));

        Assert.All(chords, c => Assert.False(IsCompactRepeat(c)));
    }

    [Fact]
    public void CompactRepeat_OutsideAHandShape_StillCompactsAnImmediateRepeat()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        level.Chords.Add(PlainChord(0, 1, (0, 1), (1, 3)));
        level.Chords.Add(PlainChord(1, 1, (0, 1), (1, 3)));

        var chords = Chords(Write(project));

        Assert.False(IsCompactRepeat(chords[0]));
        Assert.True(IsCompactRepeat(chords[1]));
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedBeyondTheRepeatThreshold()
    {
        // EOF's eof_chord_density_threshold default is 10 s; place the repeat
        // well past it on a sparse beat grid.
        var project = new ChartProject();
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 20_000.0, Bpm = 3, IsMeasure = true });
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(arr);

        var level = AddLevel(project);
        level.Chords.Add(PlainChord(0, 1, (0, 1), (1, 3)));
        level.Chords.Add(PlainChord(1, 1, (0, 1), (1, 3))); // 20 s later

        var chords = Chords(Write(project));

        Assert.False(IsCompactRepeat(chords[1]));
    }

    // ── chordNote sustain vs. tempo ──────────────────────────────────────

    [Fact]
    public void ChordNoteSustain_IsMeasuredWhereTheChordIs_NotAtBar1Tempo()
    {
        // 120 bpm (500 ms/beat) for the first eight beats, then 60 bpm (1000 ms).
        var project = new ChartProject();
        for (var i = 0; i <= 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        for (var i = 1; i <= 4; i++)
            project.Beats.Add(new BeatMarker { Time = 4000.0 + i * 1000.0, Bpm = 60, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        project.Arrangements.Add(arr);

        var level = AddLevel(project);
        var chord = PlainChord(8, 1, (0, 1), (1, 3)); // beat 8 == 4000 ms, in the 60 bpm half
        foreach (var cn in chord.ChordNotes) cn.Sustain = new FractionalPosition(1); // one beat
        level.Chords.Add(chord);

        var chordNotes = Chords(Write(project))[0].Elements("chordNote").ToList();

        // One beat at 60 bpm is 1000 ms ("1.000" in RS's seconds.millis form).
        // Measuring the sustain from the grid ORIGIN resolves it at bar-1 tempo
        // and writes 0.500 — every chord-note sustain on a tempo-varying chart
        // exported at the wrong length.
        Assert.NotEmpty(chordNotes);
        Assert.All(chordNotes, cn => Assert.Equal("1.000", cn.Attribute("sustain")!.Value));
    }

    [Fact]
    public void ChordNoteSustain_IsUnchangedOnAConstantTempoChart()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        var chord = PlainChord(4, 1, (0, 1), (1, 3));
        foreach (var cn in chord.ChordNotes) cn.Sustain = new FractionalPosition(2); // two beats
        level.Chords.Add(chord);

        var chordNotes = Chords(Write(project))[0].Elements("chordNote").ToList();

        // Two beats at a constant 120 bpm — the fix must be a strict no-op here.
        Assert.NotEmpty(chordNotes);
        Assert.All(chordNotes, cn => Assert.Equal("1.000", cn.Attribute("sustain")!.Value));
    }

    [Fact]
    public void CompactRepeat_IsNotEmittedWhenASingleNoteInterruptsTheRepeat()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        level.Chords.Add(PlainChord(0, 1, (0, 1), (1, 3)));
        level.Chords.Add(PlainChord(2, 1, (0, 1), (1, 3)));
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1),
            StringIndex = 5,
            Fret = 7
        });

        var chords = Chords(Write(project));

        Assert.False(IsCompactRepeat(chords[1]));
    }

    // ─── Fields the writer simply never emitted ───────────────────────────────

    [Fact]
    public void ToneSlots_FallBackToStartingToneAndTheToneChanges()
    {
        // The A-D slots were written raw, so an importer that fills StartingTone and
        // ToneChanges but not the slots (EofProGuitarTrackParser does exactly that)
        // produced <tone> changes referencing names declared in no element at all.
        var project = BuildProject();
        var arr = Arr(project);
        arr.StartingTone = "clean";
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "lead" });

        var song = Write(project);

        Assert.Equal("clean", Value(song, "tonebase"));
        Assert.Equal("lead", Value(song, "tonea"));
    }

    [Fact]
    public void ChordTemplate_ArpeggioFlagAloneStillMarksTheDisplayName()
    {
        // Only ForceArpeggioInRs was honoured; Arpeggio is a separate user-settable
        // flag and the SNG export folds both, so the two disagreed on the same chart.
        // THREE strings on purpose: a two-string shape ships unnamed since 0.41,
        // and this test is about the arpeggio flag reaching displayName, not about
        // the double-stop gate (which has its own coverage in
        // DoubleStopChordNameTests, including the "-arp" interaction).
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "Am", Arpeggio = true,
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 1, [2] = 2 }
        });

        var template = Write(project).Element("chordTemplates")!.Elements("chordTemplate").First();

        Assert.Equal("Am-arp", template.Attribute("displayName")!.Value);
    }

    [Fact]
    public void Note_VibratoIsWrittenAsAStrengthNotAFlag()
    {
        // RS2014's vibrato attribute is a strength (40/80/120). A bare 1 made the XML
        // and the SNG built from the same chart disagree on every vibrato.
        var project = BuildProject();
        var level = AddLevel(project);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            Vibrato = true, VibratoStrength = 120
        });

        var note = Write(project).Descendants("note").First();

        Assert.Equal("120", note.Attribute("vibrato")!.Value);
    }

    [Fact]
    public void ChordNote_IgnoreIsWritten()
    {
        var project = BuildProject();
        var level = AddLevel(project);
        var chord = PlainChord(0, 0, (0, 1), (1, 3));
        chord.ChordNotes[1].Ignore = true;
        level.Chords.Add(chord);

        var chordNotes = Write(project).Descendants("chordNote").ToList();

        Assert.Equal("1", chordNotes[1].Attribute("ignore")!.Value);
    }

    [Fact]
    public void ChordNote_ThumbFingeringSurvives()
    {
        // FingerNames maps 0 to "T" and WriteChordTemplate emits finger0="0", so the
        // `> 0` filter left the file inconsistent with its own template.
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "F",
            Frets = new Dictionary<int, int> { [0] = 1, [1] = 3 },
            Fingers = new Dictionary<int, int> { [0] = 0, [1] = 3 }
        });
        var level = AddLevel(project);
        level.Chords.Add(PlainChord(0, 0, (0, 1), (1, 3)));

        var chordNotes = Write(project).Descendants("chordNote").ToList();

        Assert.Equal("0", chordNotes[0].Attribute("leftHand")!.Value);
        Assert.Equal("3", chordNotes[1].Attribute("leftHand")!.Value);
    }

    [Fact]
    public void Tuning_EmitsTheStringCountWhenItIsNotSix()
    {
        // The reader honours <tuning strings="N">; the writer never emitted it, so a
        // 4- or 5-string bass round-tripped as a 6-string.
        var project = BuildProject();
        var arr = Arr(project);
        arr.StringCount = 4;

        var tuning = Write(project).Element("tuning")!;

        Assert.Equal("4", tuning.Attribute("strings")!.Value);
    }

    [Fact]
    public void Tuning_OmitsTheStringCountForAPlainSixString()
    {
        // A normal guitar arrangement must stay byte-identical to what shipped.
        var tuning = Write(BuildProject()).Element("tuning")!;

        Assert.Null(tuning.Attribute("strings"));
    }

    [Fact]
    public void Phrases_AnEditorAuthoredMarkerStillReachesThePhraseIterations()
    {
        // Both stores are live at once: the rscp reader fills Phrases/PhraseIterations
        // while the timeline and overview editors author EventPoint.Phrase. The writer
        // short-circuited on the explicit lists, so a phrase added in the editor never
        // occurred in the exported song.
        var project = BuildProject();
        var arr = Arr(project);
        arr.Phrases.Add("COUNT");
        arr.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "riff" });

        var song = Write(project);

        var names = song.Element("phrases")!.Elements("phrase")
            .Select(p => p.Attribute("name")!.Value).ToList();
        Assert.Contains("riff", names);

        var riffId = names.IndexOf("riff");
        Assert.Contains(song.Element("phraseIterations")!.Elements("phraseIteration"),
            it => it.Attribute("phraseId")!.Value == riffId.ToString());
    }

    [Fact]
    public void Phrases_AnImportedChartWhoseStoresAgree_IsUnchanged()
    {
        // The merge is additive: when both stores describe the same phrases every
        // marker matches an existing iteration by position, so an RS-imported chart
        // must round-trip with exactly the phrases it arrived with.
        var project = BuildProject();
        var arr = Arr(project);
        arr.Phrases.Add("COUNT");
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        // The reader mirrors the same phrases onto the event points.
        arr.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Phrase = "COUNT" });
        arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "riff" });

        var song = Write(project);

        Assert.Equal(["COUNT", "riff"],
            song.Element("phrases")!.Elements("phrase").Select(p => p.Attribute("name")!.Value));
        Assert.Equal(2, song.Element("phraseIterations")!.Elements("phraseIteration").Count());
    }

    [Theory]
    [InlineData(ArrangementSubtype.Main)]
    [InlineData(ArrangementSubtype.Bonus)]
    [InlineData(ArrangementSubtype.Alternate)]
    public void Subtype_SurvivesTheRoundTrip(ArrangementSubtype subtype)
    {
        // represent/bonusArr fully encode the subtype and the writer derives them
        // from it, but the reader left them in Properties only — so a bonus
        // arrangement re-imported as Main. BuildArrangementFileName bakes Subtype
        // into the export filename, so the next export then OVERWROTE the main
        // arrangement's XML.
        var project = BuildProject();
        Arr(project).Subtype = subtype;

        var doc = new RsXmlSongWriter().Write(project, null);
        var reloaded = new RsXmlSongReader().ReadDocument(doc, "roundtrip.xml");

        Assert.Equal(subtype, reloaded.Arrangements[0].Subtype);
    }
}
