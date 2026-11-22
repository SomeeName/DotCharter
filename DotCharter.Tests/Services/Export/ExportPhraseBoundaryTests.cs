using System.Globalization;
using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #639: the XML writer, the SNG mapper and the phrase ceilings read ONE resolved
///     iteration table, so the time a file writes for an iteration, the content that
///     iteration owns and the maximum of its phrase cannot disagree, also for content in
///     the last millisecond before a boundary and for iterations clipped into the song.
/// </summary>
public sealed class ExportPhraseBoundaryTests
{
    [Fact]
    public void Sng_END_clipped_onto_a_level_two_note_leaves_the_note_to_the_phrase_before_it()
    {
        // Row 1 of the issue: as before the song-end clip, the note belongs to p0 with its
        // maximum 2 and heroes [0,1,2]; END is written clipped to the song length but owns
        // nothing, as in the XML, which writes END on its beat.
        var project = LastMillisecondChart(authoredMaxima: true);

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 0).Sng;

        var note = Assert.Single(sng.Levels.Single(level => level.Difficulty == 2).Notes);
        var iteration = sng.PhraseIterations[note.PhraseIterationId];
        Assert.Equal(7.499f, sng.MetaData.SongLength);
        Assert.Equal(7.499f, note.Time);
        Assert.Equal(7.499f, sng.PhraseIterations[^1].StartTime);
        Assert.Equal("p0", sng.Phrases[iteration.PhraseId].Name);
        Assert.Equal(2, sng.Phrases[iteration.PhraseId].MaxDifficulty);
        Assert.Equal([0, 1, 2], iteration.Difficulty);
        AssertNotesWithinPhraseMaximum(sng);
    }

    [Fact]
    public void Xml_writes_the_iteration_time_its_ceilings_were_computed_with()
    {
        // Row 2: the ceilings are computed with END where the file writes it, on its beat
        // one millisecond past the song length, so p0 owns the level-2 note and says 2.
        var project = LastMillisecondChart(authoredMaxima: false);

        var xml = new RsXmlSongWriter().Write(project);

        Assert.Equal("7.499", xml.Root!.Element("songLength")!.Value);
        Assert.Equal(["0.000", "7.500"], xml.Descendants("phraseIteration").Select(i => (string)i.Attribute("time")!));
        Assert.Equal([("p0", 2), ("END", 0)],
            xml.Descendants("phrase").Select(p => ((string)p.Attribute("name")!, (int)p.Attribute("maxDifficulty")!)));
        AssertNotesWithinPhraseMaximum(xml);
    }

    public static TheoryData<string, bool, int> BoundaryCases
    {
        get
        {
            var cases = new TheoryData<string, bool, int>();
            foreach (var chart in new[] { "last-millisecond", "every-boundary", "sub-millisecond" })
            {
                foreach (var authored in new[] { false, true })
                {
                    // Audio length unknown, longer than the chart, and ending inside the third phrase.
                    foreach (var audioLengthMs in new[] { 0, 600000, 5000 }) cases.Add(chart, authored, audioLengthMs);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Every_emitted_note_is_at_or_below_the_maximum_of_its_iterations_phrase(string chart, bool authoredMaxima,
        int audioLengthMs)
    {
        var project = Chart(chart, authoredMaxima);

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, audioLengthMs).Sng;
        var xml = new RsXmlSongWriter().Write(project, audioLengthMs > 0 ? audioLengthMs : null);

        AssertNotesWithinPhraseMaximum(sng);
        AssertNotesWithinPhraseMaximum(xml);
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Xml_and_sng_agree_on_iterations_phrase_maxima_and_note_owners(string chart, bool authoredMaxima,
        int audioLengthMs)
    {
        var project = Chart(chart, authoredMaxima);

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, audioLengthMs).Sng;
        var xml = new RsXmlSongWriter().Write(project, audioLengthMs > 0 ? audioLengthMs : null);

        // The SNG writes the XML's iteration times clipped into the song.
        var songLengthMs = Ms(sng.MetaData.SongLength);
        Assert.Equal(sng.PhraseIterations.Select(i => (Ms(i.StartTime), i.PhraseId)),
            xml.Descendants("phraseIteration").Select(i =>
                (Math.Min(Ms((string)i.Attribute("time")!), songLengthMs), (int)i.Attribute("phraseId")!)));
        Assert.Equal(sng.Phrases.Select(p => (p.Name, p.MaxDifficulty)),
            xml.Descendants("phrase").Select(p => ((string)p.Attribute("name")!, (int)p.Attribute("maxDifficulty")!)));
        Assert.Equal(songLengthMs, Ms(xml.Root!.Element("songLength")!.Value));
        Assert.Equal(NoteOwners(xml), sng.Levels.SelectMany(level => level.Notes.Select(note =>
            (level.Difficulty, Ms(note.Time), sng.PhraseIterations[note.PhraseIterationId].PhraseId))).Order());
    }

    /// <summary>Every XML note as (level, time, phrase of the last iteration written at or
    /// before it), in order.</summary>
    private static List<(int Level, int TimeMs, int PhraseId)> NoteOwners(XDocument xml)
    {
        var song = xml.Root!;
        var iterations = song.Element("phraseIterations")!.Elements("phraseIteration")
            .Select(i => (Time: Ms((string)i.Attribute("time")!), PhraseId: (int)i.Attribute("phraseId")!))
            .OrderBy(i => i.Time).ToList();
        var owners = new List<(int, int, int)>();
        foreach (var level in song.Element("levels")!.Elements("level"))
        {
            foreach (var time in level.Element("notes")!.Elements("note").Select(note => Ms((string)note.Attribute("time")!)))
            {
                var id = iterations.Count - 1;
                while (id > 0 && iterations[id].Time > time) id--;
                owners.Add(((int)level.Attribute("difficulty")!, time, iterations[id].PhraseId));
            }
        }

        return [.. owners.Order()];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void A_phrase_is_raised_only_by_content_its_own_iterations_own(int audioLengthMs)
    {
        // "sub-millisecond": a level-two note 0.4 ms before B's marker is written at B's
        // millisecond, so B owns it in the file. A must not be raised for it, and with
        // authored maxima of zero its copies below level two do not exist.
        var project = Chart("sub-millisecond", authoredMaxima: true);

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, audioLengthMs).Sng;

        AssertPhraseMaximaAreExactlyTheOwnedContent(sng);
    }

    internal static void AssertNotesWithinPhraseMaximum(RocksmithSng sng)
    {
        foreach (var level in sng.Levels)
        {
            foreach (var note in level.Notes)
            {
                Assert.InRange(note.PhraseIterationId, 0, sng.PhraseIterations.Count - 1);
                var phrase = sng.Phrases[sng.PhraseIterations[note.PhraseIterationId].PhraseId];
                Assert.True(phrase.MaxDifficulty >= level.Difficulty,
                    $"SNG level {level.Difficulty} note at {note.Time} s is in phrase {phrase.Name} with maximum {phrase.MaxDifficulty}");
            }
        }
    }

    internal static void AssertPhraseMaximaAreExactlyTheOwnedContent(RocksmithSng sng)
    {
        AssertNotesWithinPhraseMaximum(sng);
        for (var id = 0; id < sng.Phrases.Count; id++)
        {
            var owned = sng.Levels.Where(level => level.Notes.Any(note =>
                    sng.PhraseIterations[note.PhraseIterationId].PhraseId == id))
                .Select(level => level.Difficulty).DefaultIfEmpty(0).Max();
            Assert.True(owned == sng.Phrases[id].MaxDifficulty,
                $"phrase {sng.Phrases[id].Name}: maximum {sng.Phrases[id].MaxDifficulty}, highest owned level {owned}");
        }
    }

    /// <summary>The same invariant read off the FILE: a note belongs to the last
    /// iteration written at or before it (the first one when it is earlier).</summary>
    internal static void AssertNotesWithinPhraseMaximum(XDocument xml)
    {
        var song = xml.Root!;
        var phrases = song.Element("phrases")!.Elements("phrase")
            .Select(p => (Name: (string)p.Attribute("name")!, Max: (int)p.Attribute("maxDifficulty")!)).ToList();
        var iterations = song.Element("phraseIterations")!.Elements("phraseIteration")
            .Select(i => (Time: Ms((string)i.Attribute("time")!), PhraseId: (int)i.Attribute("phraseId")!))
            .OrderBy(i => i.Time).ToList();
        foreach (var level in song.Element("levels")!.Elements("level"))
        {
            var difficulty = (int)level.Attribute("difficulty")!;
            var sounds = level.Element("notes")!.Elements("note").Concat(level.Element("chords")!.Elements("chord"));
            foreach (var time in sounds.Select(sound => Ms((string)sound.Attribute("time")!)))
            {
                var id = iterations.Count - 1;
                while (id > 0 && iterations[id].Time > time) id--;
                var phrase = phrases[iterations[id].PhraseId];
                Assert.True(phrase.Max >= difficulty,
                    $"XML level {difficulty} sound at {time} ms is in phrase {phrase.Name} with maxDifficulty {phrase.Max}");
            }
        }
    }

    internal static ChartProject Chart(string name, bool authoredMaxima) => name switch
    {
        "last-millisecond" => LastMillisecondChart(authoredMaxima),
        "every-boundary" => EveryBoundaryChart(authoredMaxima),
        "sub-millisecond" => SubMillisecondChart(authoredMaxima),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    /// <summary>The issue's chart: 500 ms beats through 7.500 s, levels 0-2, one level-2
    /// note at 7.499 s, <c>p0</c> at 0 and <c>END</c> on the last beat.</summary>
    internal static ChartProject LastMillisecondChart(bool authoredMaxima)
    {
        var project = Project(["p0", "END"], [0, 15]);
        var arrangement = project.Arrangements[0];
        if (authoredMaxima)
        {
            arrangement.PhraseInfos["p0"] = new PhraseInfo(2, false);
            arrangement.PhraseInfos["END"] = new PhraseInfo(0, false);
        }

        AddNote(project, 2, 7499);
        return project;
    }

    /// <summary>Four phrases; every level has a note on each boundary and one millisecond
    /// before it, at a different level per boundary.</summary>
    internal static ChartProject EveryBoundaryChart(bool authoredMaxima)
    {
        var project = Project(["A", "B", "C", "END"], [0, 4, 8, 15]);
        var arrangement = project.Arrangements[0];
        if (authoredMaxima)
        {
            foreach (var phrase in arrangement.Phrases) arrangement.PhraseInfos[phrase] = new PhraseInfo(0, false);
        }

        AddNote(project, 0, 1000);
        AddNote(project, 2, 1999);
        AddNote(project, 1, 2000);
        AddNote(project, 1, 3999);
        AddNote(project, 2, 4000);
        AddNote(project, 2, 7499);
        return project;
    }

    /// <summary>A level-two note 0.4 ms before the marker of <c>B</c>: both are written at
    /// 2.000 s, so the file gives the note to <c>B</c>.</summary>
    internal static ChartProject SubMillisecondChart(bool authoredMaxima)
    {
        var project = Project(["A", "B", "END"], [0, 4, 15]);
        var arrangement = project.Arrangements[0];
        if (authoredMaxima)
        {
            foreach (var phrase in arrangement.Phrases) arrangement.PhraseInfos[phrase] = new PhraseInfo(0, false);
        }

        AddNote(project, 0, 1000);
        AddNote(project, 0, 3000);
        // 3 + 1249/1250 beats = 1999.6 ms, which rounds to B's 2000.
        arrangement.Levels[2].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3, new Fraction(1249, 1250)), StringIndex = 0, Fret = 3
        });
        return project;
    }

    private static ChartProject Project(string[] phrases, int[] beatsOfIterations)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Phrases.AddRange(phrases);
        for (var i = 0; i < beatsOfIterations.Length; i++)
            arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(beatsOfIterations[i]), i));
        for (var difficulty = 0; difficulty <= 2; difficulty++)
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = difficulty });
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static void AddNote(ChartProject project, int level, int timeMs) =>
        project.Arrangements[0].Levels[level].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromImportedMs(timeMs, project.Beats), StringIndex = 0, Fret = 3
        });

    private static int Ms(float seconds) => (int)Math.Round(seconds * 1000.0);

    private static int Ms(string seconds) =>
        (int)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * 1000.0);
}
