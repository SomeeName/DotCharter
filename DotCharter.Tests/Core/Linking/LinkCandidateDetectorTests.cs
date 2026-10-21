using DotCharter.Core.Linking;

namespace DotCharter.Tests.Core.Linking;

/// <summary>
///     Repeated-run detection over sequences of measures written from a
///     letter per measure: the same letter is the same one-bar riff, '.' is a rest bar.
/// </summary>
public sealed class LinkCandidateDetectorTests
{
    [Fact]
    public void TheLongestRepeat_WinsAndCollectsEveryOccurrence()
    {
        var song = Part("ABCABCDABC");

        var result = LinkCandidateDetector.Detect([song]);

        var group = Assert.Single(result.Groups);
        Assert.Equal("A", group.Letter);
        Assert.Equal(3, group.MeasureCount);
        Assert.Equal([0, 3, 7], group.Placements.Select(p => p.FirstMeasure));
        Assert.False(group.SpansParts);
        Assert.Equal([true, true, true, true, true, true, false, true, true, true], result.Covered(0, 10));
    }

    [Fact]
    public void Placements_NeverOverlap()
    {
        var result = LinkCandidateDetector.Detect([Part("AAAAA")]);

        // AA AA, then the odd one out stays alone.
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.MeasureCount);
        Assert.Equal([0, 2], group.Placements.Select(p => p.FirstMeasure));
    }

    [Fact]
    public void RunsNeverStartOrEndOnARest()
    {
        var result = LinkCandidateDetector.Detect([Part("AB.XAB.Y..")]);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.MeasureCount);
        Assert.Equal([0, 4], group.Placements.Select(p => p.FirstMeasure));
    }

    [Fact]
    public void ARestInsideARun_IsPartOfIt()
    {
        var result = LinkCandidateDetector.Detect([Part("A.BXA.BY")]);

        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.MeasureCount);
    }

    [Fact]
    public void AcrossParts_AShared_RunSpansThem()
    {
        var lead = Part("ABCDXY");
        var rhythm = Part("ABCDZW");

        var result = LinkCandidateDetector.Detect([lead, rhythm]);

        var group = Assert.Single(result.Groups);
        Assert.True(group.SpansParts);
        Assert.Equal(4, group.MeasureCount);
        Assert.Equal([new LinkCandidatePlacement(0, 0), new LinkCandidatePlacement(1, 0)], group.Placements);
        Assert.Equal(4, result.Covered(1, 6, spanningOnly: true).Count(c => c));
    }

    [Fact]
    public void NothingRepeats_NoGroups()
    {
        Assert.Empty(LinkCandidateDetector.Detect([Part("ABCDEFG")]).Groups);
        Assert.DoesNotContain(LinkCandidateDetector.Detect([Part("A.A.")]).Groups, g => g.MeasureCount > 1);
    }

    /// <summary>Single measures are only the candidate filter: a run whose inner barline
    /// differs (a note held further into the next bar) is shortened, not grouped whole.</summary>
    [Fact]
    public void ARunThatDiffersAtAnInnerBarline_IsNotGroupedWhole()
    {
        var beats = LinkFixture.Beats(8, seed: null);
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        foreach (var (start, hold) in new[] { (0, 2.0), (4, 3.0) })
        {
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(start, 0), 0, 3, 3 + hold));
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(start + 1, 3), 1, 5, 0));
        }

        var result = LinkCandidateDetector.Detect([new MeasureContentIndex(arrangement, level, beats)]);

        Assert.All(result.Groups, g => Assert.Equal(1, g.MeasureCount));
        Assert.Equal(2, result.Groups.Count);
    }

    [Fact]
    public void GroupLetters_RunPastZ()
    {
        Assert.Equal("A", LinkCandidateDetector.Letter(0));
        Assert.Equal("Z", LinkCandidateDetector.Letter(25));
        Assert.Equal("AA", LinkCandidateDetector.Letter(26));
        Assert.Equal("BA", LinkCandidateDetector.Letter(52));
    }

    [Fact]
    public void Compatibility_FollowsTheStrictRules()
    {
        static Arrangement Guitar(int capo = 0, int lowStep = 0, int strings = 6, ArrangementType type = ArrangementType.Lead)
        {
            var arrangement = new Arrangement { StringCount = strings, Capo = capo, ArrangementType = type };
            for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(i == 0 ? lowStep : 0));
            return arrangement;
        }

        Assert.True(LinkCompatibility.CanShare(Guitar(), Guitar(type: ArrangementType.Rhythm)));
        Assert.False(LinkCompatibility.CanShare(Guitar(), Guitar(capo: 2)));
        Assert.False(LinkCompatibility.CanShare(Guitar(), Guitar(lowStep: -2)));
        Assert.False(LinkCompatibility.CanShare(Guitar(), Guitar(strings: 7)));
        Assert.False(LinkCompatibility.CanShare(Guitar(strings: 4), Guitar(strings: 4, type: ArrangementType.Bass)));
        Assert.Null(LinkCompatibility.Signature(Guitar(type: ArrangementType.Vocal)));
        var playOnly = Guitar();
        playOnly.Provenance = ChartProvenance.PlayOnly;
        Assert.False(LinkCompatibility.CanShare(playOnly, playOnly));
    }

    /// <summary>One measure per letter: a letter is a note on a fret of its own; '.' is a rest.</summary>
    private static MeasureContentIndex Part(string letters)
    {
        var beats = LinkFixture.Beats(letters.Length, seed: 5);
        var arrangement = new Arrangement { StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        for (var m = 0; m < letters.Length; m++)
        {
            if (letters[m] == '.') continue;
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(m, 0), 0, letters[m] - 'A' + 1, 0));
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(m, 2), 1, letters[m] - 'A' + 1, 0));
        }

        return new MeasureContentIndex(arrangement, level, beats);
    }
}
