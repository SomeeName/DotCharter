using System.Xml.Linq;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     An accented strum repeated inside one hand shape ships as the game's repeat box: a <c>&lt;chord&gt;</c>
///     with no <c>&lt;chordNote&gt;</c> children that keeps its chord-level accent. The chord sets its accent
///     on itself alone and every chord note's own flag stays false, which is the state every importer and the
///     whole-chord toggle produce. Written through the real RS-XML writer, so the check is against the file.
/// </summary>
public sealed class AccentedRepeatChordExportTests
{
    private static readonly (int StringIndex, int Fret)[] Shape = [(4, 5), (5, 7)];

    [Fact]
    public void TheRepeatsOfAnAccentedChord_ShipWithoutChordNotes_AndKeepTheAccent()
    {
        var chords = ExportedChords(accentOnChord: true, stringAccent: false);

        Assert.Equal(3, chords.Count);
        Assert.Equal(2, chords[0].Elements("chordNote").Count());
        Assert.All(chords, c => Assert.Equal("1", c.Attribute("accent")?.Value));
        Assert.Empty(chords[1].Elements("chordNote"));
        Assert.Empty(chords[2].Elements("chordNote"));
    }

    /// <summary>The same strums with the accent on a string alone have no chord tag to carry it: full panels.</summary>
    [Fact]
    public void AnAccentOnAStringAlone_KeepsTheFullPanels()
    {
        var chords = ExportedChords(accentOnChord: false, stringAccent: true);

        Assert.Equal(3, chords.Count);
        Assert.All(chords, c => Assert.Equal(2, c.Elements("chordNote").Count()));
        Assert.All(chords, c => Assert.Equal(1, c.Elements("chordNote").Count(n => n.Attribute("accent")?.Value == "1")));
    }

    private static List<XElement> ExportedChords(bool accentOnChord, bool stringAccent)
    {
        var project = new ChartProject();
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        }

        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var template = new ChordTemplate { Id = 1, Name = "double stop" };
        foreach (var (stringIndex, fret) in Shape) template.Frets[stringIndex] = fret;
        arrangement.ChordTemplates.Add(template);
        arrangement.Phrases.AddRange(["COUNT", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(24), 1));

        var level = new ChartLevel { DifficultyIndex = 0 };
        foreach (var beat in new[] { 1, 2, 3 })
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 1, Accent = accentOnChord };
            foreach (var (stringIndex, fret) in Shape)
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = stringIndex,
                    Fret = fret,
                    Accent = stringAccent && stringIndex == 5
                });
            }

            level.Chords.Add(chord);
        }

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4), TemplateId = 1
        });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        // Nothing else in the level may ship as a repeat but these: the verdicts say what the file must hold.
        var verdicts = RepeatChords.Explain(arrangement.ChordTemplates, level.Notes, level.Chords, level.HandShapes, project.Beats);
        Assert.Equal(accentOnChord, verdicts[level.Chords[1]].Ships);

        var xml = new RsXmlSongWriter().Write(project, null);
        return [.. xml.Root!.Element("levels")!.Elements("level").Single().Element("chords")!.Elements("chord")];
    }
}
