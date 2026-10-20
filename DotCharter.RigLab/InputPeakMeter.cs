using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.RigLab;

/// <summary>
///     The rig header's input meter: the absolute peak of the most recent input
///     window. DotCharter reads this through Play Mode's live-play session; RigLab
///     has none, so it reads the capture window itself. Polled by the view at the
///     meter's rate on the UI thread, so the buffer is reused — no allocation per
///     read.
/// </summary>
public sealed class InputPeakMeter(ILiveInputCapture capture)
{
    private readonly float[] _window = new float[2048];

    public float Read()
    {
        int count;
        try { count = capture.ReadInputCaptureWindow(_window); }
        catch (ObjectDisposedException) { return 0f; }
        return Peak(_window, count);
    }

    /// <summary>The largest absolute sample among the first
    /// <paramref name="count" />; 0 for an empty window.</summary>
    public static float Peak(ReadOnlySpan<float> samples, int count)
    {
        var peak = 0f;
        count = Math.Clamp(count, 0, samples.Length);
        for (var i = 0; i < count; i++)
        {
            var a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
        }
        return peak;
    }
}
