using System.Xml.Linq;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     EOF's Split and Fingerless statuses survive a trip through DotCharter (#609).
///     <list type="bullet">
///         <item><b>Split</b> — <c>EOF_PRO_GUITAR_NOTE_FLAG_SPLIT = 536870912</c>
///         (<c>EOF_SRC/song.h:64</c>), a note flag: "a chord that exports to
///         Rocksmith as single notes instead of a chord". It is
///         <see cref="Chord.SplitIntoNotes" />.</item>
///         <item><b>Fingerless</b> — <c>EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS = 32</c>
///         (<c>song.h:137</c>), an EXTENDED flag: the chord intentionally has no
///         fingering. EOF calls its fingering valid (<c>rs.c:3593-3596</c>) and
///         exports every finger as <c>-1</c>. It is <see cref="ChordTemplate.Fingerless" />.</item>
///     </list>
///     The importer read neither and the exporter wrote neither, so a split chord in
///     a <c>.eof</c> came in as an ordinary chord and DotCharter → EOF lost both.
/// </summary>
public sealed class EofSplitFingerlessTests
{
    [Fact]
    public void TheBits_AreEofsOwn()
    {
        // A wrong bit is invisible in a DotCharter-only round trip and changes what
        // EOF itself reads, so the values are pinned against song.h, not each other.
        Assert.Equal(536870912u, EofConstants.PgFlagSplit);
        Assert.Equal(32u, EofConstants.PgEFlagFingerless);
    }

    [Fact]
    public void TheExport_WritesSplitAsTheNoteFlag_AndFingerlessAsTheExtendedFlag()
    {
        var project = Project();

        var notes = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);

