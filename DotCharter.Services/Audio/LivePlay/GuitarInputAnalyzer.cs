using JuceSharp.Dsp;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>One detected guitar event (attack or legato transition) from the
/// raw input signal, in app-level terms.</summary>
public readonly record struct LiveDetectionEvent(
    bool IsLegato,
    ulong StreamSamplePosition,
    double StreamSeconds,
    // Detected pitch candidates with salience, strongest first
    // (empty = percussive).
    LivePitch[] Pitches,
    float TopFrequencyHz,
    float[] Chroma,
    float SpectralCentroidHz,
    float Rms,
    float CrestFactor,
    float SpectralFlatness,
    float DecayRatio,
    LiveTechniqueFlags Techniques)
{
    /// <summary>The detector position of the provisional onset (<see cref="LiveOnsetNotice" />)
    /// this event finalizes; null for a legato event the pitch tracker found with no onset
    /// behind it, and from detectors that publish no provisional onsets.</summary>
    public ulong? DetectorSamplePosition { get; init; }
}

/// <summary>
/// A PROVISIONAL attack: the onset detector fired, ~170 ms before the analyzed
///     <see cref="LiveDetectionEvent" /> that finalizes it — which carries the same
///     <see cref="DetectorSamplePosition" /> (the pairing KEY) in
///     <see cref="LiveDetectionEvent.DetectorSamplePosition" /> — or this onset is
///     <see cref="IsWithdrawn" /> because it will produce no event. <see cref="StreamSamplePosition" />
///     (capture-stream axis) is the attack estimate, snapped onto the RMS rise heard so far. Says
///     WHEN, never WHAT: no pitch.
/// </summary>
public readonly record struct LiveOnsetNotice(
    bool IsWithdrawn,
    ulong DetectorSamplePosition,
    ulong StreamSamplePosition,
    double StreamSeconds,
    float Strength);

/// <summary>
///     The note detector behind ONE live-input capture ring.
///     <para>
///         It exists because there is now more than one: since ABI v28 every
///         seat owns its own ring, and the latency calibration wizard drives
///         its own detector. <see cref="GuitarInputAnalyzer" /> is the product
///         implementation; the seam is what lets a wiring test assert that a
///         seat's ring reaches a detector and its verdicts reach that seat's
///         tally, without synthesising audio that would really have to trip the
///         onset detector (a timing-shaped, flaky test for a question that is
///         not about DSP).
///     </para>
/// </summary>
public interface ILiveInputDetector
{
    /// <summary>Sample rate the detector was built for — the same rate the
    /// capture-stream ↔ host-tick mapping divides by.</summary>
    int SampleRate { get; }

    /// <inheritdoc cref="GuitarInputAnalyzer.Push" />
    IReadOnlyList<LiveDetectionEvent> Push(ReadOnlySpan<float> samples, ulong blockStreamPosition);
}

/// <summary>
///     Thin adapter over the JuceSharp backend's <see cref="JuceGuitarAnalyzer"/>
///     (spectral-flux onsets + iterative harmonic-salience multi-F0 + technique
///     classification). All DSP lives in JuceSharp.Dsp; this class only anchors
///     the analyzer to the native capture ring's ABSOLUTE stream positions
///     (re-syncing after a ring-overrun skip) and maps the event model.
/// </summary>
public sealed class GuitarInputAnalyzer : ILiveInputDetector
{
    private readonly JuceGuitarAnalyzer _inner;
    private readonly List<JuceOnsetNotice> _rawNotices = [];
    private readonly List<LiveOnsetNotice> _notices = [];

    /// <summary>Detected onsets nothing has finalized or withdrawn yet — withdrawn here when a
    /// ring skip resets the analyzer, which drops them without a word. A few at most (the
    /// analyzer's ~190 ms finalize queue).</summary>
    private readonly List<LiveOnsetNotice> _outstanding = [];

    private const int OutstandingCap = 64;

    public GuitarInputAnalyzer(int sampleRate) => _inner = new JuceGuitarAnalyzer(sampleRate);

    public int SampleRate => _inner.SampleRate;

    /// <summary>Absolute capture-stream position consumed so far.</summary>
    public ulong StreamPosition => _inner.StreamPosition;

    /// <summary>The provisional onsets the last <see cref="Push" /> reported, in stream order:
    /// each detected onset at once, then its withdrawal if it produces no event. Reused —
    /// read before the next push.</summary>
    public IReadOnlyList<LiveOnsetNotice> OnsetNotices => _notices;

