using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.Services.State;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class LyricTapBoundaryOwnershipTests
{
    [Fact]
    public void AppendFollowsATappedLineJoinedIntoItsPrecedingLine()
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A\nB C");
        var a = rig.Tap(1000);
        var b = rig.Tap(1500);
        var first = rig.Track.Phrases[0];
        var second = rig.Track.Phrases[1];
        Assert.True(rig.Boundary(first, a, false));
        rig.History.Undo();
        rig.History.Redo();
        var c = rig.Tap(2000);
        Assert.Same(first, rig.Track.Phrases.Single(p => p.Lyrics.Contains(c)));
        Assert.Equal([a, b, c], first.Lyrics);
        rig.History.Undo();
        rig.History.Redo();
        Assert.Same(c, first.Lyrics[^1]);
        rig.History.Undo();
        c = rig.Tap(2000);
        Assert.Same(first, rig.Track.Phrases.Single(p => p.Lyrics.Contains(c)));
        rig.History.Undo();
        rig.History.Undo();
        c = rig.Tap(2000);
        Assert.Same(second, rig.Track.Phrases.Single(p => p.Lyrics.Contains(c)));
        Assert.Equal([b, c], second.Lyrics);
        rig.AssertEnds();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppendFollowsTheLastTappedSyllableThroughBoundaryUndoRedoAndAnotherTap(bool join)
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B C");
        var a = rig.Tap(1000);
        var b = rig.Tap(1500);
        var first = Assert.Single(rig.Track.Phrases);
        if (join)
        {
            rig.Track.Phrases.Add(new LyricPhrase
            {
                Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10),
                Lyrics = [new LyricEvent { Text = "X", Position = new FractionalPosition(8), IsPhraseEnd = true }]
            });
        }

        Assert.True(rig.Boundary(first, join ? b : a, !join));
        rig.History.Undo();
        rig.History.Redo();
        var owner = rig.Track.Phrases.Single(p => p.Lyrics.Contains(b));
        var c = rig.Tap(5500);
        Assert.Same(owner, rig.Track.Phrases.Single(p => p.Lyrics.Contains(c)));
        Assert.Equal(join ? ["A", "B", "X", "C"] : ["B", "C"], owner.Lyrics.Select(l => l.Text));
        rig.AssertEnds();

        rig.History.Undo();
        rig.History.Redo();
        Assert.Same(c, owner.Lyrics[^1]);
        rig.History.Undo();
        var repeated = rig.Tap(5500);
        Assert.Same(owner, rig.Track.Phrases.Single(p => p.Lyrics.Contains(repeated)));
        rig.AssertEnds();

        rig.History.Undo();
        rig.History.Undo();
        var afterBoundaryUndo = rig.Tap(5500);
        Assert.Same(first, rig.Track.Phrases.Single(p => p.Lyrics.Contains(afterBoundaryUndo)));
        Assert.Equal(["A", "B", "C"], first.Lyrics.Select(l => l.Text));
        rig.AssertEnds();
    }

    [Fact]
    public void UndoingAnAppendRestoresTheTappedAnchorRatherThanTheJoinedLinesLastSyllable()
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B C");
        rig.Tap(1000);
        var b = rig.Tap(1500);
        var first = Assert.Single(rig.Track.Phrases);
        rig.Track.Phrases.Add(new LyricPhrase
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10),
            Lyrics = [new LyricEvent { Text = "X", Position = new FractionalPosition(8), IsPhraseEnd = true }]
        });
        Assert.True(rig.Boundary(first, b, false));
        rig.Tap(5500);
        rig.History.Undo();
        Assert.True(rig.Boundary(first, b, true));

        var c = rig.Tap(5500);

        Assert.Same(first, rig.Track.Phrases.Single(p => p.Lyrics.Contains(c)));
        Assert.Equal(["A", "B", "C"], first.Lyrics.Select(l => l.Text));
        rig.AssertEnds();
    }
}
