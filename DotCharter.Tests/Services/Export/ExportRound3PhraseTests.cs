using DotCharter.Services.Export.Sng;

namespace DotCharter.Tests.Services.Export;

public sealed class ExportRound3PhraseTests
{
    [Fact]
    public void Content_before_first_explicit_iteration_keeps_mains_maximum_two_and_hero_levels_zero_one_two()
    {
        const int mainMaximum = 2;
        var project = FirstExplicitP0Chart();
        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 600000).Sng;
        var phrase = Assert.Single(sng.Phrases);
        var iteration = Assert.Single(sng.PhraseIterations);
        var note = Assert.Single(sng.Levels.Single(level => level.Difficulty == mainMaximum).Notes);

        Assert.Equal("p0", phrase.Name);
        Assert.Equal(0, note.PhraseIterationId);
        Assert.Equal(1f, note.Time);
        Assert.Equal(2f, iteration.StartTime);
        Assert.Equal(mainMaximum, phrase.MaxDifficulty);
        Assert.Equal([0, 1, mainMaximum], iteration.Difficulty);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Unsorted_explicit_iterations_derive_maximum_from_the_emitted_notes_owner(int noteBeat)
    {
        var project = FirstExplicitP0Chart();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.Add("later");
        arrangement.PhraseIterations.Insert(0, new PhraseIteration(new FractionalPosition(8), 1));
        arrangement.Levels[2].Notes[0].Position = new FractionalPosition(noteBeat);
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 600000).Sng;
        var note = Assert.Single(sng.Levels.Single(level => level.Difficulty == 2).Notes);
        var iteration = sng.PhraseIterations[note.PhraseIterationId];

        Assert.Equal(0, note.PhraseIterationId);
        Assert.Equal("p0", sng.Phrases[iteration.PhraseId].Name);
        Assert.Equal(2, sng.Phrases[iteration.PhraseId].MaxDifficulty);
        Assert.Equal([0, 1, 2], iteration.Difficulty);
        Assert.Equal(0, Assert.Single(sng.Phrases, phrase => phrase.Name == "later").MaxDifficulty);
    }

    [Fact]
    public void Clamped_iteration_ties_keep_source_order_for_negative_notes()
    {
        var project = FirstExplicitP0Chart();
        var arrangement = project.Arrangements[0];
        arrangement.Phrases.Add("later");
        arrangement.PhraseIterations[0].Position = new FractionalPosition(-2);
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(-4), 1));
        arrangement.Levels[2].Notes[0].Position = new FractionalPosition(-1);
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 600000).Sng;
        var note = Assert.Single(sng.Levels.Single(level => level.Difficulty == 2).Notes);

        Assert.All(sng.PhraseIterations, iteration => Assert.Equal(0f, iteration.StartTime));
        Assert.Equal(0, note.PhraseIterationId);
        Assert.Equal("p0", sng.Phrases[sng.PhraseIterations[0].PhraseId].Name);
        Assert.Equal(2, Assert.Single(sng.Phrases, phrase => phrase.Name == "p0").MaxDifficulty);
        Assert.Equal(0, Assert.Single(sng.Phrases, phrase => phrase.Name == "later").MaxDifficulty);
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("markers")]
    [InlineData("synthetic")]
    public void Note_after_last_iteration_end_raises_its_owning_END_phrase(string phraseSource)
    {
        var project = FinalBoundaryChart(phraseSource);
        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 600000).Sng;
        var note = Assert.Single(sng.Levels.Single(level => level.Difficulty == 2).Notes);
        var iteration = sng.PhraseIterations[note.PhraseIterationId];

        Assert.Equal(sng.PhraseIterations.Count - 1, note.PhraseIterationId);
        Assert.True(note.Time > iteration.EndTime);
        Assert.Equal("END", sng.Phrases[iteration.PhraseId].Name);
        Assert.Equal(2, sng.Phrases[iteration.PhraseId].MaxDifficulty);
        Assert.Equal([0, 1, 2], iteration.Difficulty);
    }

    [Fact]
    public void Synthetic_p0_keeps_mains_maximum_two_and_hero_levels_zero_one_two()
    {
        const int mainMaximum = 2;
        var project = SyntheticP0Chart();
        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 600000).Sng;
        var phrase = Assert.Single(sng.Phrases, candidate => candidate.Name == "p0");
        var iteration = Assert.Single(sng.PhraseIterations,
            candidate => sng.Phrases[candidate.PhraseId].Name == "p0");

        Assert.Equal(mainMaximum, phrase.MaxDifficulty);
        Assert.Equal([0, 1, mainMaximum], iteration.Difficulty);
        Assert.All(sng.Phrases.Where(candidate => candidate.Name is "COUNT" or "END"),
            candidate => Assert.Equal(0, candidate.MaxDifficulty));
    }

    internal static ChartProject FirstExplicitP0Chart()
    {
        var project = SyntheticP0Chart();
        var arrangement = project.Arrangements[0];
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 0));
        foreach (var level in arrangement.Levels) level.Notes.Clear();
        arrangement.Levels[2].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        return project;
    }

    internal static ChartProject FinalBoundaryChart(string phraseSource)
    {
        var project = SyntheticP0Chart();
        var arrangement = project.Arrangements[0];
        foreach (var level in arrangement.Levels) level.Notes.Clear();
        arrangement.Levels[2].Notes.Add(new GuitarNote { Position = new FractionalPosition(15), StringIndex = 0, Fret = 3 });
        if (phraseSource == "explicit")
        {
            arrangement.Phrases.Add("END");
            arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
            arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(14), 1));
        }
        else if (phraseSource == "markers")
        {
            arrangement.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Phrase = "p0" });
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(14), Phrase = "END" });
        }

        return project;
    }

    internal static ChartProject SyntheticP0Chart()
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Phrases.Add("p0");
        for (var difficulty = 0; difficulty <= 2; difficulty++)
        {
            var level = new ChartLevel { DifficultyIndex = difficulty };
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(10), StringIndex = 0, Fret = 3 });
            arrangement.Levels.Add(level);
        }

        project.Arrangements.Add(arrangement);
        return project;
    }
}
