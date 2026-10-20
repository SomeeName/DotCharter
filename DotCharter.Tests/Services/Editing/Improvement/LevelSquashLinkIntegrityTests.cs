using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     A squash copies every phrase segment from that phrase's OWN
///     <see cref="PhraseInfo.MaxDifficulty" /> level, so the one level it
///     produces is a MIX. A sustain that links across a phrase boundary then
///     points into content that came from a different level — where the note it
///     named may have been thinned away or folded into a reduced chord — while
///     the source note keeps its flag verbatim. <see cref="LinkNextRule" />
///     reports the result as <c>LinkNextMissingTargetNote</c>.
/// </summary>
public sealed class LevelSquashLinkIntegrityTests
{
    [Fact]
    public void TrySquash_ClearsALinkTheSquashedLevelHasNothingToLandOn()
    {
        var arrangement = TwoLevelChartWithACrossBoundaryLink();

        Assert.True(new LevelSquashService().TrySquash(arrangement));

        var squashed = Assert.Single(arrangement.Levels);
        // The verse segment came from level 0, which plays string 0 there — so
        // the linked sustain on string 2 has no landing anywhere after it.
        Assert.DoesNotContain(squashed.Notes, n => n.StringIndex == 2 && n.Position == new FractionalPosition(4));
        var source = Assert.Single(squashed.Notes, n => n.Position == new FractionalPosition(0));
        Assert.False(source.LinkNext);
        Assert.Empty(IssuesFor(arrangement));
    }

    /// <summary>The narrow rule is the point: a link whose landing merely MOVED
    /// is the charter's own to judge, and a squash may not silently unlink one
    /// that still has somewhere to go.</summary>
    [Fact]
    public void TrySquash_KeepsALinkThatStillHasALandingOnItsString()
    {
        var arrangement = TwoLevelChartWithACrossBoundaryLink();
        // Level 0 does play string 2 in the verse, so the chain survives.
        arrangement.Levels[0].Notes.Add(new GuitarNote
        { Position = new FractionalPosition(4), StringIndex = 2, Fret = 5 });

        Assert.True(new LevelSquashService().TrySquash(arrangement));

        var squashed = Assert.Single(arrangement.Levels);
        Assert.True(Assert.Single(squashed.Notes, n => n.Position == new FractionalPosition(0)).LinkNext);
    }

    /// <summary>
    ///     A chart with two levels and two phrases: "intro" reads the hard level,
    ///     "verse" reads the easy one. The intro's last note is a sustain linked
    ///     into the verse's first sound on string 2 — which only the HARD level
    ///     plays.
    /// </summary>
    private static Arrangement TwoLevelChartWithACrossBoundaryLink()
    {
        var arrangement = new Arrangement { Name = "Lead" };

        var easy = new ChartLevel { DifficultyIndex = 0 };
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 2, Fret = 5 });
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(easy);

        var hard = new ChartLevel { DifficultyIndex = 1 };
        hard.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 5,
            Sustain = new FractionalPosition(4), LinkNext = true
        });
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 5 });
        hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(hard);

        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        arrangement.PhraseInfos["intro"] = new PhraseInfo(1, false);
        arrangement.PhraseInfos["verse"] = new PhraseInfo(0, false);
        return arrangement;
    }

    private static List<ArrangementIssue> IssuesFor(Arrangement arrangement)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 12; i++) beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });

        var issues = new List<ArrangementIssue>();
        new LinkNextRule().Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        return issues;
    }
}
