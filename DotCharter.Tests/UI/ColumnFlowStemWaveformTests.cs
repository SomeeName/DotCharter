using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     Design D's mini stem waveform squeezes the whole song into one strip: each column is the loudest block it
///     covers (a short transient is never averaged away), from the coarsest pyramid level that still fills the strip.
/// </summary>
public sealed class ColumnFlowStemWaveformTests
{
    [Fact]
    public void EachColumn_IsTheLoudestBlockItCovers()
    {
        var peaks = new float[64];
        peaks[5] = 0.9f; // one transient in the first quarter
        peaks[40] = 0.3f;
        var samples = WaveformSamples.FromBasePeaks(peaks, 2.0);

        var columns = ColumnFlowStemWaveform.Columns(samples, 4);

        Assert.Equal([0.9f, 0f, 0.3f, 0f], columns);
    }

    [Fact]
    public void NoData_DrawsNoColumns()
    {
        Assert.Empty(ColumnFlowStemWaveform.Columns(null, 10));
        Assert.Empty(ColumnFlowStemWaveform.Columns(WaveformSamples.Empty, 10));
        Assert.Empty(ColumnFlowStemWaveform.Columns(WaveformSamples.FromBasePeaks([1f], 2.0), 0));
    }
}
