using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #930: when the bass hand re-plants up (the abandonment rule) on a note whose very next note plays the same
///     fret on a LOWER string, the charters move the anchor one note later, onto that lower note: the root the index
///     finger takes. The same fret on a higher string still re-plants where it did.
/// </summary>
public sealed class BassRootBelowReplantTests
{
    private static string Anchors(bool bass, params (int String, int Fret)[] notes)
    {
        var level = new ChartLevel();
        for (var i = 0; i < notes.Length; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i), StringIndex = notes[i].String, Fret = notes[i].Fret,
            });
        }

        return string.Join(" ", new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: bass)
            .Select(a => $"{a.Position.BeatId}:{a.Fret}"));
    }

    [Fact]
    public void TheReplantWaitsForTheSameFretOnTheLowerString()
    {
        // E4 D6 E4 D6 E4 D6 | E6 D8 E6 D8 E6 D8 (strings 0 and 2). Every note after
        // the third D6 plays above 4, so the hand re-plants on 6 while that D6 still fits the hand at 4. The charters
        // move on the E6 that follows it (the new octave's root, index finger), not on the D6.
        var anchors = Anchors(true, (0, 4), (2, 6), (0, 4), (2, 6), (0, 4), (2, 6), (0, 6), (2, 8), (0, 6), (2, 8),
            (0, 6), (2, 8));

        Assert.Equal("0:4 6:6", anchors);
    }

    [Fact]
    public void TheSameFretOnAHigherString_StillReplantsOnTheFirstNote()
    {
        var anchors = Anchors(true, (1, 3), (1, 3), (1, 3), (1, 6), (2, 6), (2, 7), (2, 8), (2, 6), (2, 7), (2, 8),
            (2, 6));

        Assert.Equal("0:3 3:6", anchors);
    }

    [Fact]
    public void OnGuitar_TheReplantIsUnchanged()
    {
        // Guitar's look-ahead is 14 notes, so the phrase is long enough for it to re-plant at all.
        var notes = new List<(int, int)> { (1, 3), (1, 3), (1, 3), (2, 6) };
        for (var i = 0; i < 4; i++) notes.AddRange([(1, 6), (1, 7), (1, 8)]);
        notes.Add((1, 6));

        Assert.Equal("0:3 3:6", Anchors(false, [.. notes]));
    }
}
