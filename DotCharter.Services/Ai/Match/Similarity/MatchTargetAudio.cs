namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     A target clip after its silent head and tail have been cut off, and how much
///     was cut. <see cref="Content" /> is what is left — the only part of the window
///     that is a tone at all.
/// </summary>
/// <param name="Audio">The trimmed clip. Same rate, same samples, fewer of them.</param>
/// <param name="LeadingSilence">How much was cut from the front.</param>
/// <param name="TrailingSilence">How much was cut from the back.</param>
public readonly record struct MatchTarget(MonoAudio Audio, TimeSpan LeadingSilence, TimeSpan TrailingSilence)
{
    /// <summary>How much playing the matcher actually has to work with.</summary>
    public TimeSpan Content => Audio.Duration;

    /// <summary>True when anything was cut — the one condition worth a note.</summary>
    public bool WasTrimmed => LeadingSilence > TimeSpan.Zero || TrailingSilence > TimeSpan.Zero;

    /// <summary>One line for a result's notes, or null when nothing was cut.</summary>
    public string? Note => WasTrimmed
        ? $"target: {Content.TotalSeconds:0.0} s of playing after trimming " +
          $"{LeadingSilence.TotalSeconds:0.0} s of leading and " +
          $"{TrailingSilence.TotalSeconds:0.0} s of trailing silence"
        : null;
}

/// <summary>
///     Prepares the audio a match is measured AGAINST.
///     <para>
///         <b>It exists because a window of song is not a window of playing, and
///         every stage below this one quietly assumed it was.</b> The panel picks a
///         span of the song and hands the isolated stem for it straight to the
///         matcher; on the owner's own project (no tone changes, so the span is the
///         opening of the song) the separated guitar is DIGITAL SILENCE for the
///         first ten seconds and −70 dBFS for five more — half the clip. Measured
///         on <c>stems/guitar.wav</c>, 5 s blocks: −180, −93.5, −17.3,
///         −16.0, −70.7, −30.2 dBFS.
///     </para>
///     <para>
///         <b>Three consumers read that window differently, and only one of them
///         defends itself.</b> <see cref="ToneFeatureExtractor" />'s spectral blocks
///         gate frames at <see cref="ToneFeatureExtractor.FrameGateDb" /> below the
///         loudest, so silence is dropped there; its DYNAMICS and MODULATION blocks
///         are taken over the whole buffer and are not gated at all — and the
///         dynamics block is the one the layout measures as carrying the RIG. And
///         <c>ToneMatcher.SelfDistance</c> slices the raw head and tail, so a silent
///         head embeds as the all-zero vector and the "floor" becomes the distance
///         between nothing and something.
///     </para>
///     <para>
///         Trimming is reported, never silent: <see cref="MatchTarget.Note" /> goes
///         into the result's notes. Nothing is resampled, re-levelled or reordered —
///         this only chooses WHERE the clip starts and stops.
///     </para>
/// </summary>
public static class MatchTargetAudio
{
    /// <summary>The block a level is measured over: short enough that the attack of
    /// one note is not averaged into the rest before it, long enough that a single
    /// zero crossing is not a gap.</summary>
    public const double BlockSeconds = 0.020;

    /// <summary>How far below the loudest block a block may sit and still count as
    /// playing. The same 45 dB <see cref="ToneFeatureExtractor.FrameGateDb" /> uses,
    /// deliberately — two different gates would drop two different halves of the
    /// clip and nothing downstream would say which.</summary>
    public const double GateDb = 45.0;

    /// <summary>
    ///     A block quieter than this is not a quiet note, it is the separator's noise
    ///     floor. Without it the relative gate above turns an all-but-silent clip
    ///     into "content" by normalising its own hiss — which is the exact failure
    ///     this type exists to stop, one level down.
    /// </summary>
    public const double AbsoluteFloorDbfs = -75.0;

    /// <summary>
    ///     <paramref name="audio" /> with its silent head and tail removed.
    ///     <para>
    ///         A clip with no block above the floor comes back EMPTY (and the caller
    ///         decides what to say about it) rather than being handed on as a tone —
    ///         an all-zero clip embeds as an all-zero feature vector, which is a
    ///         perfectly comparable number about nothing.
    ///     </para>
    /// </summary>
    public static MatchTarget Trim(MonoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var rate = audio.SampleRate;
        var samples = audio.Samples;
        if (rate <= 0 || samples.Length == 0)
            return new MatchTarget(audio, TimeSpan.Zero, TimeSpan.Zero);

        var block = Math.Max(1, (int)Math.Round(BlockSeconds * rate));
        var blocks = (samples.Length + block - 1) / block;
        var rms = new double[blocks];
        var peak = 0.0;
        for (var b = 0; b < blocks; b++)
        {
            var start = b * block;
            var end = Math.Min(start + block, samples.Length);
            double sq = 0;
            for (var i = start; i < end; i++)
            {
                var s = float.IsFinite(samples[i]) ? samples[i] : 0f;
                sq += s * (double)s;
            }

            rms[b] = Math.Sqrt(sq / Math.Max(1, end - start));
            peak = Math.Max(peak, rms[b]);
        }

        var absolute = Math.Pow(10.0, AbsoluteFloorDbfs / 20.0);
        var gate = Math.Max(peak * Math.Pow(10.0, -GateDb / 20.0), absolute);

        var first = -1;
        var last = -1;
        for (var b = 0; b < blocks; b++)
        {
            if (rms[b] < gate) continue;
            if (first < 0) first = b;
            last = b;
        }

        if (first < 0)
        {
            return new MatchTarget(
                new MonoAudio([], rate), audio.Duration, TimeSpan.Zero);
        }

        var from = first * block;
        var to = Math.Min((last + 1) * block, samples.Length);
        if (from == 0 && to == samples.Length)
            return new MatchTarget(audio, TimeSpan.Zero, TimeSpan.Zero);

        var slice = new float[to - from];
        Array.Copy(samples, from, slice, 0, slice.Length);
        return new MatchTarget(
            new MonoAudio(slice, rate),
            TimeSpan.FromSeconds(from / (double)rate),
            TimeSpan.FromSeconds((samples.Length - to) / (double)rate));
    }

    /// <summary>
    ///     The shortest stretch of PLAYING a tone can be measured over. Shorter than
    ///     <see cref="ToneSection.MinDuration" /> (4 s, which is a section's SPAN and
    ///     includes its rests) because this is content after trimming; below it the
    ///     spectral average is one chord and the dynamics block is one attack.
    /// </summary>
    public static TimeSpan MinContent { get; } = TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="target" /> holds at least
    /// <see cref="MinContent" /> of playing — i.e. when matching it means anything.</summary>
    public static bool IsMeasurable(MatchTarget target) => target.Content >= MinContent;
}
