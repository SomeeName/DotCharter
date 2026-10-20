using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A "sl." grace note (a grace that SLIDES into its target) must import as a
///     real short note sliding into the target — the representation EOF and Log
///     Charter both produce — not a notation-only <see cref="GuitarNote.GraceFret"/>
///     hint that never shows on the highway (owner report 2026-07-19). A plain
///     grace (no slide) stays a hint.
/// </summary>
public sealed class GpGraceSlideTests
{
    private static GuitarNote Target(int fret, int graceFret, bool graceSlide, int beat = 4)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            Sustain = new FractionalPosition(2),
            StringIndex = 1,
            Fret = fret,
            GraceFret = graceFret,
            GraceSlide = graceSlide
        };
    }

    [Fact]
    public void SlideGrace_BecomesARealSlidingNoteIntoTheTarget()
    {
        // "15 sl. 17": a grace at fret 15 sliding into the target at 17.
        var target = Target(fret: 17, graceFret: 15, graceSlide: true);
        var level = new ChartLevel();
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        Assert.Equal(2, level.Notes.Count);
        var grace = level.Notes.Single(n => n.Fret == 15);
        Assert.Equal(17, grace.SlideTo);            // slides up into the target fret
        Assert.True(grace.LinkNext);                // reads as a slide-in
        Assert.Equal(1, grace.StringIndex);         // same string as the target
        Assert.True(grace.Position.CompareTo(target.Position) < 0); // before the target
        Assert.Equal(target.Position, grace.Position + grace.Sustain); // slide travels up to it

        // The target is a real landing note now — the hint is fully consumed.
        Assert.Null(target.GraceFret);
        Assert.False(target.GraceSlide);
    }

    [Fact]
    public void PlainGrace_StaysANotationHint()
    {
        var target = Target(fret: 17, graceFret: 15, graceSlide: false);
        var level = new ChartLevel();
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        Assert.Single(level.Notes);          // no note materialized
        Assert.Equal(15, target.GraceFret);  // hint preserved
    }

    [Fact]
    public void SameFretGrace_CannotSlide_StaysAHint()
    {
        var target = Target(fret: 17, graceFret: 17, graceSlide: true);
        var level = new ChartLevel();
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        Assert.Single(level.Notes);
        Assert.Equal(17, target.GraceFret);
        Assert.False(target.GraceSlide);
    }

    [Fact]
    public void MaterializedGrace_KeptThroughTheFullPipeline_AndTargetKeepsSustain()
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var target = Target(fret: 17, graceFret: 15, graceSlide: true);
        target.NotationDuration = new Fraction(2, 1);
        var level = new ChartLevel();
        level.Notes.Add(target);

        GpPostProcessor.Run(level, arr);

        Assert.Equal(2, level.Notes.Count);
        var grace = level.Notes.Single(n => n.Fret == 15);
        Assert.Equal(17, grace.SlideTo);
        // The grace and its landing target both keep their sustain (the slide
        // and its target render across it) — neither is trimmed to a strike.
        Assert.True(grace.Sustain.CompareTo(FractionalPosition.Zero) > 0);
        Assert.True(level.Notes.Single(n => n.Fret == 17).Sustain.CompareTo(FractionalPosition.Zero) > 0);
    }
}
