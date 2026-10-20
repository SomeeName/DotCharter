using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI;

/// <summary>
///     The waveform's per-frame level-of-detail decision. Pure, so the part of
///     the v0.35 renderer change that decides "how much detail" is provable in
///     the default gate — the pixels themselves need a GPU.
/// </summary>
public sealed class WaveformLodTests
{
    private static WaveformSamples Samples(int blocks, double baseBlockMs)
    {
        var peaks = new float[blocks];
        for (var i = 0; i < blocks; i++) peaks[i] = 0.5f;
        return WaveformSamples.FromBasePeaks(peaks, baseBlockMs);
    }

    [Fact]
    public void NoDataOrNoZoom_IsNothingToDraw()
    {
        Assert.Null(WaveformLod.Compute(null, 1.0));
        Assert.Null(WaveformLod.Compute(WaveformSamples.Empty, 1.0));
        Assert.Null(WaveformLod.Compute(Samples(1024, 2.0), 0));
        Assert.Null(WaveformLod.Compute(Samples(1024, 2.0), -1));
    }

    [Theory]
    [InlineData(0.02)]   // whole-song overview
    [InlineData(0.05)]
    [InlineData(0.2)]    // the default editing zoom
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void AColumnIsNeverNarrowerThanTheTarget(double zoom)
    {
        // Below the target the frame pays for columns finer than the screen can
        // show. The picker only fails this where the pyramid has bottomed out,
        // which none of these zooms reach with a 2 ms base.
        var lod = WaveformLod.Compute(Samples(1 << 16, WaveformSamples.GenerationBlockMs), zoom);
        Assert.True(lod.HasValue);
        Assert.True(lod!.Value.StepWidthPx >= WaveformLod.TargetColumnPx,
            $"zoom {zoom}: column {lod.Value.StepWidthPx} px is finer than the {WaveformLod.TargetColumnPx} px target");
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.2)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AColumnIsNeverWiderThanTwiceTheTarget_UnlessThePyramidBottomsOut(double zoom)
    {
        var samples = Samples(1 << 16, WaveformSamples.GenerationBlockMs);
        var lod = WaveformLod.Compute(samples, zoom)!.Value;

        // Levels are octaves, so "at least the target" implies "under twice the
        // target" — the ONLY escape is level 0, where there is nothing finer.
        Assert.True(lod.StepWidthPx < 2 * WaveformLod.TargetColumnPx || lod.Level == 0,
            $"zoom {zoom}: {lod.StepWidthPx} px columns at level {lod.Level} (a finer level existed)");
    }

    [Fact]
    public void ZoomingIn_WalksDownToTheBaseLevel_AndTheBaseBlockIsWhatStopsIt()
    {
        // The v0.35 fix in one assertion: the finest thing the picker can ever
        // reach is level 0, so the base block IS the resolution ceiling. At the
        // old 20 ms base the editing zooms below already bottomed out (20 px
        // columns at 1 px/ms — the "low res" staircase); at 2 ms they do not.
        var coarse = Samples(1 << 16, 20.0);
        var fine = Samples(1 << 16, WaveformSamples.GenerationBlockMs);

        Assert.Equal(0, WaveformLod.Compute(coarse, 0.2)!.Value.Level);
        Assert.Equal(20.0 * 0.2, WaveformLod.Compute(coarse, 0.2)!.Value.StepWidthPx, 3);
        Assert.Equal(20.0 * 1.0, WaveformLod.Compute(coarse, 1.0)!.Value.StepWidthPx, 3);

        Assert.True(WaveformLod.Compute(fine, 0.2)!.Value.Level > 0);
        Assert.True(WaveformLod.Compute(fine, 0.2)!.Value.StepWidthPx < 3.0f);
        Assert.True(WaveformLod.Compute(fine, 1.0)!.Value.StepWidthPx < 3.0f);
    }

    [Theory]
    [InlineData(2.6, 1.0, false)] // 2.6 px columns — the fill's own edge is fine
    [InlineData(4.0, 1.0, true)]  // 4 px columns — the staircase needs the ribbon
    public void TheSilhouetteRibbonFlipsAtTheColumnWidthThreshold(
        double baseBlockMs, double zoom, bool expected)
    {
        // A single-level pyramid pins the column width exactly, so this tests
        // the threshold and not the picker.
        var samples = new WaveformSamples([new float[64]], baseBlockMs);
        Assert.Equal(expected, WaveformLod.Compute(samples, zoom)!.Value.DrawOutline);
    }

    [Fact]
    public void TheStrokeThicknessCurveIsCapped()
    {
        Assert.Equal(1.0f, WaveformLod.StrokeThickness(1.5f, 7f));           // below the curve's floor
        Assert.Equal((float)Math.Exp(4.0 * 0.2), WaveformLod.StrokeThickness(4f, 7f), 3);
        Assert.Equal(7f, WaveformLod.StrokeThickness(40f, 7f));              // capped, never a heavy band
    }
}
