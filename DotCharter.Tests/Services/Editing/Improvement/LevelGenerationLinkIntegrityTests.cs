using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     Link-Next integrity of the levels <see cref="LevelGenerationService" />
///     builds. The generator only ever asked "did the sound my link pointed at
///     survive the thinning?" — which is the wrong question on a level that also
///     REWRITES the surviving sounds. Two rewrites break the chain while the
///     target is still there, and <see cref="LinkNextRule" /> reports both:
///     stripping a slide leaves the fret jump unexplained, and reducing a chord
///     to its root note removes the landing from every other string.
/// </summary>
public sealed class LevelGenerationLinkIntegrityTests
{
    /// <summary>Three levels, so level 0 (ratio 1/3) is a SIMPLIFY level and
    /// level 2 is the verbatim top.</summary>
    private const int LevelCount = 3;

    [Fact]
    public void TryGenerate_ClearsLinkNext_WhenTechniqueStrippingRemovedTheSlideThatExplainedTheFretJump()
    {
        var arrangement = TwoPhraseArrangement();
        var level = arrangement.Levels[0];
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 5,
            Sustain = new FractionalPosition(1), SlideTo = 8, LinkNext = true
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 8 });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        var lowest = arrangement.Levels[0];
        Assert.Equal(2, lowest.Notes.Count);
        Assert.Null(lowest.Notes[0].SlideTo);
        Assert.False(lowest.Notes[0].LinkNext);
        Assert.Empty(IssuesFor(arrangement));

        // The verbatim top level keeps the slide AND the link.
        var top = arrangement.Levels[^1];
        Assert.Equal(8, top.Notes[0].SlideTo);
        Assert.True(top.Notes[0].LinkNext);
    }

    [Fact]
    public void TryGenerate_ClearsLinkNext_WhenTheLandingChordWasReducedToAnotherString()
    {
        var arrangement = TwoPhraseArrangement();
        var level = arrangement.Levels[0];
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 7,
            Sustain = new FractionalPosition(1), LinkNext = true
        });
        level.Chords.Add(new Chord
        {
            Position = new FractionalPosition(1), TemplateId = 0,
            ChordNotes =
            {
                new ChordNote { StringIndex = 0, Fret = 5 },
                new ChordNote { StringIndex = 1, Fret = 7 },
                new ChordNote { StringIndex = 2, Fret = 7 }
            }
        });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, LevelCount));

        var lowest = arrangement.Levels[0];
        // The chord collapsed onto string 0, so nothing lands on string 2.
        Assert.Empty(lowest.Chords);
        Assert.DoesNotContain(lowest.Notes, n => n.StringIndex == 2 && n.Position == new FractionalPosition(1));
        var source = Assert.Single(lowest.Notes, n => n.Position == new FractionalPosition(0));
        Assert.False(source.LinkNext);
        Assert.Empty(IssuesFor(arrangement));

        var top = arrangement.Levels[^1];
        Assert.True(top.Notes[0].LinkNext);
        Assert.Single(top.Chords);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An empty level plus a phrase marker on beat 0 and beat 1, so
    /// BOTH sounds the tests place are phrase-starting (tier 0) and neither is
    /// thinned away — the level differences are then purely the rewrites.</summary>
    private static Arrangement TwoPhraseArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1), Phrase = "verse" });
        return arrangement;
    }

    /// <summary>Every Link-Next finding the inspector's own rule reports over
    /// the generated levels — the generator may not manufacture one.</summary>
    private static List<ArrangementIssue> IssuesFor(Arrangement arrangement)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 8; i++) beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });

        var issues = new List<ArrangementIssue>();
        new LinkNextRule().Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        return issues;
    }
}
