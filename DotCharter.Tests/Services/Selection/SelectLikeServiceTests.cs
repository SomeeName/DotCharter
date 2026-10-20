using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Selection;

public class SelectLikeServiceTests
{
    private static GuitarNote Note(int beat, int @string, int fret)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = @string,
            Fret = fret
        };
    }

    private static SelectLikeService NewService() => new(new FakeAppLogger());

    [Fact]
    public void FindMatches_SingleNote_ReturnsOtherNotesWithSameStringAndFret()
    {
        var template = Note(0, 5, 7);
        var matchA = Note(4, 5, 7);
        var matchB = Note(8, 5, 7);
        var different = Note(2, 4, 7);
        var source = new[] { template, matchA, matchB, different };

        var result = NewService().FindMatches(new[] { template }, source);

        Assert.Equal(new[] { matchA, matchB }, result);
    }

    [Fact]
    public void FindMatches_PatternMatch_AllowsTransposition()
    {
        // Template: open-string D, then 2nd fret E (relative +2 frets, +1 beat).
        var t1 = Note(0, 4, 0);
        var t2 = Note(1, 4, 2);
        // Same shape, transposed up: fret 5 then fret 7.
        var c1 = Note(8, 4, 5);
        var c2 = Note(9, 4, 7);
        // Same first note but wrong interval – must not match.
        var d1 = Note(16, 4, 5);
        var d2 = Note(17, 4, 8);
        var source = new[] { t1, t2, c1, c2, d1, d2 };

        var result = NewService().FindMatches(new[] { t1, t2 }, source);

        Assert.Equal(new[] { c1, c2 }, result);
    }

    [Fact]
    public void FindMatches_NoTransposition_RequiresExactFrets()
    {
        var t1 = Note(0, 4, 5);
        var c1 = Note(4, 4, 5);
        var c2 = Note(8, 4, 7);
        var source = new[] { t1, c1, c2 };

        var result = NewService().FindMatches(new[] { t1 }, source, allowTransposition: false);

        Assert.Equal(new[] { c1 }, result);
    }

    [Fact]
    public void FindMatches_TechniquesMustMatch()
    {
        var template = Note(0, 4, 5);
        template.Hopo = HopoType.HammerOn;
        var matching = Note(4, 4, 5);
        matching.Hopo = HopoType.HammerOn;
        var differentTechnique = Note(8, 4, 5);
        differentTechnique.Mute = MuteType.Palm;
        var source = new[] { template, matching, differentTechnique };

        var result = NewService().FindMatches(new[] { template }, source);

        Assert.Equal(new[] { matching }, result);
    }

    [Fact]
    public void FindMatches_EmptyTemplate_ReturnsEmpty()
    {
        var result = NewService().FindMatches(Array.Empty<GuitarNote>(), new[] { Note(0, 0, 0) });
        Assert.Empty(result);
    }
}
