using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A repeated chord inside a hand shape ships WITHOUT chord notes, in every
///     artifact, and survives the trip back.
///     <para>
///         Owner report, 2026-08-24: <i>"in actual rocksmith it rendered all
///         repeated chords with their full size and the chord name on all"</i>.
///         The 3D preview was right; the export was not.
///     </para>
///     <para>
///         Rocksmith decides this from the ABSENCE of <c>&lt;chordNote&gt;</c>
///         children — <c>XmlToSngNote.fs</c> sets <c>NoteMask.ChordPanel</c> from
///         <c>chord.HasChordNotes</c> alone. The <c>highDensity</c> attribute is a
///         custom-file convention DLC Builder translates in
///         <c>InstrumentalArrangement.FixHighDensity</c>, and it only runs that
///         translation for XML older than version 8. DotCharter writes version 8
///         and kept the children, so nothing ever converted: measured on the
///         owner's chart, 474 chords with 335 repeats shipped 474 chord panels.
///     </para>
///     <para>
///         Cross-checked against the two tools that get this right: <b>EOF</b>
///         writes version 7 (children kept, DLC Builder converts) and
///         <b>RsCharter</b> writes version 8 with the children already dropped —
///         23 of 80 chords on the same test chart. DotCharter was alone in
///         combining version 8 with the children.
///     </para>
/// </summary>
public sealed class CompactRepeatChordTests
{
    private static List<BeatMarker> Beats(int count = 16)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0,
                BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        return beats;
    }

    /// <summary>Four identical F chords under one hand shape — the canonical run.</summary>
    private static ChartProject RepeatedRun(int chordCount = 4)
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));

        var template = new ChordTemplate { Id = 1, Name = "F" };
        template.Frets[0] = 1;
        template.Frets[1] = 3;
        template.Frets[2] = 3;
        arr.ChordTemplates.Add(template);

        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var i = 0; i < chordCount; i++)
        {
            var chord = new Chord { Position = new FractionalPosition(i), TemplateId = 1 };
            foreach (var (stringIndex, fret) in new[] { (0, 1), (1, 3), (2, 3) })
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            level.Chords.Add(chord);
        }

        level.HandShapes.Add(new HandShape
        {
            Position = FractionalPosition.Zero,
            EndPosition = new FractionalPosition(chordCount),
            TemplateId = 1
        });

        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }

    private static List<XElement> WrittenChords(ChartProject project) =>
        new RsXmlSongWriter().Write(project, null).Root!
            .Element("levels")!.Element("level")!
            .Element("chords")!.Elements("chord").ToList();

    // ── the export ──────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheFirstChordOfTheHandShapeShipsAChordPanel()
    {
        var chords = WrittenChords(RepeatedRun());

        Assert.Equal(3, chords[0].Elements("chordNote").Count());
        Assert.Empty(chords[1].Elements("chordNote"));
        Assert.Empty(chords[2].Elements("chordNote"));
        Assert.Empty(chords[3].Elements("chordNote"));
    }

    /// <summary>
    ///     Per-string state that the compact chord tag cannot carry keeps the full
    ///     panel. EOF has no guard for this because EOF has no per-STRING accent,
    ///     ignore or mute — they are whole-note flags there, and the
    ///     <c>&lt;chord&gt;</c> tag carries them losslessly. DotCharter's inspector
    ///     writes per-string chips, so the port needs the extra rule or the
    ///     compaction silently eats them.
    /// </summary>
    [Fact]
    public void APartlyMutedRepeatKeepsItsChordNotes()
    {
        var project = RepeatedRun();
        project.Arrangements[0].Levels[0].Chords[1].ChordNotes[0].Mute = MuteType.Palm;

        Assert.Equal(3, WrittenChords(project)[1].Elements("chordNote").Count());
    }

    /// <summary>…while a mute the whole strum shares still compacts, because the
    /// chord tag says <c>palmMute="1"</c> for itself.</summary>
    [Fact]
    public void AFullyPalmMutedRepeatStillCompacts()
    {
        var project = RepeatedRun();
        foreach (var chordNote in project.Arrangements[0].Levels[0].Chords[1].ChordNotes)
            chordNote.Mute = MuteType.Palm;

        var chord = WrittenChords(project)[1];

        Assert.Empty(chord.Elements("chordNote"));
        Assert.Equal("1", chord.Attribute("palmMute")!.Value);
    }

    // ── the trip back ───────────────────────────────────────────────────

    /// <summary>
    ///     A chord with no children reads back with its shape restored from its
    ///     template. Without this the export would be lossy against itself — and
    ///     it was already lossy against every OFFICIAL file, which spell every
    ///     repeat this way: they imported as chords with zero notes, drawing
    ///     nothing in the editor and exporting as nothing.
    /// </summary>
    [Fact]
    public void AChordWithNoChordNotesReadsBackWithItsShape()
    {
        var project = RepeatedRun();
        var document = new RsXmlSongWriter().Write(project, null);

        var reloaded = new RsXmlSongReader().ReadDocument(document, "compact-repeat.xml");
        var chords = reloaded.Arrangements[0].Levels[0].Chords;

        Assert.Equal(4, chords.Count);
        Assert.All(chords, c => Assert.Equal(3, c.ChordNotes.Count));
        Assert.Equal([1, 3, 3], chords[2].ChordNotes.OrderBy(c => c.StringIndex).Select(c => c.Fret));
    }

    // ── the SNG DotCharter builds itself ────────────────────────────────

    /// <summary>
    ///     The psarc DotCharter writes directly must make the same call as the XML
    ///     it hands to DLC Builder, or one chart renders two different ways
    ///     depending on which button you pressed.
    /// </summary>
    [Fact]
    public void TheSngAgreesWithTheXml()
    {
        var project = RepeatedRun();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.AddRange(["COUNT", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 8000);

        var chords = sng.Sng.Levels[0].Notes
            .Where(n => (n.Mask & RsNoteMask.Chord) != 0)
            .OrderBy(n => n.Time)
            .ToList();

        // ChordPanel is the bit that matters and the ONLY one that tracks this:
        // ChordNotesId stays -1 for a plain chord either way, because
        // CreateChordNotes returns -1 when every per-string mask is None (the
        // same short-circuit RS2014.NET's XmlToSngNote.fs has).
        Assert.Equal(4, chords.Count);
        Assert.True((chords[0].Mask & RsNoteMask.ChordPanel) != 0, "the first chord keeps its panel");

        foreach (var repeat in chords.Skip(1))
        {
            Assert.True((repeat.Mask & RsNoteMask.ChordPanel) == 0,
                "a repeat must not carry the chord panel — that is what draws the box and the name");
        }
    }

    // ── the flag that decides it ────────────────────────────────────────

    /// <summary>
    ///     <c>ExplicitSustain</c> is the port of EOF's
    ///     <c>EOF_PRO_GUITAR_NOTE_EFLAG_SUSTAIN</c>, and it has to survive a save:
    ///     an RS import states it, and if the project file drops it, opening and
    ///     re-exporting an imported chart would compact chords the source held at
    ///     full size.
    /// </summary>
    [Fact]
    public void TheExplicitSustainStatusSurvivesTheProjectFile()
    {
        var project = RepeatedRun();
        project.Arrangements[0].Levels[0].Chords[0].ChordNotes[0].ExplicitSustain = true;

        var path = Path.Combine(Path.GetTempPath(), $"dc-explicit-sustain-{Guid.NewGuid():N}.rscp");
        try
        {
            var serializer = new RscpChartSerializer(new FakeAppLogger());
            serializer.Save(path, project);
            var reloaded = serializer.Load(path);

            var chordNotes = reloaded.Arrangements[0].Levels[0].Chords[0].ChordNotes;
            Assert.True(chordNotes[0].ExplicitSustain);
            Assert.False(chordNotes[1].ExplicitSustain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     An RS import states the status for itself, mirroring
    ///     EOF_SRC/rs_import.c:370-395 — a chord note that arrived from Rocksmith
    ///     carrying its own sustain keeps its full panel on the way back out.
    /// </summary>
    [Fact]
    public void ReadingRsXmlStatesTheSustainStatusForSustainedChordNotes()
    {
        // The ebeats matter: the reader derives its own beat map from them, and a
        // chordNote's sustain is read as a POSITION difference — with no grid,
        // every sustain collapses to zero and this fixture would prove nothing.
        var ebeats = string.Concat(Enumerable.Range(0, 8)
            .Select(i => $"<ebeat time=\"{i * 0.5:0.000}\" measure=\"{(i % 4 == 0 ? i / 4 : -1)}\" />"));

        var document = XDocument.Parse(
            "<song version=\"8\">" +
            $"  <ebeats count=\"8\">{ebeats}</ebeats>" +
            "  <chordTemplates count=\"1\">" +
            "    <chordTemplate chordName=\"F\" fret0=\"1\" fret1=\"3\" finger0=\"1\" finger1=\"3\" />" +
            "  </chordTemplates>" +
            "  <levels count=\"1\">" +
            "    <level difficulty=\"0\">" +
            "      <chords count=\"2\">" +
            "        <chord time=\"0.000\" chordId=\"0\">" +
            "          <chordNote time=\"0.000\" string=\"0\" fret=\"1\" sustain=\"0.400\" />" +
            "          <chordNote time=\"0.000\" string=\"1\" fret=\"3\" />" +
            "        </chord>" +
            "        <chord time=\"1.000\" chordId=\"0\" />" +
            "      </chords>" +
            "    </level>" +
            "  </levels>" +
            "</song>");

        var chords = new RsXmlSongReader().ReadDocument(document, "sustain-status.xml")
            .Arrangements[0].Levels[0].Chords;

        Assert.True(chords[0].ChordNotes[0].ExplicitSustain, "it arrived with a sustain of its own");
        Assert.False(chords[0].ChordNotes[1].ExplicitSustain, "it did not");
        Assert.Equal(2, chords[1].ChordNotes.Count);
        Assert.All(chords[1].ChordNotes, cn => Assert.False(cn.ExplicitSustain));
    }
}
