using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     #639 review: a note the song length, END and the first marker leave outside every
///     phrase range still reaches every generated difficulty level, in the chart, in the
///     XML and in the SNG. A single-level chart, audio 180 s (song length 179.999 s), END on
///     the beat at 184 s, and the note after the song length, after END or before the first
///     phrase.
/// </summary>
public sealed class DdContentPastSongEndTests
{
    private const int AudioMs = 180000;
    private const int EndBeat = 368;

    [Theory]
    [InlineData(180500, 0)]
    [InlineData(185000, 0)]
    [InlineData(500, 4)]
    public void The_note_is_in_every_generated_level_and_in_both_files(int noteMs, int firstPhraseBeat)
    {
        var project = Chart(noteMs, firstPhraseBeat);

        var built = DlcBuilderBuildSteps.Apply(project, project.Arrangements[0], AudioMs, new DlcBuilderSteps(false, true));

        Assert.NotNull(built);
        Assert.True(built.Levels.Count > 1, $"{built.Levels.Count} level(s) generated");
        foreach (var level in built.Levels)
            Assert.Contains(level.Notes, note => Ms(note.Position.GetPositionMs(project.Beats)) == noteMs);

        var output = new ChartProject { Beats = project.Beats, Arrangements = [built] };
        var xml = new RsXmlSongWriter().Write(output, AudioMs);
        var (xmlLevels, xmlMax) = XmlLevelsHolding(xml, noteMs);
        Assert.Equal(Enumerable.Range(0, xmlMax + 1), xmlLevels);

        var sng = SngFromDomainMapper.BuildInstrumental(built, project.Beats, AudioMs).Sng;
        var sngLevels = sng.Levels.Where(level => level.Notes.Any(note => Ms(note.Time * 1000.0) == noteMs)).ToList();
        Assert.NotEmpty(sngLevels);
        var sngNote = sngLevels[^1].Notes.First(note => Ms(note.Time * 1000.0) == noteMs);
        var sngMax = sng.Phrases[sng.PhraseIterations[sngNote.PhraseIterationId].PhraseId].MaxDifficulty;
        Assert.Equal(Enumerable.Range(0, sngMax + 1), sngLevels.Select(level => level.Difficulty));
        Assert.Equal(xmlMax, sngMax);
        ExportPhraseBoundaryTests.AssertNotesWithinPhraseMaximum(sng);
        ExportPhraseBoundaryTests.AssertNotesWithinPhraseMaximum(xml);
    }

    /// <summary>The levels whose notes include one at <paramref name="noteMs" />, and the
    /// maximum of the phrase the file gives it to (the last iteration written at or before it).</summary>
    private static (List<int> Levels, int Max) XmlLevelsHolding(XDocument xml, int noteMs)
    {
        var song = xml.Root!;
        var iterations = song.Element("phraseIterations")!.Elements("phraseIteration")
            .Select(i => (Time: Ms((string)i.Attribute("time")!), PhraseId: (int)i.Attribute("phraseId")!))
            .OrderBy(i => i.Time).ToList();
        var owner = iterations.Count - 1;
        while (owner > 0 && iterations[owner].Time > noteMs) owner--;
        var max = (int)song.Element("phrases")!.Elements("phrase").ElementAt(iterations[owner].PhraseId).Attribute("maxDifficulty")!;
        var levels = song.Element("levels")!.Elements("level")
            .Where(level => level.Element("notes")!.Elements("note").Any(note => Ms((string)note.Attribute("time")!) == noteMs))
            .Select(level => (int)level.Attribute("difficulty")!).Order().ToList();
        return (levels, max);
    }

    private static ChartProject Chart(int noteMs, int firstPhraseBeat)
    {
        var project = new ChartProject();
        for (var i = 0; i <= EndBeat + 1; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Phrases.AddRange(["p0", "p1", "p2", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(firstPhraseBeat), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(180), 1));
        // p2 holds nothing but the note when it lies past the song length, so no level may
        // leave it out for being the phrase's easiest sound to drop.
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(350), 2));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(EndBeat), 3));
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        // A riff of eighth notes in every phrase, so each gets several generated levels.
        if (noteMs < 3000) AddNote(project, level, noteMs, 2, 5);
        for (var ms = 3000; ms < 170000; ms += 250)
            AddNote(project, level, ms, ms / 250 % 3, 3 + (ms / 250 % 4));
        if (noteMs >= 170000) AddNote(project, level, noteMs, 2, 5);
        return project;
    }

    private static void AddNote(ChartProject project, ChartLevel level, int ms, int stringIndex, int fret) =>
        level.Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromImportedMs(ms, project.Beats), StringIndex = stringIndex, Fret = fret
        });

    private static int Ms(double ms) => (int)Math.Round(ms);

    private static int Ms(string seconds) =>
        (int)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * 1000.0);
}
