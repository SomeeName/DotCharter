using System.Globalization;
using System.Text;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using JuceSharp.Dsp;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     What one tone window SOUNDS like, in named quantities a person (or a
///     language model) can reason about — the DSP half of tone generation.
///     <para>
///         <b>Named and in real units, deliberately.</b> <c>tonefx-v2</c>'s 357-element
///         vector is the right thing to compare two tones WITH and the wrong thing to
///         describe one: its elements are normalised into "1.0 is a large perceptual
///         step" and carry no names, so a prompt built out of them would be 357
///         numbers a model can only pattern-match. These are eleven numbers with
///         units.
///     </para>
///     <para>
///         Only <see cref="RmsDbfs" /> and <see cref="PeakDbfs" /> come from the
///         shared extractor (they are its <c>level</c> block, which is the one block
///         in absolute units); the rest are measured here because no named,
///         unit-carrying form of them exists in the vector.
///     </para>
/// </summary>
/// <param name="Seconds">How much audio these describe.</param>
/// <param name="RmsDbfs">Average level. The gain-structure half of "how hard is this
/// hitting the amp".</param>
/// <param name="PeakDbfs">Peak level.</param>
/// <param name="CrestDb">Peak minus RMS. <b>The compression / distortion axis in the
/// time domain</b>: a clean take runs 14–20 dB, a squashed or heavily distorted one
/// under 10.</param>
/// <param name="CentroidHz">The spectrum's centre of mass — "how bright".</param>
/// <param name="Rolloff85Hz">Where 85 % of the energy is below — how far the top
/// extends past the centroid.</param>
/// <param name="TiltDbPerOctave">The straight-line slope of the log-frequency
/// spectrum. <b>Negative is dark</b> (a closed-back cab, the tone control down),
/// near zero is bright and open. The single most gear-diagnostic number here.</param>
/// <param name="Flatness">Spectral flatness in <c>[0,1]</c>: tonal near 0, noise-like
/// near 1. A fuzz and a dying cymbal both push it up, which is why it is read beside
/// <see cref="CrestDb" /> and not alone.</param>
/// <param name="HighMidRatioDb">2–6 kHz against 200–800 Hz. The presence/bite axis a
/// drive pedal's tone control and a cab's mic position both move.</param>
/// <param name="DecaySeconds">How long the envelope takes to fall 20 dB after the
/// loudest onset. <b>The delay / reverb / gate axis</b>: a gated high-gain rhythm
/// part is under 0.3 s, a wash of reverb is seconds. NaN when no onset was found.</param>
/// <param name="ModulationDepth">Peak periodic depth of the level envelope in
/// <c>[0,1]</c> at 0.5–12 Hz. <b>The tremolo / chorus / phaser axis</b>: near zero
/// for a static rig.</param>
/// <param name="ModulationHz">The rate that depth was found at, or NaN.</param>
public sealed record ToneWindowFeatures(
    double Seconds,
    double RmsDbfs,
    double PeakDbfs,
    double CrestDb,
    double CentroidHz,
    double Rolloff85Hz,
    double TiltDbPerOctave,
    double Flatness,
    double HighMidRatioDb,
    double DecaySeconds,
    double ModulationDepth,
    double ModulationHz)
{
    /// <summary>
    ///     The features as the lines a prompt carries — one per line, named, with
    ///     units, and with the reading spelled out where a number alone would not
    ///     transfer ("crest 8.4 dB (heavily compressed or distorted)").
    /// </summary>
    public string ToPromptLines()
    {
        var sb = new StringBuilder(512);
        var c = CultureInfo.InvariantCulture;
        sb.Append(c, $"length: {Seconds:0.0} s\n");
        sb.Append(c, $"level: {RmsDbfs:0.0} dBFS RMS, {PeakDbfs:0.0} dBFS peak\n");
        sb.Append(c, $"crest: {CrestDb:0.0} dB ({CrestReading()})\n");
        sb.Append(c, $"centroid: {CentroidHz:0} Hz, 85 % rolloff {Rolloff85Hz:0} Hz\n");
        sb.Append(c, $"spectral tilt: {TiltDbPerOctave:0.0} dB/octave ({TiltReading()})\n");
        sb.Append(c, $"presence (2-6 kHz vs 200-800 Hz): {HighMidRatioDb:0.0} dB\n");
        sb.Append(c, $"spectral flatness: {Flatness:0.00} ({FlatnessReading()})\n");
        sb.Append(double.IsNaN(DecaySeconds)
            ? "decay: not measurable (no clear onset in this window)\n"
            : string.Create(c, $"decay to -20 dB: {DecaySeconds:0.00} s ({DecayReading()})\n"));
        sb.Append(ModulationDepth < 0.08 || double.IsNaN(ModulationHz)
            ? "modulation: none detected\n"
            : string.Create(c, $"modulation: depth {ModulationDepth:0.00} at {ModulationHz:0.0} Hz\n"));
        return sb.ToString();
    }

    private string CrestReading() => CrestDb switch
    {
        < 8 => "heavily compressed or distorted",
        < 12 => "driven",
        < 17 => "moderate",
        _ => "clean and dynamic"
    };

    private string TiltReading() => TiltDbPerOctave switch
    {
        < -9 => "very dark",
        < -5 => "dark",
        < -2 => "balanced",
        _ => "bright"
    };

    private string FlatnessReading() => Flatness switch
    {
        < 0.05 => "strongly tonal",
        < 0.2 => "tonal",
        < 0.4 => "grainy",
        _ => "noise-like"
    };

    private string DecayReading() => DecaySeconds switch
    {
        < 0.3 => "tight or gated",
        < 0.8 => "natural",
        < 2.0 => "long — delay or reverb likely",
        _ => "very long — ambient"
    };
}

/// <summary>
///     Measures a <see cref="ToneWindowFeatures" /> from a window of isolated stem.
///     Pure, deterministic and engine-free: an STFT and some arithmetic, no JUCE
///     engine and no model.
///     <para>
///         <b>This is the primary input, and the model is a MAPPER over it.</b> The
///         alternative — handing raw audio to a multimodal model — is one API call
///         away from an answer nobody can reproduce, explain or test. Eleven measured
///         numbers can be asserted in a unit test, printed in a report and read by a
///         human who disagrees with the result; a spectrogram a model looked at
///         cannot. <see cref="IToneEvidenceSource" /> is the seam a raw-audio arm
///         would arrive behind.
///     </para>
/// </summary>
public sealed class ToneWindowFeatureExtractor
{
    private const int Fft = 4096;
    private const int Hop = 1024;

    /// <summary>Everything is analysed at the extractor's rate, so the two halves of
    /// the evidence (these features and any <c>tonefx-v2</c> vector taken beside
    /// them) describe the same resampled signal.</summary>
    public const int WorkingRate = ToneFeatureExtractor.WorkingRate;

    /// <summary>Frames quieter than this below the loudest are dropped from the
    /// spectral statistics — the same 45 dB gate the shared extractor uses, for the
    /// same reason: a gap between phrases is not a tone.</summary>
    public const double FrameGateDb = ToneFeatureExtractor.FrameGateDb;

    /// <summary>Measures the window.</summary>
    /// <param name="audio">The isolated stem for the window. Any rate; resampled.</param>
    /// <exception cref="ArgumentException">The window carries no audio.</exception>
    public ToneWindowFeatures Extract(MonoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var work = MonoResampler.To(audio, WorkingRate);
        if (work.Samples.Length < Fft)
        {
            throw new ArgumentException(
                $"A tone window needs at least {Fft / (double)WorkingRate:0.00} s of audio to measure; " +
                $"this one is {work.Duration.TotalSeconds:0.00} s.", nameof(audio));
        }

        var x = work.Samples;
        double sumSq = 0, peak = 0;
        foreach (var s in x)
        {
            var v = float.IsFinite(s) ? s : 0f;
            sumSq += v * (double)v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        var rms = Math.Sqrt(sumSq / x.Length);
        var rmsDb = Db(rms);
        var peakDb = Db(peak);

        var spectrogram = new JuceSpectrogramBuilder(Fft, Hop).Build(x, WorkingRate);
        var kept = GateFrames(spectrogram);

        var (centroid, rolloff, tilt, flatness, presence) = Spectral(spectrogram, kept);
        var envelope = Envelope(x);
        var decay = DecaySeconds(envelope, WorkingRate / (double)Hop);
        var (depth, rate) = Modulation(envelope, WorkingRate / (double)Hop);

        return new ToneWindowFeatures(
            work.Duration.TotalSeconds,
            rmsDb, peakDb, peakDb - rmsDb,
            centroid, rolloff, tilt, flatness, presence,
            decay, depth, rate);
    }

    private static double Db(double linear) => 20.0 * Math.Log10(Math.Max(linear, 1e-9));

    /// <summary>Frame indices within <see cref="FrameGateDb" /> of the loudest.</summary>
    private static int[] GateFrames(JuceSpectrogram s)
    {
        var energy = new double[s.FrameCount];
        var loudest = 0.0;
        for (var f = 0; f < s.FrameCount; f++)
        {
            double sum = 0;
            foreach (var m in s.Frames[f]) sum += m * (double)m;
            energy[f] = sum;
            loudest = Math.Max(loudest, sum);
        }

        var gate = loudest * Math.Pow(10.0, -FrameGateDb / 10.0);
        var kept = new List<int>(s.FrameCount);
        for (var f = 0; f < s.FrameCount; f++)
        {
            if (energy[f] >= gate && energy[f] > 0)
                kept.Add(f);
        }

        return kept.Count > 0 ? kept.ToArray() : [0];
    }

    private static (double Centroid, double Rolloff, double Tilt, double Flatness, double Presence)
        Spectral(JuceSpectrogram s, int[] kept)
    {
        // The averaged POWER spectrum over the kept frames. Averaging power and
        // taking the log once (rather than averaging logs) is what fills in the
        // harmonic combs — the same finding the match lane's extractor records.
        var power = new double[s.BinCount];
        foreach (var f in kept)
        {
            var frame = s.Frames[f];
            for (var b = 0; b < power.Length && b < frame.Length; b++)
                power[b] += frame[b] * (double)frame[b];
        }

        for (var b = 0; b < power.Length; b++) power[b] /= kept.Length;

        double total = 0, weighted = 0, logSum = 0, arithmetic = 0;
        var counted = 0;
        for (var b = 1; b < power.Length; b++)
        {
            var hz = s.BinHz(b);
            if (hz is < 40 or > 12_000) continue;
            var p = power[b];
            total += p;
            weighted += hz * p;
            logSum += Math.Log(Math.Max(p, 1e-20));
            arithmetic += p;
            counted++;
        }

        var centroid = total > 0 ? weighted / total : 0;

        var running = 0.0;
        var rolloff = 0.0;
        for (var b = 1; b < power.Length; b++)
        {
            var hz = s.BinHz(b);
            if (hz is < 40 or > 12_000) continue;
            running += power[b];
            if (running >= 0.85 * total) { rolloff = hz; break; }
        }

        var flatness = counted > 0 && arithmetic > 0
            ? Math.Exp(logSum / counted) / (arithmetic / counted)
            : 0;

        return (centroid, rolloff, Tilt(s, power), Math.Clamp(flatness, 0, 1), Presence(s, power));
    }

    /// <summary>
    ///     Least-squares slope of level (dB) against log2 frequency, over
    ///     60 Hz–8 kHz. One number for "dark or bright", and the one a cab choice and
    ///     a tone control both move.
    /// </summary>
    private static double Tilt(JuceSpectrogram s, double[] power)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var b = 1; b < power.Length; b++)
        {
            var hz = s.BinHz(b);
            if (hz is < 60 or > 8000) continue;
            var x = Math.Log2(hz);
            var y = 10.0 * Math.Log10(Math.Max(power[b], 1e-20));
            n++; sx += x; sy += y; sxx += x * x; sxy += x * y;
        }

        var denominator = n * sxx - sx * sx;
        return Math.Abs(denominator) < 1e-9 ? 0 : (n * sxy - sx * sy) / denominator;
    }

    /// <summary>2–6 kHz against 200–800 Hz, in dB. The presence/bite axis.</summary>
    private static double Presence(JuceSpectrogram s, double[] power)
    {
        double high = 0, mid = 0;
        for (var b = 1; b < power.Length; b++)
        {
            var hz = s.BinHz(b);
            if (hz is >= 2000 and <= 6000) high += power[b];
            else if (hz is >= 200 and <= 800) mid += power[b];
        }

        return 10.0 * Math.Log10(Math.Max(high, 1e-20) / Math.Max(mid, 1e-20));
    }

    /// <summary>The level envelope at one value per <see cref="Hop" /> samples.</summary>
    private static double[] Envelope(float[] x)
    {
        var frames = Math.Max(1, x.Length / Hop);
        var env = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            var start = f * Hop;
            var end = Math.Min(start + Hop, x.Length);
            double sum = 0;
            for (var i = start; i < end; i++)
            {
                var v = float.IsFinite(x[i]) ? x[i] : 0f;
                sum += v * (double)v;
            }

            env[f] = Math.Sqrt(sum / Math.Max(1, end - start));
        }

        return env;
    }

    /// <summary>
    ///     Seconds from the loudest frame until the envelope falls 20 dB below it.
    ///     NaN when it never does — a part that never stops is not a decay
    ///     measurement, and saying so beats reporting the window length.
    /// </summary>
    private static double DecaySeconds(double[] env, double framesPerSecond)
    {
        if (env.Length < 2 || framesPerSecond <= 0) return double.NaN;

        var peak = 0.0;
        var at = 0;
        for (var i = 0; i < env.Length; i++)
            if (env[i] > peak) { peak = env[i]; at = i; }

        if (peak <= 1e-9) return double.NaN;
        var target = peak * Math.Pow(10.0, -20.0 / 20.0);
        for (var i = at + 1; i < env.Length; i++)
        {
            if (env[i] <= target)
                return (i - at) / framesPerSecond;
        }

        return double.NaN;
    }

    /// <summary>
    ///     The strongest periodic component of the envelope between 0.5 and 12 Hz —
    ///     the band a tremolo, a chorus or a phaser lives in — as a depth in
    ///     <c>[0,1]</c> and the rate it was found at.
    ///     <para>
    ///         A plain Goertzel sweep rather than an FFT: the band is a handful of
    ///         rates and the envelope is a few hundred points, so the loop is cheaper
    ///         than the transform and there is no bin-alignment argument to have.
    ///     </para>
    /// </summary>
    private static (double Depth, double Hz) Modulation(double[] env, double framesPerSecond)
    {
        if (env.Length < 16 || framesPerSecond <= 0) return (0, double.NaN);

        var mean = env.Average();
        if (mean <= 1e-9) return (0, double.NaN);

        var bestDepth = 0.0;
        var bestHz = double.NaN;
        for (var hz = 0.5; hz <= 12.0; hz += 0.1)
        {
            var omega = 2 * Math.PI * hz / framesPerSecond;
            if (omega >= Math.PI) break; // past the envelope's own Nyquist
            double re = 0, im = 0;
            for (var i = 0; i < env.Length; i++)
            {
                var d = env[i] - mean;
                re += d * Math.Cos(omega * i);
                im += d * Math.Sin(omega * i);
            }

            // Amplitude of that component, as a fraction of the mean level.
            var amplitude = 2.0 * Math.Sqrt(re * re + im * im) / env.Length;
            var depth = amplitude / mean;
            if (depth > bestDepth) { bestDepth = depth; bestHz = hz; }
        }

        return (Math.Clamp(bestDepth, 0, 1), bestHz);
    }
}
