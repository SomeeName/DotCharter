namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
/// How SURE the detector was of one verdict, on one 0..1 scale for hits and misses
///     alike, so the analysis monitor can tell a player whether a miss was their playing or the
///     detector. It reads only what the judging path already computed and changes no verdict.
///     <list type="bullet">
///         <item>A HIT is as certain as its verifying evidence: <see cref="LivePlayEvent.Confidence" />
///               (the CQT verifier's <c>1 − gate/level</c>, a chord's weighted coverage, or the
///               classic path's candidate salience).</item>
///         <item>A MISS is certain in proportion to how LITTLE the verifier found:
///               <c>1 − </c><see cref="LivePlayEvent.MissEvidence" />. Nothing at the charted pitch
///               is a sure miss; a bin that cleared its floor but failed the peak test is not.</item>
///         <item>A miss no verifier looked at (the classic path, a pitchless sound) has NO
///               certainty — null, never a confident 1 it did not earn.</item>
///     </list>
///     <see cref="UnsureBelow" /> is a DISPLAY threshold, not a detection one: every verdict, and
///     so every detection score measured against the corpus, is exactly
///     what it was. What this adds is the evidence each verdict was ALREADY decided on, shown.
/// </summary>
public static class LiveDetectionCertainty
{
    /// <summary>Below this a verdict (or a string's mean) reads "low confidence": its evidence was
    /// nearer the other verdict than this one.</summary>
    public const float UnsureBelow = 0.5f;

    /// <summary>The verdict's certainty, or null when the detector gathered no evidence for it.</summary>
    public static float? Of(in LivePlayEvent ev)
    {
        if (ev.Verdict != LivePlayVerdict.Miss) return Clamp01(ev.Confidence);
        return ev.MissEvidence is { } evidence ? 1f - Clamp01(evidence) : null;
    }

    /// <summary>A miss the detector was unsure of: it found evidence for the note and still
    /// called it a miss — the one a player should not blame on their playing.</summary>
    public static bool IsUnsureMiss(in LivePlayEvent ev) =>
        ev.Verdict == LivePlayVerdict.Miss && Of(ev) is < UnsureBelow;

    private static float Clamp01(float value) => float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
}

/// <summary>One string's reading: the mean <see cref="LiveDetectionCertainty" /> of its recent
/// judgments, over <see cref="Samples" /> of them (0 = nothing judged on it recently).</summary>
public readonly record struct LiveStringConfidenceReading(int StringIndex, int Samples, float Certainty)
{
    public bool HasData => Samples > 0;

    public bool IsUnsure => HasData && Certainty < LiveDetectionCertainty.UnsureBelow;
}

/// <summary>
/// The per-string aggregate the analysis monitor shows: for each chart string, the mean
///     certainty of the last <see cref="SamplesPerString" /> judgments ON that string whose chart
///     time lies within <see cref="WindowMs" /> before "now". A chord counts toward every string
///     that carries one of its pitched tones (<see cref="ExpectedSound.StringMask" />), with the
///     chord's one number — the verifier scores a chord as a whole, not per tone.
///     <para>
///         The clock is INJECTED: "now" is the playhead the caller passes to <see cref="Sample" />,
///         and each judgment is stamped with its sound's chart time. A seek back leaves judgments
///         from the future, which are dropped (<see cref="FutureToleranceMs" />), so a rewind needs
///         no bookkeeping. Worker-confined, like the session it listens to; no locks.
///     </para>
/// </summary>
public sealed class LiveStringConfidenceTracker
{
    /// <summary>Strings tracked: a 9-string arrangement is the widest the app charts.</summary>
    public const int MaxStrings = StandardOpenStrings.MaxStringCount;

    /// <summary>Judgments kept per string — enough to steady the mean, few enough to follow a
    /// change of tone or position within a phrase.</summary>
    public const int SamplesPerString = 8;

    /// <summary>How far back (chart time) a judgment still counts. A string not played for this
    /// long reads "no recent notes" rather than a stale verdict.</summary>
    public const double WindowMs = 10_000;

    /// <summary>A miss is judged when its window closes, a quarter second after its chart time,
    /// so "now" trails a judgment by up to that; anything further ahead of now is from before a
    /// seek back and does not describe this pass.</summary>
    public const double FutureToleranceMs = 1_000;

    private readonly (double TimeMs, float Certainty)[,] _ring = new (double, float)[MaxStrings, SamplesPerString];
    private readonly int[] _count = new int[MaxStrings];
    private readonly int[] _next = new int[MaxStrings];

    /// <summary>Records a judged sound on each of its strings. Judgments without a certainty (see
    /// <see cref="LiveDetectionCertainty.Of" />) and sounds on no known string are skipped.</summary>
    public void Record(in LivePlayEvent ev)
    {
        if (LiveDetectionCertainty.Of(ev) is not { } certainty) return;
        var mask = ev.Sound.StringMask;
        if (mask == 0 && ev.Sound.StringIndex is >= 0 and < MaxStrings) mask = 1 << ev.Sound.StringIndex;
        Record(mask, ev.Sound.TimeMs, certainty);
    }

    /// <summary>Records one certainty at <paramref name="timeMs" /> on every string in
    /// <paramref name="stringMask" />.</summary>
    public void Record(int stringMask, double timeMs, float certainty)
    {
        for (var s = 0; s < MaxStrings; s++)
        {
            if ((stringMask & (1 << s)) == 0) continue;
            _ring[s, _next[s]] = (timeMs, certainty);
            _next[s] = (_next[s] + 1) % SamplesPerString;
            if (_count[s] < SamplesPerString) _count[s]++;
        }
    }

    /// <summary>Forgets everything (a new run).</summary>
    public void Clear()
    {
        Array.Clear(_count);
        Array.Clear(_next);
    }

    /// <summary>Every string's reading at chart time <paramref name="nowMs" />.</summary>
    public LiveStringConfidenceReading[] Sample(double nowMs)
    {
        var readings = new LiveStringConfidenceReading[MaxStrings];
        for (var s = 0; s < MaxStrings; s++)
        {
            var samples = 0;
            var sum = 0.0;
            for (var i = 0; i < _count[s]; i++)
            {
                var (timeMs, certainty) = _ring[s, i];
                if (timeMs < nowMs - WindowMs || timeMs > nowMs + FutureToleranceMs) continue;
                samples++;
                sum += certainty;
            }

            readings[s] = new LiveStringConfidenceReading(s, samples, samples == 0 ? 0f : (float)(sum / samples));
        }

        return readings;
    }
}