        var split = notes.Single(n => n.Raw.PositionMs == 2000);
        var fingerless = notes.Single(n => n.Raw.PositionMs == 3000);
        var plain = notes.Single(n => n.Raw.PositionMs == 4000);
        Assert.NotEqual(0u, split.Raw.Flags & EofConstants.PgFlagSplit);
        Assert.Equal(0u, split.Raw.ExtendedFlags & EofConstants.PgEFlagFingerless);
        Assert.NotEqual(0u, fingerless.Raw.ExtendedFlags & EofConstants.PgEFlagFingerless);
        Assert.Equal(0u, fingerless.Raw.Flags & EofConstants.PgFlagSplit);
        Assert.Empty(fingerless.Fingers);
        Assert.Equal(0u, plain.Raw.Flags & EofConstants.PgFlagSplit);
        Assert.Equal(0u, plain.Raw.ExtendedFlags & EofConstants.PgEFlagFingerless);
    }

    /// <summary>A <c>.eof</c> with a split chord and a fingerless chord → DotCharter →
    /// <c>.eof</c> keeps both flags, and the import in between says what they mean.</summary>
    [Fact]
    public void A_split_and_a_fingerless_chord_survive_eof_to_dotcharter_to_eof()
    {
        var first = EofExporter.Export(Project());
        Assert.True(first.Report.IsLossless, Describe(first.Report));

        var imported = Import(first.Bytes);
        var arrangement = Assert.Single(imported.Arrangements);
        var level = Assert.Single(arrangement.Levels);
        var split = level.Chords.Single(c => Ms(imported, c) == 2000);
        var fingerless = level.Chords.Single(c => Ms(imported, c) == 3000);
        var plain = level.Chords.Single(c => Ms(imported, c) == 4000);

        Assert.True(split.SplitIntoNotes);
        Assert.False(TemplateOf(arrangement, split).Fingerless);
        Assert.Equal(3, TemplateOf(arrangement, split).Fingers.Count);

        Assert.False(fingerless.SplitIntoNotes);
        Assert.True(TemplateOf(arrangement, fingerless).Fingerless);
        Assert.Empty(TemplateOf(arrangement, fingerless).Fingers);
        // The tremolo phrase over it comes back onto every string — and only there.
        Assert.All(fingerless.ChordNotes, n => Assert.True(n.Tremolo));
        Assert.All(split.ChordNotes.Concat(plain.ChordNotes), n => Assert.False(n.Tremolo));

        Assert.False(plain.SplitIntoNotes);
        Assert.False(TemplateOf(arrangement, plain).Fingerless);

        // …and back out to EOF with the same two flags on the same two notes.
        var again = EofNoteBuilder.Build(arrangement, imported.Beats);
        Assert.NotEqual(0u, again.Single(n => n.Raw.PositionMs == 2000).Raw.Flags & EofConstants.PgFlagSplit);
        Assert.NotEqual(0u,
            again.Single(n => n.Raw.PositionMs == 3000).Raw.ExtendedFlags & EofConstants.PgEFlagFingerless);
        Assert.Equal(0u, again.Single(n => n.Raw.PositionMs == 4000).Raw.Flags & EofConstants.PgFlagSplit);
        Assert.Equal(0u,
            again.Single(n => n.Raw.PositionMs == 4000).Raw.ExtendedFlags & EofConstants.PgEFlagFingerless);
    }

    /// <summary>
    ///     EOF keeps a note's finger array under the Fingerless status and only
    ///     ignores it on export (<c>rs.c:910-914</c>, <c>rs.c:5958</c>). The import
    ///     drops those values: the grip is fingerless, and a fingerless chord on the
    ///     same frets as a fingered one plays a template of its own.
    /// </summary>
    [Fact]
    public void Fingers_EofStoredUnderTheFingerlessFlag_AreDropped_AndTheGripStaysItsOwnTemplate()
    {
        var project = Project();
        var arrangement = project.Arrangements[0];
        // Fingered AND flagged — what EOF stores when a charter ticks "Fingerless"
        // on a chord that already had fingers.
        arrangement.ChordTemplates[1].Fingers[1] = 1;
        arrangement.ChordTemplates[1].Fingers[2] = 3;
        // A fingered chord on the fingerless chord's frets.
        var fingered = new ChordTemplate { Id = 2, Name = "" };
        fingered.Frets[1] = 5;
        fingered.Frets[2] = 7;
        fingered.Fingers[1] = 1;
        fingered.Fingers[2] = 3;
        arrangement.ChordTemplates.Add(fingered);
        arrangement.Levels[0].Chords.Add(Chord(project, 5000, 2, (1, 5), (2, 7)));

        var imported = Import(EofExporter.Export(project).Bytes);

        var importedArrangement = imported.Arrangements[0];
        var level = importedArrangement.Levels[0];
        var flagged = TemplateOf(importedArrangement, level.Chords.Single(c => Ms(imported, c) == 3000));
        var other = TemplateOf(importedArrangement, level.Chords.Single(c => Ms(imported, c) == 5000));
        Assert.True(flagged.Fingerless);
        Assert.Empty(flagged.Fingers);
        Assert.False(other.Fingerless);
        Assert.Equal(2, other.Fingers.Count);
        Assert.NotSame(flagged, other);
    }

    /// <summary>The owner's reference lines from #599 (<i>Shinbatsu wo Tadaori</i>
    /// m197–198, the project's <c>1_Lead_Main_RS2.xml</c>). Copied verbatim.</summary>
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
    ///     EOF → DotCharter → RS XML for the #599 pick scrape: a <c>.eof</c> whose four
    ///     scrapes are SPLIT chords imports as four split chords, and the RS XML
    ///     written from it is the owner's eight <c>&lt;note&gt;</c> lines, field for
    ///     field, with no <c>&lt;chord&gt;</c>. The one known difference is the
    ///     owner's hand-trimmed E+A sustain (449 ms against the tab's half note,
    ///     480 ms) — the scene is built from the tab, as in
    ///     <c>GpPickScrapeImportTests.ReferenceScene_ExportsTheOwnersNoteLines_FieldForField</c>.
    /// </summary>
    [Fact]
    public void A_split_pick_scrape_in_an_eof_exports_the_owners_note_lines()
    {
        var eof = EofExporter.Export(PickScrapeScenes.ReferenceProject());
        var raw = EofNoteBuilder.Build(PickScrapeScenes.ReferenceProject().Arrangements[0],
            PickScrapeScenes.ReferenceProject().Beats);
        Assert.Equal(4, raw.Count(n => (n.Raw.Flags & EofConstants.PgFlagSplit) != 0));

        var imported = Import(eof.Bytes);
        var level = Assert.Single(Assert.Single(imported.Arrangements).Levels);
        Assert.Equal(4, level.Chords.Count(c => c.SplitIntoNotes && c.Ignore));

        var doc = new RsXmlSongWriter().Write(imported);
        Assert.Empty(doc.Descendants("chord"));
        Assert.Empty(doc.Descendants("handShape"));
        var written = doc.Descendants("note").Where(n => n.Attribute("ignore")?.Value == "1")
            .Select(Fields).ToArray();
        var expected = ReferenceNotes.Select(XElement.Parse).Select(Fields)
            .Select(f => f["time"] == "199.164" ? new SortedDictionary<string, string>(f) { ["sustain"] = "0.480" } : f)
            .ToArray();

        // One line per note, every attribute and value, so a failure names the field.
        Assert.Equal(expected.Select(Line), written.Select(Line));

        static SortedDictionary<string, string> Fields(XElement note)
            => new(note.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), StringComparer.Ordinal);

        static string Line(SortedDictionary<string, string> fields) =>
            string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}"));
    }

    // ── fixture ─────────────────────────────────────────────────────────────

    /// <summary>Three chords: a SPLIT C major (2 s), a FINGERLESS double stop tremolo
    /// on both strings (3 s) and an ordinary C major (4 s), on a 120 BPM grid.</summary>
    private static ChartProject Project()
    {
        var project = new ChartProject { Bpm = 120 };
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));

        var c = new ChordTemplate { Id = 0, Name = "C" };
        c.Frets[1] = 3;
        c.Frets[2] = 2;
        c.Frets[4] = 1;
        c.Fingers[1] = 3;
        c.Fingers[2] = 2;
        c.Fingers[4] = 1;
        arrangement.ChordTemplates.Add(c);

        var dyad = new ChordTemplate { Id = 1, Name = "", Fingerless = true };
        dyad.Frets[1] = 5;
        dyad.Frets[2] = 7;
        arrangement.ChordTemplates.Add(dyad);

        var level = new ChartLevel { DifficultyIndex = 0 };
        var split = Chord(project, 2000, 0, (1, 3), (2, 2), (4, 1));
        split.SplitIntoNotes = true;
        level.Chords.Add(split);
        var tremoloDyad = Chord(project, 3000, 1, (1, 5), (2, 7));
        // Tremolo on every string: an EOF tremolo phrase over the chord.
        foreach (var note in tremoloDyad.ChordNotes) note.Tremolo = true;
        level.Chords.Add(tremoloDyad);
        level.Chords.Add(Chord(project, 4000, 0, (1, 3), (2, 2), (4, 1)));
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static Chord Chord(ChartProject project, double ms, int templateId, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = TimelineMath.PositionFromMs(ms, project.Beats), TemplateId = templateId };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static ChartProject Import(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-split-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, bytes);
        try
        {
            var importer = new EofImporter();
            var tracks = importer.GetAvailableTracks(path);
            var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
            // The project the app builds from an import (EofProjectCreator.BuildProject).
            var project = new ChartProject();
            foreach (var arrangement in result.Arrangements) project.Arrangements.Add(arrangement);
            foreach (var marker in EofImporter.BuildBeatMarkers(result.Metadata)) project.Beats.Add(marker);
            return project;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static long Ms(ChartProject project, Chord chord) => (long)Math.Round(chord.Position.GetPositionMs(project.Beats));

    private static ChordTemplate TemplateOf(Arrangement arrangement, Chord chord) =>
        arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);

    private static string Describe(EofConversionReport report) =>
        string.Join("; ", report.Notes.Select(n => $"{n.Kind} {n.Subject}: {n.Message}"));
}
