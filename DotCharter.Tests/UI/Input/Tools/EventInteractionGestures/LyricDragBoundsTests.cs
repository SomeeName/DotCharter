using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

public sealed class LyricDragBoundsTests
{
    private static LyricEvent LyricAt(int beat, string text = "la") => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat),
        Text = text
    };

    private static LyricPhrase Phrase(params LyricEvent[] lyrics)
    {
        var phrase = new LyricPhrase();
        foreach (var l in lyrics) phrase.Lyrics.Add(l);
        if (lyrics.Length > 0)
        {
            phrase.Position = lyrics[0].Position;
            phrase.EndPosition = lyrics[^1].EndPosition;
        }

        return phrase;
    }

    // ---- FindNeighbors ------------------------------------------------------

    [Fact]
    public void FindNeighbors_InsideAPhrase_ReturnsBothSiblings()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var c = LyricAt(3);
        var phrases = new List<LyricPhrase> { Phrase(a, b, c) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, b);

        Assert.Same(a, prev);
        Assert.Same(c, next);
    }

    /// <summary>The neighbour may live in the ADJACENT PHRASE — the last
    /// syllable of one phrase bounds against the next phrase's first.</summary>
    [Fact]
    public void FindNeighbors_AtAPhraseBoundary_CrossesIntoTheAdjacentPhrases()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var c = LyricAt(4);
        var d = LyricAt(5);
        var phrases = new List<LyricPhrase> { Phrase(a, b), Phrase(c, d) };

        var (prevOfC, nextOfC) = LyricDragBounds.FindNeighbors(phrases, c);
        Assert.Same(b, prevOfC);
        Assert.Same(d, nextOfC);

        var (prevOfB, nextOfB) = LyricDragBounds.FindNeighbors(phrases, b);
        Assert.Same(a, prevOfB);
        Assert.Same(c, nextOfB);
    }

    [Fact]
    public void FindNeighbors_SkipsEmptyPhrasesBetween()
    {
        var a = LyricAt(1);
        var b = LyricAt(4);
        var phrases = new List<LyricPhrase> { Phrase(a), Phrase(), Phrase(b) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, b);

        Assert.Same(a, prev);
        Assert.Null(next);
    }

    [Fact]
    public void FindNeighbors_FirstAndLastSyllable_HaveOpenSides()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var phrases = new List<LyricPhrase> { Phrase(a, b) };

        Assert.Equal((null, b), LyricDragBounds.FindNeighbors(phrases, a));
        Assert.Equal((a, null), LyricDragBounds.FindNeighbors(phrases, b));
    }

    [Fact]
    public void FindNeighbors_UnknownTarget_ReturnsNothing()
    {
        var phrases = new List<LyricPhrase> { Phrase(LyricAt(1)) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, LyricAt(9));
        Assert.Null(prev);
        Assert.Null(next);
    }
}
