using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.State;

public sealed class LyricEditSequenceTests
{
    [Fact]
    public void DeletingTheOnlyTapStartsANewLineAndUndoRedoRestoresBothContainers()
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B C");
        var a = rig.Tap(1000);
        var original = Assert.Single(rig.Track.Phrases);
        Assert.True(rig.History.Do(new RemoveLyricAction(rig.Track.Phrases, original, a)));
        var b = rig.Tap(1500);
        var replacement = Assert.Single(rig.Track.Phrases);
        Assert.NotSame(original, replacement);
        Assert.Equal([b], replacement.Lyrics);
        rig.History.Undo();
        Assert.Empty(rig.Track.Phrases);
        rig.History.Undo();
        Assert.Same(original, Assert.Single(rig.Track.Phrases));
        Assert.Equal([a], original.Lyrics);
        rig.History.Redo();
        rig.History.Redo();
        Assert.Same(replacement, Assert.Single(rig.Track.Phrases));
        Assert.Equal([b], replacement.Lyrics);
        rig.AssertEnds();
    }

    [Fact]
    public void JoiningLinesInReverseTimeOrderRetainsBothSpansAndTheirPadding()
    {
        var rig = new LyricEditTestRig();
        var a = Word("A", 6);
        var b = Word("B", 2);
        var first = new LyricPhrase { Position = new FractionalPosition(5), EndPosition = new FractionalPosition(8), Lyrics = [a] };
        var second = new LyricPhrase { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4), Lyrics = [b] };
        rig.Track.Phrases.Add(first);
        rig.Track.Phrases.Add(second);
        Assert.True(rig.Boundary(first, a, false));
        AssertBounds();
        rig.History.Undo();
        Assert.Equal((new FractionalPosition(5), new FractionalPosition(8)), (first.Position, first.EndPosition));
        Assert.Equal((new FractionalPosition(1), new FractionalPosition(4)), (second.Position, second.EndPosition));
        rig.History.Redo();
        AssertBounds();
        rig.AssertEnds();

        void AssertBounds()
        {
            Assert.Same(first, Assert.Single(rig.Track.Phrases));
            Assert.Equal([a, b], first.Lyrics);
            Assert.Equal((new FractionalPosition(1), new FractionalPosition(8)), (first.Position, first.EndPosition));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinThenEarlierTapNeverTrimsTheLaterStartingJoinedSyllable(bool longTail)
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B C");
        var a = rig.Tap(1000);
        var b = rig.Tap(1500);
        var first = Assert.Single(rig.Track.Phrases);
        if (longTail) first.EndPosition = b.EndPosition = new FractionalPosition(5);
        var x = Word("X", 8);
        var next = new LyricPhrase { Position = x.Position, EndPosition = x.EndPosition, Lyrics = [x] };
        rig.Track.Phrases.Add(next);
        Assert.True(rig.Boundary(first, b, false));
        var c = rig.Tap(2000);
        AssertJoined();
        rig.History.Undo();
        Assert.Equal([a, b, x], first.Lyrics);
        Assert.True(x.IsPhraseEnd);
        Assert.Equal(longTail ? new FractionalPosition(5) : new FractionalPosition(3, new Fraction(1, 2)), b.EndPosition);
        rig.History.Redo();
        AssertJoined();

        void AssertJoined()
        {
            Assert.Equal([a, b, x, c], Assert.Single(rig.Track.Phrases).Lyrics);
            Assert.Equal(new FractionalPosition(8, new Fraction(1, 2)), x.EndPosition);
            Assert.Equal(longTail ? new FractionalPosition(4) : new FractionalPosition(3, new Fraction(1, 2)), b.EndPosition);
            rig.AssertEnds();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SplitAndJoinContainAllSyllablesDespiteTimeOrderAndPreserveOuterPadding(bool join, bool padding)
    {
        var rig = new LyricEditTestRig();
        var a = Word("A", 6);
        var b = Word("B", 2);
        var c = Word("C", 4);
        var first = new LyricPhrase
        {
            Position = new FractionalPosition(padding ? 1 : 2),
            EndPosition = padding ? new FractionalPosition(8) : a.EndPosition, Lyrics = [a, b, c]
        };
        a.IsPhraseEnd = b.IsPhraseEnd = false;
        rig.Track.Phrases.Add(first);
        Assert.True(rig.Boundary(first, b, true));
        var second = rig.Track.Phrases[1];
        Assert.Equal(new FractionalPosition(padding ? 1 : 2), first.Position);
        Assert.Equal(a.EndPosition, first.EndPosition);
        Assert.Equal(c.Position, second.Position);
        Assert.Equal(padding ? new FractionalPosition(8) : c.EndPosition, second.EndPosition);
        AssertRouting(a, first);
        AssertRouting(b, first);
        AssertRouting(c, second);
        if (join)
        {
            Assert.True(rig.Boundary(first, b, false));
            Assert.Equal((new FractionalPosition(padding ? 1 : 2), padding ? new FractionalPosition(8) : a.EndPosition),
                (first.Position, first.EndPosition));
        }
        var groups = rig.Track.Phrases.ToArray();
        var bounds = groups.Select(p => (p.Position, p.EndPosition)).ToArray();
        rig.History.Undo();
        rig.History.Redo();
        Assert.Equal(groups, rig.Track.Phrases);
        Assert.Equal(bounds, rig.Track.Phrases.Select(p => (p.Position, p.EndPosition)));
        rig.AssertEnds();

        void AssertRouting(LyricEvent word, LyricPhrase owner)
        {
            var inserted = rig.Insert(word.Position.BeatId);
            Assert.Contains(inserted, owner.Lyrics);
            rig.History.Undo();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeleteThenContinueUsesTheNearestSurvivingTapAndRestoresItsHistory(int deleted)
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B C D");
        var a = rig.Tap(1000);
        var b = rig.Tap(1500);
        var original = Assert.Single(rig.Track.Phrases);
        if (deleted != 0) Assert.True(rig.History.Do(new RemoveLyricAction(rig.Track.Phrases, original, b)));
        if (deleted != 1) Assert.True(rig.History.Do(new RemoveLyricAction(rig.Track.Phrases, original, a)));
        var survivor = deleted == 0 ? b : a;
        var c = rig.Tap(2000);
        var owner = Assert.Single(rig.Track.Phrases);
        Assert.Equal(deleted == 2 ? [c] : [survivor, c], owner.Lyrics);
        if (deleted != 2) Assert.Same(original, owner);
        rig.History.Undo();
        Assert.DoesNotContain(c, rig.Members());
        rig.History.Redo();
        Assert.Same(owner, Assert.Single(rig.Track.Phrases));
        Assert.Same(c, owner.Lyrics[^1]);
        rig.History.Undo();
        var repeated = rig.Tap(2000);
        Assert.Equal(deleted == 2 ? [repeated] : [survivor, repeated], Assert.Single(rig.Track.Phrases).Lyrics);
        rig.AssertEnds();
    }

    private static LyricEvent Word(string text, int beat) => new()
    {
        Text = text, Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat, new Fraction(1, 2)), IsPhraseEnd = true
    };
}
