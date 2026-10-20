namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
///     The cabinet's response guide as pure maths: the native <c>cab</c> module's
///     low / high-cut filter, drawn by <c>RigFilterResponsePlot</c>. (The mic is
///     not here any more: it is the native mic model, <see cref="MicPlacementMath" />.)
/// </summary>
public static class CabinetFilterMath
{
    /// <summary>The cabinet's low/high-cut filter gain at <paramref name="frequencyHz" />
    /// in dB — two second-order Butterworth edges, as the design's response guide draws.</summary>
    public static double FilterGainDb(double frequencyHz, double lowCutHz, double highCutHz)
    {
        if (frequencyHz <= 0) return -120;
        var low = -10.0 * Math.Log10(1.0 + Math.Pow(lowCutHz / frequencyHz, 4));
        var high = -10.0 * Math.Log10(1.0 + Math.Pow(frequencyHz / highCutHz, 4));
        return low + high;
    }
}
