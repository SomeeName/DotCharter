using DotCharter.Tests.Services.State;

namespace DotCharter.Tests.Services.Editing.Maintenance;

public sealed class VocalInsertionBoundaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OutOfOrderTaps_InsertKeepsTheActualLastSyllableFlaggedAndEveryToggleWorks(int index)
    {
        var rig = new LyricEditTestRig();
        rig.Session.Load("A B");
        var a = rig.Tap(3000);
        var b = rig.Tap(1000);
        var phrase = Assert.Single(rig.Track.Phrases);

        var placeholder = rig.Insert(4);

        Assert.Equal([placeholder, a, b], phrase.Lyrics);
        rig.AssertEnds();
        rig.History.Undo();
        Assert.Equal([a, b], phrase.Lyrics);
        Assert.False(a.IsPhraseEnd);
        Assert.True(b.IsPhraseEnd);
        rig.History.Redo();
        rig.AssertEnds();
        var lyric = phrase.Lyrics[index];
        Assert.Equal(index < 2, rig.Boundary(phrase, lyric, !lyric.IsPhraseEnd));
        rig.AssertEnds();
        if (index == 2) return;
        Assert.Equal(2, rig.Track.Phrases.Count);
        Assert.True(rig.Boundary(phrase, lyric, false));
        Assert.Same(phrase, Assert.Single(rig.Track.Phrases));
        Assert.Equal([placeholder, a, b], phrase.Lyrics);
        rig.AssertEnds();
    }
}
