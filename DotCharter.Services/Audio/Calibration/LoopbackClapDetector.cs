using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Audio.Calibration;

/// <summary>
///     The onset detector the LOOPBACK calibration listens with: a level
///     detector for one known signal, not a note analyser.
///     <para>
///         <b>Why.</b> Output looped into input 1 of
///         a Focusrite, "it didn't detect the clicks". The wizard analysed the
///         ring with <see cref="GuitarInputAnalyzer" /> — the note detector — whose
///         absolute attack gate (<c>MinAttackRms</c> = 0.01) exists to keep hiss
///         from scoring as a pluck. The engine's clap is 90 ms of white noise at
///         base gain 0.45, and measured through that analyser (<c>Services/Audio/
///         Calibration/LoopbackClapDetectionTests</c>) it is heard at unity, -10
///         and -20 dB and NOT at -30 dB or below: an interface with its output
///         and input knobs anywhere moderate returns the clap right around that
///         line, and the wizard then reported "no click heard" while the meter
///         moved. A clap is the loudest, most broadband thing the ring will see
///         during a run; the only question is "did the level jump", and this
///         answers that against the ring's OWN noise floor rather than an
///         absolute playing-level gate.
///     </para>
///     <para>
///         Per hop of <see cref="HopSize" /> samples the RMS is compared with the
///         tracked floor: a hop <see cref="FloorRatio" />× above it (and above the
///         <see cref="AbsoluteFloor" />, so a dead-silent digital loop cannot fire
///         on quantisation noise) starts an event at the first sample of the hop
///         that crossed <see cref="RefineRatio" />× the floor. The floor only
///         learns from quiet hops, so the clap never raises its own bar; a
///         <see cref="RefractoryMs" /> hold keeps one clap from reporting twice.
///         Pure, allocation-light and deterministic.
///     </para>
/// </summary>
public sealed class LoopbackClapDetector : ILiveInputDetector
{
    /// <summary>Analysis hop — 64 samples is 1.3 ms at 48 kHz, the timing
    /// resolution a round-trip measurement gets before refinement.</summary>
    public const int HopSize = 64;

    /// <summary>A hop must be this many times above the floor to be a clap
    /// (4× ≈ +12 dB).</summary>
    public float FloorRatio { get; init; } = 4f;

    /// <summary>Within the firing hop, the first sample above this many times
    /// the floor is the onset.</summary>
    public float RefineRatio { get; init; } = 2f;

    /// <summary>RMS below which nothing is ever a clap (≈ -70 dBFS): the
    /// bar for a ring that carries no noise at all.</summary>
    public float AbsoluteFloor { get; init; } = 0.0003f;

    /// <summary>Hold after an onset during which nothing else fires: the
    /// clap's own length, so its decay is never a second onset. Anything after
    /// that is the SESSION's to ignore (an accepted echo puts it in Settling),
    /// which is why this is not longer.</summary>
    public double RefractoryMs { get; init; } = 90;

    /// <summary>Hops the floor learns from before the first clap can fire
    /// (≈ 27 ms at 48 kHz): the ring's resting level.</summary>
    public int WarmupHops { get; init; } = 20;

    private readonly float[] _hop = new float[HopSize];
    private readonly long _refractorySamples;
    private readonly List<LiveDetectionEvent> _events = [];
    private readonly float[] _noChroma = new float[12];
    private int _hopFill;
    private ulong _hopStart;
    private int _hopsSeen;
    private float _floor;
    private ulong _lastOnset;
    private bool _hasOnset;

    public LoopbackClapDetector(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        _refractorySamples = (long)(RefractoryMs / 1000.0 * sampleRate);
    }

    public int SampleRate { get; }

    /// <summary>The tracked resting RMS of the ring — what the last completed
    /// run's clap had to beat. Diagnostic.</summary>
    public float NoiseFloor => _floor;

    public IReadOnlyList<LiveDetectionEvent> Push(ReadOnlySpan<float> samples, ulong blockStreamPosition)
    {
        _events.Clear();
        for (var i = 0; i < samples.Length; i++)
        {
            if (_hopFill == 0) _hopStart = blockStreamPosition + (ulong)i;
            _hop[_hopFill++] = samples[i];
            if (_hopFill < HopSize) continue;
            _hopFill = 0;
            AnalyseHop();
        }
        return _events.Count == 0 ? [] : _events.ToArray();
    }

    private void AnalyseHop()
    {
        double sum = 0;
        for (var i = 0; i < HopSize; i++) sum += (double)_hop[i] * _hop[i];
        var rms = (float)Math.Sqrt(sum / HopSize);
        _hopsSeen++;

        var floor = Math.Max(_floor, AbsoluteFloor);
        var inRefractory = _hasOnset && _hopStart - _lastOnset < (ulong)_refractorySamples;
        var loud = _hopsSeen > WarmupHops && rms > floor * FloorRatio;

        if (loud && !inRefractory)
        {
            var refine = floor * RefineRatio;
            var offset = 0;
            for (var i = 0; i < HopSize; i++)
                if (Math.Abs(_hop[i]) > refine) { offset = i; break; }
            var pos = _hopStart + (ulong)offset;
            _lastOnset = pos;
            _hasOnset = true;
            _events.Add(new LiveDetectionEvent(
                IsLegato: false,
                StreamSamplePosition: pos,
                StreamSeconds: pos / (double)SampleRate,
                Pitches: [],
                TopFrequencyHz: 0f,
                Chroma: _noChroma,
                SpectralCentroidHz: 0f,
                Rms: rms,
                CrestFactor: 0f,
                SpectralFlatness: 1f,
                DecayRatio: 0f,
                Techniques: LiveTechniqueFlags.Percussive));
            return;
        }

        // The floor learns from quiet hops only: never from a clap, never from
        // its ring-out. Fast to seed, slow to drift, so a run that starts with
        // a stray knock still settles inside the warm-up.
        if (loud || inRefractory) return;
        if (_hopsSeen == 1) { _floor = rms; return; }
        var alpha = _hopsSeen <= WarmupHops ? 0.2f : 0.01f;
        _floor += (rms - _floor) * alpha;
    }
}
