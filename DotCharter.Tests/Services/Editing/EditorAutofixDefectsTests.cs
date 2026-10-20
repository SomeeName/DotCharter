namespace DotCharter.Tests.Services.Editing;

/// <summary>
/// #284: defects in the auto-fixes, each with the rule that keeps it fixed.
/// </summary>
public sealed class EditorAutofixDefectsTests
{
    private static List<BeatMarker> Beats(int count = 24) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static AppConfig Config() => new();

    /// <summary>
    /// Improve Chart cleared every link on a chord where only some notes were
    /// linked, and no rule reported it. A chord note linked into the next note on
    /// its string, its sustain reaching that note, is valid authoring.
    /// </summary>
    [Fact]
    public void ImproveChart_KeepsAValidPartialChordLink()
    {
        var project = new ChartProject();
        foreach (var b in Beats()) project.Beats.Add(b);
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7, Sustain = new FractionalPosition(1), LinkNext = true });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 2, Fret = 7, Sustain = new FractionalPosition(1) });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.True(chord.ChordNotes[1].LinkNext);
    }

    /// <summary>
    /// EnsureEndPhrase read only EventPoint phrases, and an imported chart keeps
    /// its phrases in PhraseIterations, so it never got an END phrase.
    /// </summary>
    [Fact]
    public void ImproveChart_AddsEndToAnImportedChartWhosePhrasesAreIterations()
    {
        var project = new ChartProject();
        foreach (var b in Beats()) project.Beats.Add(b);
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("verse");
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        Assert.DoesNotContain(arrangement.EventPoints, e => !string.IsNullOrWhiteSpace(e.Phrase));

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.Contains(PhraseMarkers.All(arrangement),
            m => string.Equals(m.Name, "END", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Auto-fix raises a slide target below the capo to the capo, but the
    /// inspector never reported one, so Fix changed something the editor never
    /// showed. L01 now covers a slide target too, on notes and chord notes.
    /// </summary>
    [Fact]
    public void TheInspector_ReportsASlideTargetBelowTheCapo()
    {
        var arrangement = new Arrangement { Capo = 3 };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), Sustain = new FractionalPosition(1), StringIndex = 2,
            Fret = 5, SlideTo = 1, LinkNext = true
        });
        var chord = new Chord { Position = new FractionalPosition(6) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, SlideTo = 2, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);

        var issues = new ArrangementValidationService(new FakeAppLogger())
            .Validate(arrangement, Beats())
            .Where(i => i.Type == ArrangementIssueType.NoteFretBelowCapo)
            .ToList();

        Assert.Contains(issues, i => i.Message.Contains("Slide target fret 1", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("Slide target fret 2", StringComparison.Ordinal));
    }
}
