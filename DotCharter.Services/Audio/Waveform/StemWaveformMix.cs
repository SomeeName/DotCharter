namespace DotCharter.Services.Audio.Waveform;

/// <summary>One channel of the mixer as the waveform needs to see it.</summary>
/// <param name="Name">The stem's name; <c>Original</c> for the untouched mix.</param>
/// <param name="IsMasterMix">True for the original-mix channel.</param>
/// <param name="Gain">Linear gain, 1.0 = unity.</param>
/// <param name="Muted">The channel is silenced.</param>
/// <param name="Soloed">The channel is soloed; any solo silences everything unsoloed.
/// A soloed channel that is also <paramref name="Muted" /> stays silent — see
/// <see cref="StemWaveformMix.Audible" />.</param>
public readonly record struct StemMixChannel(string Name, bool IsMasterMix, double Gain, bool Muted, bool Soloed);

/// <summary>
///     Builds the waveform of what the mixer is actually PLAYING, rather than
///     of the file on disk.
///     <para>
///         Summing the per-stem peaks is the right model here, not an
///         approximation chosen for convenience: source separation is defined by
///         the stems summing back to the input, so htdemucs' six stems at unity
///         gain reconstruct the original mix. Mute one and the drawing loses
///         exactly what the ear loses.
///     </para>
///     <para>
///         Only LEVEL 0 is mixed. <see cref="WaveformSamples.FromBasePeaks" />
///         rebuilds both pyramids from it — the type's own rule that there is a
///         single derivation, so a mixed waveform cannot disagree with a
///         generated one about how a level is halved.
///     </para>
/// </summary>
public static class StemWaveformMix
{
    /// <summary>
    ///     Which channels are audible, with the gain each contributes.
    ///     <para>
    ///         <b>This is the ENGINE's rule, transcribed, and it is not "solo wins
    ///         over mute".</b> It said so, and that sentence was false about this
    ///         app: <c>JuceAudioPlaybackService.ApplyStemGainsLocked</c> silences a
    ///         stem when <c>UserMuted || (anySolo &amp;&amp; !UserSoloed)</c> — mute
    ///         FIRST, so a stem that is both muted and soloed is silent. The drawing
    ///         included it, so the timeline showed a stem you could not hear.
    ///     </para>
    ///     <para>
    ///         Muted-and-soloed is unreachable from the mixer's own strips (each
    ///         setter clears the other), but it is perfectly storable: a
    ///         <c>.dcproj</c> carries <c>stem/@muted</c> and <c>stem/@solo</c>
    ///         independently and <c>BuildStrips</c> seeds the strip from both
    ///         without reconciling them.
    ///     </para>
    ///     <para>
    ///         The original-mix channel is included like any other — it is a real
    ///         source in this engine, and un-muting it while stems play really does
    ///         sum both. It cannot itself be soloed, so <c>ApplyGain</c>'s
    ///         <c>_masterMixMuted || anySolo</c> is the same expression.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<StemMixChannel> Audible(IReadOnlyList<StemMixChannel> channels)
    {
        if (channels.Count == 0) return Array.Empty<StemMixChannel>();

        var soloing = channels.Any(c => c.Soloed);
        var result = new List<StemMixChannel>(channels.Count);
        foreach (var channel in channels)
        {
            if (channel.Muted || (soloing && !channel.Soloed)) continue;
            if (channel.Gain <= 0) continue;
            result.Add(channel);
        }

        return result;
    }

    /// <summary>
    ///     Sums the given waveforms at their gains into one.
    ///     <para>
    ///         Peaks are magnitudes in <c>[0,1]</c> and the sum is clamped to 1 —
    ///         a mixer that overdrives the master draws a flat top, which is what
    ///         it sounds like. Sources whose base block width differs are
    ///         REFUSED rather than resampled: the pyramid's own cache rule is
    ///         that a differing width is a miss, never something to reinterpret.
    ///     </para>
    ///     <para>
    ///         Returns <see cref="WaveformSamples.Empty" /> for no usable source —
    ///         which is exactly right when everything is muted: silence draws as
    ///         nothing.
    ///     </para>
    /// </summary>
    public static WaveformSamples Mix(IReadOnlyList<(WaveformSamples Waveform, double Gain)> parts)
    {
        if (parts.Count == 0) return WaveformSamples.Empty;

        double? blockMs = null;
        var usable = new List<(float[] Peaks, float Gain)>(parts.Count);
        var length = 0;

        foreach (var (waveform, gain) in parts)
        {
            if (!waveform.HasData || gain <= 0) continue;
            blockMs ??= waveform.BaseBlockMs;
            if (Math.Abs(waveform.BaseBlockMs - blockMs.Value) > 1e-9) continue;

            var peaks = waveform.Mipmaps[0];
            usable.Add((peaks, (float)gain));
            if (peaks.Length > length) length = peaks.Length;
        }

        if (usable.Count == 0 || length == 0 || blockMs is not { } width) return WaveformSamples.Empty;

        // One source at unity is the source itself — no reason to walk it.
        if (usable.Count == 1 && Math.Abs(usable[0].Gain - 1f) < 1e-6f)
            return WaveformSamples.FromBasePeaks(usable[0].Peaks, width);

        var mixed = new float[length];
        foreach (var (peaks, gain) in usable)
        {
            for (var i = 0; i < peaks.Length; i++)
                mixed[i] += peaks[i] * gain;
        }

        for (var i = 0; i < mixed.Length; i++)
        {
            if (mixed[i] > 1f)
                mixed[i] = 1f;
        }

        return WaveformSamples.FromBasePeaks(mixed, width);
    }
}
