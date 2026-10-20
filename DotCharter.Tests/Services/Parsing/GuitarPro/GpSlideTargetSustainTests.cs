using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The "slide-or-link TARGET" exemption inside
///     <see cref="GpPostProcessor.AdjustSustains" />: a sound that a slide or a
///     link-next lands on keeps its sustain even when it is shorter than a
///     quarter note. The exemption only applies when the gesture actually
///     REACHES the sound, and an unpitched slide-OUT never claims one.
///     <para>
///     Provenance: owner report 2026-08-21 against a real GP8 file
///     ("Yorushika - Hachigatsu Bou Tsukiakari.gp"). In measures 9 and 17 a plain
///     eighth carried a visible tail. The cause was a slide-out fifteen beats
///     earlier — <c>Slide Flags=4</c>, which <see cref="GpNoteParser" /> stores as
///     <c>UnpitchedSlide</c> plus a synthetic <c>SlideTo = fret - 5</c> — being
///     treated as pointing at the next sound on that string, however distant.
///     </para>
/// </summary>
public sealed class GpSlideTargetSustainTests
{
    private static readonly FractionalPosition Eighth = new(0, new Fraction(1, 2));
    private static readonly FractionalPosition Quarter = new(0, new Fraction(1, 1)); // → BeatId 1

    /// <summary>Two notes on one string: the first carries the gesture, the
    /// second is a plain short note whose fate the exemption decides.</summary>
    private static (ChartLevel Level, GuitarNote Second) Pair(
        FractionalPosition firstSustain, int secondBeat,
        bool unpitchedSlide = false, int? slideTo = null, bool linkNext = false)
    {
        var first = new GuitarNote
        {
            Position = new FractionalPosition(4), StringIndex = 1, Fret = 4,
            Sustain = firstSustain, UnpitchedSlide = unpitchedSlide,
            SlideTo = slideTo, LinkNext = linkNext
        };
        var second = new GuitarNote
        {
            Position = new FractionalPosition(secondBeat), StringIndex = 1, Fret = 4,
            Sustain = Eighth
        };
        var level = new ChartLevel();
        level.Notes.Add(first);
        level.Notes.Add(second);
        return (level, second);
    }

    [Fact]
    public void DistantNote_AfterUnpitchedSlideOut_IsTruncated()
    {
        // The reported bug, reduced: a slide-off at beat 4 and a plain eighth at
        // beat 20 with fifteen beats of silence between them.
        var (level, second) = Pair(Eighth, secondBeat: 20, unpitchedSlide: true, slideTo: 1);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(FractionalPosition.Zero, second.Sustain);
    }

    [Fact]
    public void AdjacentNote_AfterUnpitchedSlideOut_IsTruncated()
    {
        // Even ABUTTING, a slide-out lands nowhere: it slides into silence, so the
        // note that happens to follow it is not its landing and keeps no sustain.
        var (level, second) = Pair(Quarter, secondBeat: 5, unpitchedSlide: true, slideTo: 1);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(FractionalPosition.Zero, second.Sustain);
    }

    [Fact]
    public void DistantNote_AfterPitchedSlide_IsTruncated()
    {
        // A pitched slide whose own sustain stops long before the next sound did
        // not slide INTO it; only adjacency makes a landing.
        var (level, second) = Pair(Eighth, secondBeat: 20, slideTo: 7);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(FractionalPosition.Zero, second.Sustain);
    }

    [Fact]
    public void AdjacentLanding_OfPitchedSlide_KeepsSustain()
    {
        // The exemption's reason to exist: a slide that reaches its landing note
        // must leave that note something to ring on.
        var (level, second) = Pair(Quarter, secondBeat: 5, slideTo: 7);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(Eighth, second.Sustain);
    }

    [Fact]
    public void DistantNote_AfterLinkNext_IsTruncated()
    {
        var (level, second) = Pair(Eighth, secondBeat: 20, linkNext: true);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(FractionalPosition.Zero, second.Sustain);
    }

    [Fact]
    public void AdjacentLinkTarget_KeepsSustain()
    {
        var (level, second) = Pair(Quarter, secondBeat: 5, linkNext: true);
        GpPostProcessor.AdjustSustains(level, 6);
        Assert.Equal(Eighth, second.Sustain);
    }
}
