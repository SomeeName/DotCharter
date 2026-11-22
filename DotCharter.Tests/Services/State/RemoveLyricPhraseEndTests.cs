using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.State;

public sealed class RemoveLyricPhraseEndTests
{
    [Fact]
    public void RemovingTheLastSyllable_MovesTheEndFlagAndUndoRestoresIt()
    {
        var first = new LyricEvent { Text = "first" };
        var last = new LyricEvent { Text = "last", IsPhraseEnd = true };
        var phrase = new LyricPhrase { Lyrics = [first, last] };
        var track = new VocalTrack { Phrases = [phrase] };
        var history = new HistoryService(new FakeAppLogger());

        history.Do(new RemoveLyricAction(track.Phrases, phrase, last));

        Assert.Same(first, Assert.Single(phrase.Lyrics));
        Assert.True(first.IsPhraseEnd);
        history.Undo();
        Assert.Equal([first, last], phrase.Lyrics);
        Assert.False(first.IsPhraseEnd);
        Assert.True(last.IsPhraseEnd);
        history.Redo();
        Assert.Same(first, Assert.Single(phrase.Lyrics));
        Assert.True(first.IsPhraseEnd);
    }

    [Fact]
    public void RemovingTheSyllableAfterAWordPart_LeavesALineEndWithoutTheWordPartAndUndoRestoresIt()
    {
        var hel = new LyricEvent { Text = "hel", IsWordPart = true };
        var lo = new LyricEvent { Text = "lo", IsPhraseEnd = true };
        var phrase = new LyricPhrase { Lyrics = [hel, lo] };
        var track = new VocalTrack { Phrases = [phrase] };
        var history = new HistoryService(new FakeAppLogger());

        history.Do(new RemoveLyricAction(track.Phrases, phrase, lo));

        Assert.True(hel.IsPhraseEnd);
        Assert.False(hel.IsWordPart);
        history.Undo();
        Assert.True(hel.IsWordPart);
        Assert.False(hel.IsPhraseEnd);
        history.Redo();
        Assert.False(hel.IsWordPart);
    }
}
