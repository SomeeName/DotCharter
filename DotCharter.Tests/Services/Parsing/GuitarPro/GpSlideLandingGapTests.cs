using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// #284 item 5 (owner's rule, 2026-10-07): a Guitar Pro "slide to next note" took
/// the next note on its string as its landing fret however far away it was, bars
/// later after a rest. The landing now has to start within one beat of the slide's
/// end; otherwise the note imports as a plain note.
/// </summary>
public sealed class GpSlideLandingGapTests
{
    private static GuitarNote Slider() => new()
    {
        Position = new FractionalPosition(0),
        Sustain = new FractionalPosition(1),
        StringIndex = 2,
        Fret = 5,
        SlideTo = -1
    };

    private static GuitarNote NoteAt(FractionalPosition position, int fret) =>
        new() { Position = position, StringIndex = 2, Fret = fret };

    [Fact]
    public void ALandingRightAfterTheSlide_IsItsTarget()
    {
        var level = new ChartLevel();
        var slide = Slider();
        level.Notes.Add(slide);
        level.Notes.Add(NoteAt(new FractionalPosition(1), 7));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Equal(7, slide.SlideTo);
        Assert.True(slide.LinkNext);
    }

    [Fact]
    public void ALandingOneBeatAfterARest_IsStillItsTarget()
    {
        var level = new ChartLevel();
        var slide = Slider();
        level.Notes.Add(slide);
        level.Notes.Add(NoteAt(new FractionalPosition(2), 7));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Equal(7, slide.SlideTo);
        Assert.False(slide.LinkNext); // the sustain does not reach it
    }

    [Fact]
    public void ANoteBarsLater_IsNoLanding_AndTheSlideIsDropped()
    {
        var level = new ChartLevel();
        var slide = Slider();
        level.Notes.Add(slide);
        level.Notes.Add(NoteAt(new FractionalPosition(9), 12));

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(slide.SlideTo);
        Assert.False(slide.LinkNext);
    }
}
