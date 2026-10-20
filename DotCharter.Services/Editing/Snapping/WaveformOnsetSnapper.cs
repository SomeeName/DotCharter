namespace DotCharter.Services.Editing.Snapping;

/// <summary>
/// Finds audio onsets (note/word attacks) in the timeline waveform envelope so
/// the vocal editor can snap syllables to where the singing actually starts,
/// not just to the beat grid. Pure and allocation-light: it runs a simple
/// half-wave-rectified flux detector over the ~<see cref="AnalysisBlockMs"/>
/// level of the <see cref="WaveformSamples"/> mipmap (peak amplitudes,
/// normalized 0..1) and returns the nearest significant onset to a target time.
/// </summary>
public sealed class WaveformOnsetSnapper
{
    /// <summary>Onset flux must reach this fraction of the strongest flux in the search window.</summary>
    public const double DefaultSensitivity = 0.35;

    /// <summary>Absolute floor on onset flux (normalized amplitude rise) to reject noise.</summary>
    private const double MinAbsoluteFlux = 0.04;

    /// <summary>
    ///     The envelope resolution the flux thresholds are tuned for — the
    ///     pyramid's historic BASE block, now pinned explicitly.
    ///     <para>This must NOT follow the base block. The renderer's pyramid is
    ///     generated far finer than this (a 2 ms block is roughly a sixth of a
    ///     low-E period, so its peak ripples with the waveform's own cycle), and
    ///     reading level 0 blindly would feed that ripple to the flux detector as
    ///     dozens of spurious local maxima per note. The snapper asks for the
    ///     level nearest this width instead, so its behaviour is a function of
    ///     the AUDIO, not of whatever resolution the renderer currently wants.
    ///     Onset snapping is deliberately lyric-only — this keeps it that way by
    ///     construction.</para>
    /// </summary>
    public const double AnalysisBlockMs = 20.0;

    /// <summary>
    /// Returns the millisecond position of the onset nearest to
    /// <paramref name="targetMs"/> within ±<paramref name="radiusMs"/>, or
    /// <c>null</c> when no attack stands out in that window (the caller should
    /// then keep its grid snap).
    /// </summary>
    public double? FindNearestOnsetMs(WaveformSamples? waveform, double targetMs, double radiusMs,
        double sensitivity = DefaultSensitivity)
    {
        if (waveform is null || !waveform.HasData || radiusMs <= 0) return null;
        var level = waveform.PickLevelNearest(AnalysisBlockMs);
        var env = waveform.Mipmaps[level];
        var blockMs = waveform.BaseBlockMs * (1 << level);
        if (env.Length < 3 || blockMs <= 0) return null;

        // Block window covering [target - radius, target + radius]; need index-1
        // for the flux difference, so start at 1.
        var startBlock = Math.Max(1, (int)Math.Floor((targetMs - radiusMs) / blockMs));
        var endBlock = Math.Min(env.Length - 2, (int)Math.Ceiling((targetMs + radiusMs) / blockMs));
        if (startBlock > endBlock) return null;

        // First pass: strongest flux in the window sets the relative threshold.
        var maxFlux = 0.0;
        for (var i = startBlock; i <= endBlock; i++)
        {
            var flux = env[i] - env[i - 1];
            if (flux > maxFlux) maxFlux = flux;
        }
        if (maxFlux < MinAbsoluteFlux) return null;

        var threshold = Math.Max(MinAbsoluteFlux, sensitivity * maxFlux);

        // Second pass: local flux maxima above threshold are onsets; keep the
        // one whose time is closest to the target.
        double? bestMs = null;
        var bestDelta = double.MaxValue;
        for (var i = startBlock; i <= endBlock; i++)
        {
            var flux = env[i] - env[i - 1];
            if (flux < threshold) continue;
            var prevFlux = i >= 2 ? env[i - 1] - env[i - 2] : 0.0;
            var nextFlux = env[i + 1] - env[i];
            if (flux < prevFlux || flux < nextFlux) continue; // not a local peak

            var onsetMs = i * blockMs;
            var delta = Math.Abs(onsetMs - targetMs);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestMs = onsetMs;
            }
        }

        return bestMs;
    }
}
