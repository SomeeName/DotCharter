using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #502: the bass rules learned from the owner's finished charts. A bassist's hand covers three frets from the
///     anchor (index, middle, pinky): an excursion that stays inside them and comes back is played from where the
///     hand is (<see cref="HandAbandonment.StaysWithinReach" />), the hand re-plants onto the note that plays the new
///     position's lowest fret, and a group's anchor is not pulled down so far that its opening note sits three frets
///     above it. Guitar keeps the four-fret box for all three.
/// </summary>
public sealed class BassHandReachTests
{
    private static ChartLevel Level(params int[] frets)
    {
        // One string, so no octave figure (#501) can start.
        var level = new ChartLevel();
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 1, Fret = frets[i] });
        return level;
    }

    private static string Anchors(ChartLevel level, bool bass) =>
        string.Join(" ", new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: bass)
            .Select(a => $"{a.Position.BeatId}:{a.Fret}"));

    private static int[] Frets(params (int Fret, int Count)[] runs) =>
        runs.SelectMany(r => Enumerable.Repeat(r.Fret, r.Count)).ToArray();

    [Fact]
    public void AnExcursionTwoFretsUpThatComesBack_IsPlayedFromTheHandInForce()
    {
        // A3 | A5 x10 | A3: every one of the next six notes is above 3, so the abandonment look-ahead alone re-plants
        // on 5 and comes back. The charters hold 3 and take the 5 with the pinky (they re-planted on 8-33% of such
        // runs). Ten notes, so the bounce collapse (8) is not what holds it.
        var level = Level(Frets((3, 1), (5, 10), (3, 2)));

        Assert.Equal("0:3", Anchors(level, bass: true));
    }

    [Fact]
    public void AnExcursionThatLeavesTheThreeFrets_StillReplants()
    {
        // A3 | A5 A5 A6 ... | A3: the 6 is anchor + 3, past the bass hand, so the hand moves up for the run.
        var level = Level(Frets((3, 1), (5, 2), (6, 1), (5, 2), (6, 1), (5, 4), (3, 2)));

        Assert.Equal("0:3 1:5 11:3", Anchors(level, bass: true));
    }

    [Fact]
    public void OnGuitar_TheExcursionStillReplants()
    {
        // The reach rule is bass only (on guitar it measured -0.2 pp). Guitar's look-ahead is 14 notes.
        var level = Level(Frets((3, 1), (5, 16), (3, 2)));

        Assert.Equal("0:3 1:5 17:3", Anchors(level, bass: false));
    }

    [Fact]
    public void TheHandMovesOntoTheNoteThatPlaysTheNewLowestFret()
    {
        // A3 | A6 A6 A6 A5 ...: the look-ahead from the first 6 already sees the 5s, so the abandonment fires there and
        // anchors on 5 a note before anything plays it. The charters move one note later, onto the 5 (the index
        // finger's note); the bass pull-back may still set it up to two notes early.
        var level = Level(Frets((3, 1), (6, 3), (5, 8)));

        Assert.Equal("0:3 2:5", Anchors(level, bass: true));
    }

    [Fact]
    public void ABassGroupIsNotDescendedSoFarThatItsOpeningNoteSitsThreeFretsUp()
    {
        // A6 A6 | A3: on guitar the opening anchor is pulled down to 3 (6 is the pinky's fret in the box 3-6); a
        // bass hand covers 3-5, so the 3 is a new hand position. A descend that left the opening note at +3 matched
        // the charters 0-21% of the time.
        var level = Level(6, 6, 3, 3);

        Assert.Equal("0:6 2:3", Anchors(level, bass: true));
        Assert.Equal("0:3", Anchors(level, bass: false));
    }

    [Fact]
    public void ABassDescendWithinThreeFrets_StillHappens()
    {
        // A5 A5 | A3: the opening note sits at +2 of the descended anchor — inside the bass hand.
        var level = Level(5, 5, 3, 3);

        Assert.Equal("0:3", Anchors(level, bass: true));
    }
}
