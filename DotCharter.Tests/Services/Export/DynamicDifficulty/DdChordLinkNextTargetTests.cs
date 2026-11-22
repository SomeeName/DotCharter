using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     #876: a two-note chord whose string 3 rings into a single note and whose string 4 does not
///     link to anything. The chart is right (only string 3 carries the link) and the plain export
///     writes it that way; DLC Builder's steps read the XML back, and the read-back used to copy the
///     chord-level <c>linkNext</c> (set because one chord note links) onto EVERY chord note, so the
///     generated levels shipped a link on string 4 with no target note (DLC Builder I08).
/// </summary>
public sealed class DdChordLinkNextTargetTests
{
    private const int AudioMs = 60000;
    private const int ChordMs = 10000;
    private const int LandingMs = 10124;
    private const int LateNoteMs = 14000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_chord_note_that_continues_into_a_note_keeps_its_link_on_every_level(bool applyFixes)
    {
        var project = Chart();

        var built = DlcBuilderBuildSteps.Apply(project, project.Arrangements[0], AudioMs,
            new DlcBuilderSteps(applyFixes, true));

        Assert.NotNull(built);
        Assert.True(built.Levels.Count > 1, $"{built.Levels.Count} level(s) generated");
        var xml = new RsXmlSongWriter().Write(new ChartProject { Beats = project.Beats, Arrangements = [built] }, AudioMs);
        var checkedLevels = 0;
        foreach (var level in xml.Root!.Element("levels")!.Elements("level"))
        {
            var chord = level.Element("chords")!.Elements("chord").FirstOrDefault(c => Ms((string)c.Attribute("time")!) == ChordMs);
            if (chord == null) continue;
            var noteByString = chord.Elements("chordNote").ToDictionary(n => (int)n.Attribute("string")!);
            if (noteByString.Count < 2) continue;
            checkedLevels++;
            Assert.NotNull(noteByString[3].Attribute("linkNext"));
            Assert.Null(noteByString[4].Attribute("linkNext"));
            Assert.NotNull(chord.Attribute("linkNext"));
        }

        Assert.True(checkedLevels > 0, "no generated level kept the two-note chord");
        AssertEveryChordLinkHasATarget(xml);
    }

    [Fact]
    public void The_plain_export_links_only_the_string_that_continues()
    {
        var project = Chart();
        var xml = new RsXmlSongWriter().Write(project, AudioMs);

        AssertEveryChordLinkHasATarget(xml);
        var chord = xml.Root!.Descendants("chord").Single(c => Ms((string)c.Attribute("time")!) == ChordMs);
        Assert.NotNull(chord.Attribute("linkNext"));
        Assert.Null(chord.Elements("chordNote").Single(n => (int)n.Attribute("string")! == 4).Attribute("linkNext"));
    }

    [Fact]
    public void A_chord_level_link_alone_still_links_every_chord_note_on_import()
    {
        var project = Chart();
        var xml = new RsXmlSongWriter().Write(project, AudioMs);
        foreach (var n in xml.Root!.Descendants("chordNote")) n.Attribute("linkNext")?.Remove();

        var read = new RsXmlSongReader().ReadDocument(xml, "chord-level-link.xml");

        var chord = read.Arrangements[0].Levels[0].Chords.Single();
        Assert.All(chord.ChordNotes, n => Assert.True(n.LinkNext));
    }

    /// <summary>DLC Builder's <c>checkLinkNext</c> for chord notes: the next note on the string must start within 1 ms.</summary>
    private static void AssertEveryChordLinkHasATarget(XDocument xml)
    {
        foreach (var level in xml.Root!.Element("levels")!.Elements("level"))
        {
            var notes = level.Element("notes")!.Elements("note").ToList();
            foreach (var chord in level.Element("chords")!.Elements("chord"))
            {
                var chordMs = Ms((string)chord.Attribute("time")!);
                foreach (var cn in chord.Elements("chordNote").Where(n => n.Attribute("linkNext") != null))
                {
                    var str = (int)cn.Attribute("string")!;
                    var endMs = chordMs + Ms((string?)cn.Attribute("sustain") ?? "0");
                    var next = notes.Where(n => (int)n.Attribute("string")! == str && Ms((string)n.Attribute("time")!) > chordMs)
                        .OrderBy(n => Ms((string)n.Attribute("time")!)).FirstOrDefault();
                    Assert.True(next != null && Ms((string)next.Attribute("time")!) - endMs <= 1,
                        $"chord at {chordMs} ms string {str}: linkNext without a note at {endMs} ms (level {(string)level.Attribute("difficulty")!})");
                }
            }
        }
    }

    private static ChartProject Chart()
    {
        var project = new ChartProject();
        for (var i = 0; i < 130; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Phrases.AddRange(["p0", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(120), 1));
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "X", Frets = new() { [3] = 12, [4] = 13 }, Fingers = new() { [3] = 1, [4] = 2 }
        });
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        for (var ms = 3000; ms < 40000; ms += 250)
        {
            if (ms is >= ChordMs - 250 and <= LateNoteMs) continue;
            AddNote(project, level, ms, ms / 250 % 3, 3 + ms / 250 % 4, 0);
        }

        var chord = new Chord { Position = At(project, ChordMs), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 3, Fret = 12, Sustain = At(project, LandingMs) - At(project, ChordMs), LinkNext = true
        });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 4, Fret = 13, Sustain = At(project, LandingMs - 40) - At(project, ChordMs)
        });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape { Position = chord.Position, EndPosition = At(project, LandingMs), TemplateId = 0 });
        AddNote(project, level, LandingMs, 3, 12, 300);
        AddNote(project, level, LateNoteMs, 4, 13, 0);
        var sorted = level.Notes.OrderBy(n => n.Position).ToList();
        level.Notes.Clear();
        foreach (var n in sorted) level.Notes.Add(n);
        return project;
    }

    private static FractionalPosition At(ChartProject project, int ms) =>
        TimelineMath.PositionFromImportedMs(ms, project.Beats);

    private static void AddNote(ChartProject project, ChartLevel level, int ms, int stringIndex, int fret, int sustainMs) =>
        level.Notes.Add(new GuitarNote
        {
            Position = At(project, ms), StringIndex = stringIndex, Fret = fret,
            Sustain = sustainMs == 0 ? FractionalPosition.Zero : At(project, ms + sustainMs) - At(project, ms)
        });

    private static int Ms(string seconds) =>
        (int)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * 1000.0);
}
