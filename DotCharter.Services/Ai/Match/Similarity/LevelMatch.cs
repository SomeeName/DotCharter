namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     The one level convention the match lane uses: <b>RMS normalisation to
///     −20 dBFS, with a peak guard at −0.5 dBFS</b>.
///     <para>
///         <b>RMS, not peak, and the choice matters more than it looks.</b> The whole
///         point of the comparison is that one side is distorted and the other may
///         not be — and a distorted signal's crest factor is 10–15 dB lower than a
///         clean one's. Peak-normalise the two and the distorted render arrives
///         10 dB louder in every band, so the mel and MFCC blocks would report a
///         spectral difference that is really just the level difference the
///         distortion created. Every candidate rig would then be graded partly on how
///         much it squashes, which is the very thing the distortion block is supposed
///         to measure honestly.
///     </para>
///     <para>
///         The peak guard exists because a rig can hand back a render whose peaks
///         reach +6 dBFS (<c>ProcessOffline</c> has no limiter on it, by design), and
///         a clipped WAV written for a listening check is not the audio that was
///         scored. It only ever attenuates, so it cannot undo the RMS match by more
///         than the amount needed to stop the clip.
///     </para>
/// </summary>
public static class LevelMatch
{
    /// <summary>
    ///     The RMS every clip is brought to.
    ///     <para>
    ///         <b>−26 and not −20, and the six dB are headroom for CREST.</b> A dry
    ///         guitar DI's peak sits well above its RMS — the app's own drive estimator
    ///         calibrates 18 dB as "clean", and the generated reference DI measures
    ///         23.9 dB. At a −20 dBFS RMS target that peaks ABOVE the guard, so the
    ///         guard binds on ordinary material and the RMS match silently does not
    ///         happen, which is the worst of both conventions (measured: −24.36 dBFS
    ///         RMS from a −24 target). At −26 the guard is what it is meant to be: a
    ///         backstop for a rig that hands back something extreme, not a second level
    ///         rule that fires every time.
    ///     </para>
    /// </summary>
    public const double TargetRmsDbfs = -26.0;

    /// <summary>The ceiling the peak guard enforces after the RMS match.</summary>
    public const double PeakCeilingDbfs = -0.5;

    /// <summary>
    ///     Returns <paramref name="audio" /> at <see cref="TargetRmsDbfs" />, never
    ///     above <see cref="PeakCeilingDbfs" />. Silence is returned unchanged — a
    ///     gain that would make silence reach the target is division by nothing.
    /// </summary>
    public static MonoAudio ToAnalysisLevel(MonoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.Samples.Length == 0) return audio;

        double sq = 0, peak = 0;
        foreach (var s in audio.Samples)
        {
            if (!float.IsFinite(s)) continue;
            sq += s * (double)s;
            peak = Math.Max(peak, Math.Abs(s));
        }

        var rms = Math.Sqrt(sq / audio.Samples.Length);
        if (rms < 1e-9 || peak <= 0) return audio;

        var gain = Math.Pow(10.0, TargetRmsDbfs / 20.0) / rms;
        var ceiling = Math.Pow(10.0, PeakCeilingDbfs / 20.0);
        if (peak * gain > ceiling) gain = ceiling / peak;

        var scaled = new float[audio.Samples.Length];
        for (var i = 0; i < scaled.Length; i++)
        {
            var v = audio.Samples[i];
            scaled[i] = float.IsFinite(v) ? (float)(v * gain) : 0f;
        }

        return new MonoAudio(scaled, audio.SampleRate);
    }
}
