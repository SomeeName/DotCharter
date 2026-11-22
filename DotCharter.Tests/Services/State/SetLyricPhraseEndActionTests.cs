using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.Services.State.Linking;

namespace DotCharter.Tests.Services.State;

public sealed class SetLyricPhraseEndActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhraseBoundaryEdits_RetainLinkedMeasuresThroughUndoRedo(bool split)
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 7));
        var keys = h.Keys(group).Select(k => k!.Canonical).ToArray();
        var first = new LyricPhrase
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(3),
            Lyrics =
            [
                new LyricEvent { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2) },
                new LyricEvent { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), IsPhraseEnd = true }
            ]
        };
        var next = new LyricPhrase
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5),
            Lyrics = [new LyricEvent { Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5), IsPhraseEnd = true }]
        };
        var track = new VocalTrack { Phrases = [first, next] };
        h.Project.VocalTracks.Add(track);
        var action = new SetLyricPhraseEndAction(track.Phrases, first, first.Lyrics[split ? 0 : 1], split);
        Assert.Equal(LinkPolicy.Retain, LinkPolicies.Of(action));

        Assert.True(h.History.Do(action));
        Assert.Equal(split ? 3 : 1, track.Phrases.Count);
        AssertLinks();
        h.History.Undo();
        Assert.Equal(2, track.Phrases.Count);
        AssertLinks();
        h.History.Redo();
        Assert.Equal(split ? 3 : 1, track.Phrases.Count);
        AssertLinks();

        void AssertLinks()
        {
            LinkHarness.AssertLinkedAndEqual(h, h.Project.LinkGroups.Single(), 3);
            Assert.Equal(keys, h.Keys(h.Project.LinkGroups.Single()).Select(k => k!.Canonical));
        }
    }
}
