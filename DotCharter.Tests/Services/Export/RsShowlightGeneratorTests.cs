using System.Text;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The generated show-light set (issue #875): fog per section, beams from the hardest difficulty's
///     pitch classes, lasers around the first solo, the safety lights, and the one generator both the
///     export fallback and the dialog's one-click fix use.
/// </summary>
public sealed class RsShowlightGeneratorTests
{
    private const double SongMs = 40_000;

    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 100; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        return project;
    }

    private static FractionalPosition Pos(ChartProject project, double ms) => TimelineMath.PositionFromMs(ms, project.Beats);

    private static Arrangement AddArrangement(ChartProject project, ArrangementType type, int strings = 6, params (double Ms, string Name)[] sections)
    {
        var arrangement = new Arrangement { ArrangementType = type, StringCount = strings, Name = type + project.Arrangements.Count.ToString() };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        foreach (var (ms, name) in sections)
            arrangement.EventPoints.Add(new EventPoint { Position = Pos(project, ms), Section = name });
        project.Arrangements.Add(arrangement);
        return arrangement;
    }

    private static void Note(ChartProject project, Arrangement arrangement, double ms, int stringIndex, int fret, int level = 0)
    {
        var chartLevel = arrangement.Levels.First(l => l.DifficultyIndex == level);
        chartLevel.Notes.Add(new GuitarNote { Position = Pos(project, ms), StringIndex = stringIndex, Fret = fret });
    }

    private static List<(double Ms, string Type)> Lane(ChartProject project, IReadOnlyList<Showlight> lights, ShowlightLane lane)
        => lights.Where(l => ShowlightCatalog.TryGetLane(l.Type, l.Note, out var found) && found == lane)
            .Select(l => (Math.Round(TimelineMath.CalculateTimeMs(l.Position, project.Beats)), l.Type)).ToList();

    private static IReadOnlyList<Showlight> Generate(ChartProject project, double songMs = SongMs) => RsShowlightGenerator.Generate(project, songMs);

    [Fact]
    public void Fog_ChangesAtEachSectionStart_SameNameSameColour_NeighboursDiffer()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "intro"), (5000, "verse"), (10_000, "chorus"), (15_000, "verse"), (20_000, "chorus"));

        var fog = Lane(project, Generate(project), ShowlightLane.Fog);

        Assert.Equal([0, 5000, 10_000, 15_000, 20_000, SongMs], fog.Select(f => f.Ms));
        Assert.Equal(fog[1].Type, fog[3].Type);
        Assert.Equal(fog[2].Type, fog[4].Type);
        for (var i = 1; i < fog.Count; i++) Assert.NotEqual(fog[i - 1].Type, fog[i].Type);
    }

    [Fact]
    public void Fog_ConsecutiveSectionsWithTheSameName_AreOneChange()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "verse"), (5000, "verse"), (10_000, "chorus"));

        var fog = Lane(project, Generate(project), ShowlightLane.Fog);

        Assert.Equal([0, 10_000, SongMs], fog.Select(f => f.Ms));
    }

    [Fact]
    public void Fog_IsDeterministic_AndDerivedFromTheSectionNameNotRandom()
    {
        var a = NewProject();
        AddArrangement(a, ArrangementType.Lead, 6, (0, "verse"), (5000, "chorus"), (10_000, "bridge"));
        var b = NewProject();
        AddArrangement(b, ArrangementType.Lead, 6, (0, "verse"), (5000, "chorus"), (10_000, "bridge"));

        static string Dump(ChartProject p) => string.Join(";", Generate(p).Select(l => $"{l.Type}@{l.Position}"));

        Assert.Equal(Dump(a), Dump(a));
        Assert.Equal(Dump(a), Dump(b));
        // Pinned: a regression to a different derivation (or to Random) changes the first light.
        Assert.Equal("FOG_YELLOW", Lane(a, Generate(a), ShowlightLane.Fog)[0].Type);
    }

    [Fact]
    public void Beams_FollowThePitchClass_ChangingOnlyAfter350Ms()
    {
        var project = NewProject();
        var lead = AddArrangement(project, ArrangementType.Lead);
        Note(project, lead, 0, 0, 0);      // E  -> BEAMS_BLUE (52)
        Note(project, lead, 100, 0, 1);    // F, only 100 ms later: no change
        Note(project, lead, 400, 0, 1);    // F, 400 ms after the last change: change
        Note(project, lead, 800, 0, 1);    // F again: no change
        Note(project, lead, 1500, 0, 0);   // E again: change

        var beams = Lane(project, Generate(project), ShowlightLane.Beams);

        Assert.Equal([(0.0, "BEAMS_BLUE"), (401.0, "BEAMS_YELLOW"), (1500.0, "BEAMS_BLUE")], beams);
    }

    [Fact]
    public void Beams_UseTheHardestDifficultyOnly()
    {
        var project = NewProject();
        var lead = AddArrangement(project, ArrangementType.Lead);
        lead.Levels.Add(new ChartLevel { DifficultyIndex = 2 });
        Note(project, lead, 0, 0, 0, level: 0);   // would be BLUE
        Note(project, lead, 0, 0, 1, level: 2);   // F -> YELLOW

        var beams = Lane(project, Generate(project), ShowlightLane.Beams);

        Assert.Equal([(0.0, "BEAMS_YELLOW")], beams);
    }

    [Fact]
    public void Beams_AChordUsesItsFirstSoundingNote()
    {
        var project = NewProject();
        var lead = AddArrangement(project, ArrangementType.Lead);
        var chord = new Chord { Position = Pos(project, 0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });   // B: pc 11
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });   // E, the lowest string: pc 4
        lead.Levels[0].Chords.Add(chord);

        var beams = Lane(project, Generate(project), ShowlightLane.Beams);

        Assert.Equal([(0.0, "BEAMS_BLUE")], beams);
    }

    [Fact]
    public void Beams_RespectTheCapoAndTheTuning()
    {
        var project = NewProject();
        var lead = AddArrangement(project, ArrangementType.Lead);
        lead.Capo = 2;                                // open E sounds F#: pc 6
        Note(project, lead, 0, 0, 0);
        Assert.Equal([(0.0, "BEAMS_MAGENTA")], Lane(project, Generate(project), ShowlightLane.Beams));   // 48 + 6

        lead.Capo = 0;
        lead.TuningPitches = [new TuningOffset(-2), new TuningOffset(0), new TuningOffset(0), new TuningOffset(0), new TuningOffset(0), new TuningOffset(0)];
        Assert.Equal([(0.0, "BEAMS_LIGHT_BLUE")], Lane(project, Generate(project), ShowlightLane.Beams));   // D: 48 + 2
    }

    [Fact]
    public void Beams_ExtendedRange_SevenStringGuitarAndFiveStringBass()
    {
        var seven = NewProject();
        var guitar = AddArrangement(seven, ArrangementType.Lead, 7);
        guitar.TuningPitches = [.. Enumerable.Range(0, 7).Select(_ => new TuningOffset(0))];
        Note(seven, guitar, 0, 0, 0);                 // low B: pc 11 -> 59
        Assert.Equal(["BEAMS_PURPLE_PLUS"], Lane(seven, Generate(seven), ShowlightLane.Beams).Select(b => b.Type));

        var five = NewProject();
        var bass = AddArrangement(five, ArrangementType.Bass, 5);
        bass.TuningPitches = [.. ExtendedRangeTunings.StandardOffsets(5, true).Select(o => new TuningOffset(o))];
        Note(five, bass, 0, 0, 0);                    // low B again
        Assert.Equal(["BEAMS_PURPLE_PLUS"], Lane(five, Generate(five), ShowlightLane.Beams).Select(b => b.Type));
    }

    [Fact]
    public void Lasers_TurnOnAtTheFirstSolo_AndOffFiveSecondsBeforeTheEnd()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "verse"), (12_000, "Solo"), (30_000, "solo"));

        var lasers = Lane(project, Generate(project), ShowlightLane.Lasers);

        Assert.Equal([(12_000.0, "LASERS_ON"), (SongMs - 5000, "LASERS_OFF")], lasers);
    }

    [Fact]
    public void Lasers_WithoutASolo_TurnOnAtSixtyPercent()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "verse"));

        var lasers = Lane(project, Generate(project), ShowlightLane.Lasers);

        Assert.Equal([(SongMs * 0.6, "LASERS_ON"), (SongMs - 5000, "LASERS_OFF")], lasers);
    }

    [Fact]
    public void Safety_NoSectionsAndNoNotes_StillHasFogAndBeam_AndAFinalFogAtTheEnd()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead);

        var lights = Generate(project);
        var fog = Lane(project, lights, ShowlightLane.Fog);

        Assert.Single(Lane(project, lights, ShowlightLane.Beams));
        Assert.Equal(2, fog.Count);
        Assert.Equal(0.0, fog[0].Ms);
        Assert.Equal(SongMs, fog[1].Ms);
        Assert.NotEqual(fog[0].Type, fog[1].Type);
    }

    [Fact]
    public void Safety_TheFinalFogDiffersFromTheOneBeforeIt()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "verse"), (5000, "chorus"));

        var fog = Lane(project, Generate(project), ShowlightLane.Fog);

        Assert.NotEqual(fog[^2].Type, fog[^1].Type);
        Assert.Equal(SongMs, fog[^1].Ms);
    }

    [Fact]
    public void Source_IsTheLeadArrangement_ElseTheFirstInstrumental()
    {
        var project = NewProject();
        var rhythm = AddArrangement(project, ArrangementType.Rhythm, 6, (0, "verse"));
        var lead = AddArrangement(project, ArrangementType.Lead, 6, (0, "intro"), (7000, "chorus"));
        Note(project, rhythm, 0, 0, 0);
        Note(project, lead, 0, 0, 1);

        var lights = Generate(project);
        Assert.Contains(7000.0, Lane(project, lights, ShowlightLane.Fog).Select(f => f.Ms));
        Assert.Equal(["BEAMS_YELLOW"], Lane(project, lights, ShowlightLane.Beams).Select(b => b.Type));

        project.Arrangements.Remove(lead);
        Assert.Equal(["BEAMS_BLUE"], Lane(project, Generate(project), ShowlightLane.Beams).Select(b => b.Type));
    }

    [Fact]
    public void NoInstrumentalArrangement_FallsBackToTheMinimalSet()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Vocal);

        var lights = RsDefaultShowlights.For(project, SongMs);

        Assert.Equal(["FOG_BLUE", "BEAMS_BLUE", "LASERS_ON"], lights.Select(l => l.Type));
    }

    [Fact]
    public void UnknownSongLength_IsDerivedFromTheLastBeat()
    {
        var project = NewProject();
        AddArrangement(project, ArrangementType.Lead, 6, (0, "verse"));

        var fog = Lane(project, Generate(project, 0), ShowlightLane.Fog);

        Assert.Equal(99 * 500.0, fog[^1].Ms);
    }

    [Fact]
    public void Export_FallbackWritesTheGeneratedSet_AndKeepsHandMadeLights()
    {
        var project = NewProject();
        var lead = AddArrangement(project, ArrangementType.Lead, 6, (0, "intro"), (9000, "chorus"));
        Note(project, lead, 0, 0, 0);
        var warnings = new List<string>();

        var xml = Encoding.UTF8.GetString(RocksmithPackageBuilder.CreateShowlights(project, SongMs, warnings));

        Assert.Contains("generated from the sections and notes", Assert.Single(warnings));
        Assert.Contains("time=\"9.000\"", xml);
        Assert.Empty(project.Showlights);

        project.Showlights.Add(new Showlight { Position = Pos(project, 100), Type = "FOG_RED", Note = 32 });
        warnings.Clear();
        var kept = Encoding.UTF8.GetString(RocksmithPackageBuilder.CreateShowlights(project, SongMs, warnings));

        Assert.Empty(warnings);
        Assert.DoesNotContain("time=\"9.000\"", kept);
        Assert.Single(project.Showlights);
    }
}