    public void Reset(ulong streamPosition = 0)
    {
        _notices.Clear();
        ResetInner(streamPosition);
    }

    private void ResetInner(ulong streamPosition)
    {
        _inner.Reset(streamPosition);
        // The analyzer dropped its pending onsets: none of them will be finalized now.
        foreach (var n in _outstanding) _notices.Add(n with { IsWithdrawn = true });
        _outstanding.Clear();
    }

    /// <summary>
    ///     Pushes a capture block whose first sample sits at
    ///     <paramref name="blockStreamPosition"/> on the native capture axis
    ///     (from the position-reporting stream read). A mismatch with the
    ///     analyzer's own position means the ring skipped — the analyzer is
    ///     re-anchored so event positions stay on the capture axis.
    /// </summary>
    public IReadOnlyList<LiveDetectionEvent> Push(ReadOnlySpan<float> samples, ulong blockStreamPosition)
    {
        _notices.Clear();
        if (blockStreamPosition != _inner.StreamPosition)
            ResetInner(blockStreamPosition);

        _rawNotices.Clear();
        var raw = _inner.Push(samples, _rawNotices);
        foreach (var n in _rawNotices)
        {
            var notice = new LiveOnsetNotice(n.Kind == JuceOnsetNoticeKind.Withdrawn, n.DetectorSamplePosition,
                n.StreamSamplePosition, n.StreamSeconds, n.Strength);
            _notices.Add(notice);
            if (notice.IsWithdrawn)
            {
                Resolve(notice.DetectorSamplePosition);
                continue;
            }

            // Bounded whatever the analyzer does: each onset is resolved once, so this only
            // guards against a contract break growing the list for a whole session.
            if (_outstanding.Count == OutstandingCap) _outstanding.RemoveAt(0);
            _outstanding.Add(notice);
        }

        if (raw.Count == 0) return Array.Empty<LiveDetectionEvent>();

        var mapped = new List<LiveDetectionEvent>(raw.Count);
        foreach (var e in raw)
        {
            var pitches = new LivePitch[e.Pitches.Length];
            for (var i = 0; i < pitches.Length; i++)
                pitches[i] = new LivePitch(e.Pitches[i].Midi, e.Pitches[i].FrequencyHz, e.Pitches[i].Salience);
            mapped.Add(new LiveDetectionEvent(
                e.Kind == JuceGuitarEventKind.Legato,
                e.StreamSamplePosition,
                e.StreamSeconds,
                pitches,
                e.Pitches.Length > 0 ? e.Pitches[0].FrequencyHz : 0f,
                e.Chroma,
                e.SpectralCentroidHz,
                e.Rms,
                e.CrestFactor,
                e.SpectralFlatness,
                e.DecayRatio,
                MapTechniques(e.Techniques))
            {
                DetectorSamplePosition = e.DetectorSamplePosition
            });
            if (e.DetectorSamplePosition is { } key) Resolve(key);
        }

        return mapped;
    }

    private void Resolve(ulong detectorSamplePosition)
    {
        for (var i = 0; i < _outstanding.Count; i++)
        {
            if (_outstanding[i].DetectorSamplePosition != detectorSamplePosition) continue;
            _outstanding.RemoveAt(i);
            return;
        }
    }

    private static LiveTechniqueFlags MapTechniques(JuceGuitarTechniques t)
    {
        var flags = LiveTechniqueFlags.None;
        if (t.HasFlag(JuceGuitarTechniques.PalmMute)) flags |= LiveTechniqueFlags.PalmMute;
        if (t.HasFlag(JuceGuitarTechniques.Harmonic)) flags |= LiveTechniqueFlags.Harmonic;
        if (t.HasFlag(JuceGuitarTechniques.PinchHarmonic)) flags |= LiveTechniqueFlags.PinchHarmonic;
        if (t.HasFlag(JuceGuitarTechniques.Percussive)) flags |= LiveTechniqueFlags.Percussive;
        if (t.HasFlag(JuceGuitarTechniques.Slap)) flags |= LiveTechniqueFlags.Slap;
        if (t.HasFlag(JuceGuitarTechniques.Pop)) flags |= LiveTechniqueFlags.Pop;
        if (t.HasFlag(JuceGuitarTechniques.Legato)) flags |= LiveTechniqueFlags.Legato;
        return flags;
    }
}
