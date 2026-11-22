using JuceSharp.Dsp;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Per-frame onset evidence of a vocal stem at a 5 ms hop: spectral flux over the whole
///     band, flux in the 2-8 kHz consonant band, and a voicing rise (the increase of a
///     normalised autocorrelation peak in the singing pitch range, pYIN-style periodicity).
///     <para>
///         Frame <c>k</c>'s flux compares frame <c>k</c> with frame <c>k - 1</c>, so a rise
///         that starts at time <c>t</c> peaks near the frame centred on <c>t</c>
///         (<see cref="FrameSeconds" />). Pure: samples in, three float arrays out.
///     </para>
/// </summary>
public sealed class VocalOnsetFeatures
{
    /// <summary>Hop between frames, seconds.</summary>
    public const double HopSeconds = 0.005;

    private const double WindowSeconds = 0.032;
    private const double ConsonantLowHz = 2000;
    private const double ConsonantHighHz = 8000;
    private const double LowestPitchHz = 70;
    private const double HighestPitchHz = 1000;
    private const float LogScale = 10f;

    private readonly float[] _flux;
    private readonly float[] _consonantFlux;
    private readonly float[] _voicingRise;

    private VocalOnsetFeatures(float[] flux, float[] consonantFlux, float[] voicingRise, double hopSeconds)
    {
        _flux = flux;
        _consonantFlux = consonantFlux;
        _voicingRise = voicingRise;
        FrameHopSeconds = hopSeconds;
    }

    /// <summary>The hop actually used (the 5 ms hop rounded to whole samples).</summary>
    public double FrameHopSeconds { get; }

    /// <summary>Number of frames.</summary>
    public int FrameCount => _flux.Length;

    /// <summary>Builds the features from precomputed arrays (tests).</summary>
    public static VocalOnsetFeatures FromArrays(float[] flux, float[] consonantFlux, float[] voicingRise, double hopSeconds)
    {
        ArgumentNullException.ThrowIfNull(flux);
        ArgumentNullException.ThrowIfNull(consonantFlux);
        ArgumentNullException.ThrowIfNull(voicingRise);
        if (consonantFlux.Length != flux.Length || voicingRise.Length != flux.Length)
            throw new ArgumentException("The three feature arrays must have the same length.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopSeconds);

        return new VocalOnsetFeatures(flux, consonantFlux, voicingRise, hopSeconds);
    }

    /// <summary>Time of the centre of frame <paramref name="frame" />.</summary>
    public double FrameSeconds(int frame) => frame * FrameHopSeconds;

    /// <summary>The frame whose centre is nearest <paramref name="seconds" />, clamped.</summary>
    public int FrameAt(double seconds) =>
        _flux.Length == 0 ? 0 : Math.Clamp((int)Math.Round(seconds / FrameHopSeconds), 0, _flux.Length - 1);

    /// <summary>Whole-band log-magnitude flux of frame <paramref name="frame" />.</summary>
    public float Flux(int frame) => _flux[frame];

    /// <summary>2-8 kHz log-magnitude flux of frame <paramref name="frame" />.</summary>
    public float ConsonantFlux(int frame) => _consonantFlux[frame];

    /// <summary>Increase of the periodicity (0..1) from frame <c>k - 1</c> to frame <paramref name="frame" />.</summary>
    public float VoicingRise(int frame) => _voicingRise[frame];

    /// <summary>
    ///     Computes the features of a mono signal. Frames are centred on <c>k * hop</c>;
    ///     samples outside the signal read as silence. With <paramref name="around" />, only the
    ///     frames within <paramref name="reachSeconds" /> of one of those times are computed and the
    ///     rest read as zero: a song's onsets need a fraction of its frames.
    /// </summary>
    public static VocalOnsetFeatures FromSamples(
        ReadOnlySpan<float> samples, int sampleRate, IReadOnlyList<double>? around = null, double reachSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var hop = Math.Max(1, (int)Math.Round(HopSeconds * sampleRate));
        var window = Math.Max(16, (int)Math.Round(WindowSeconds * sampleRate));
        var size = 1;
        while (size < 2 * window) size <<= 1;

        var fft = new JuceFft(size);
        var hann = Windows.Hann(window);
        var frames = samples.Length / hop;
        var bins = size / 2 + 1;
        var binHz = (double)sampleRate / size;
        var lowBin = Math.Clamp((int)Math.Ceiling(ConsonantLowHz / binHz), 1, bins - 1);
        var highBin = Math.Clamp((int)Math.Floor(ConsonantHighHz / binHz), lowBin, bins - 1);
        var minLag = Math.Max(1, (int)Math.Floor(sampleRate / HighestPitchHz));
        var maxLag = Math.Min(window - 1, (int)Math.Ceiling(sampleRate / LowestPitchHz));

        var flux = new float[frames];
        var consonant = new float[frames];
        var rise = new float[frames];
        var previous = new float[bins];
        var current = new float[bins];
        var frame = new float[window];
        var re = new float[size];
        var im = new float[size];
        var power = new float[bins];
        var zero = new float[bins];
        var previousVoicing = 0f;
        var needed = NeededFrames(frames, hop / (double)sampleRate, around, reachSeconds);
        var hasPrevious = false;

        for (var k = 0; k < frames; k++)
        {
            if (needed != null && !needed[k])
            {
                hasPrevious = false;
                continue;
            }

            var start = k * hop - window / 2;
            for (var n = 0; n < window; n++)
            {
                var index = start + n;
                frame[n] = index >= 0 && index < samples.Length ? samples[index] * hann[n] : 0f;
            }

            fft.ForwardReal(frame, re, im);
            var all = 0f;
            var high = 0f;
            for (var b = 0; b < bins; b++)
            {
                power[b] = re[b] * re[b] + im[b] * im[b];
                current[b] = MathF.Log(1f + LogScale * MathF.Sqrt(power[b]));
                var increase = Math.Max(0f, current[b] - previous[b]);
                all += increase;
                if (b >= lowBin && b <= highBin) high += increase;
            }

            // The power spectrum's inverse is the autocorrelation (zero-padded, so not circular).
            fft.InverseReal(power, zero, re, im);
            var energy = re[0];
            var best = 0f;
            if (energy > 1e-9f)
            {
                for (var lag = minLag; lag <= maxLag; lag++)
                    best = Math.Max(best, re[lag] / energy);
            }

            var voicing = Math.Clamp(best, 0f, 1f);
            flux[k] = hasPrevious ? all : 0f;
            consonant[k] = hasPrevious ? high : 0f;
            rise[k] = hasPrevious ? Math.Max(0f, voicing - previousVoicing) : 0f;
            previousVoicing = voicing;
            hasPrevious = true;
            (previous, current) = (current, previous);
        }

        return new VocalOnsetFeatures(flux, consonant, rise, hop / (double)sampleRate);
    }

    /// <summary>Frames within reach of a time, plus the one before each run (its flux needs it); null = all.</summary>
    private static bool[]? NeededFrames(int frames, double hopSeconds, IReadOnlyList<double>? around, double reachSeconds)
    {
        if (around == null) return null;

        var needed = new bool[frames];
        foreach (var seconds in around)
        {
            var from = Math.Max(0, (int)Math.Floor((seconds - reachSeconds) / hopSeconds) - 1);
            var to = Math.Min(frames - 1, (int)Math.Ceiling((seconds + reachSeconds) / hopSeconds));
            for (var k = from; k <= to; k++) needed[k] = true;
        }

        return needed;
    }
}
