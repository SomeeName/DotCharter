using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The search lane's stand-ins for the three services the eval lane owns. They
///     exist so hidden-rig recovery is testable with no engine, no DLL and no audio
///     device — and they are deliberately CHEAP and PARAMETRIC rather than
///     realistic: the point of a fake renderer here is that a rig's knobs
///     demonstrably change the audio in a way the extractor can see, so a search
///     that cannot recover a hidden rig through THIS chain is broken for certain.
///     The real renderer's own recovery test is
///     <c>RealRendererMatchEvalTests</c>, gated behind <c>DOTCHARTER_AUDIO_EVAL</c>.
/// </summary>
internal static class SearchFakes
{
    internal const int Rate = 16000;

    /// <summary>A DI-like test signal: plucked-ish decaying tones at changing
    /// pitches plus a little noise, so two windows of it are NOT interchangeable
    /// (which is what makes the self-distance floor non-zero and meaningful).</summary>
    internal static MonoAudio Di(double seconds = 2.0, int seed = 7)
    {
        var rng = new Random(seed);
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var noteLen = Rate / 4;
        for (var start = 0; start < n; start += noteLen)
        {
            var f = 82.4 * Math.Pow(2.0, rng.Next(0, 24) / 12.0);
            for (var i = 0; i < noteLen && start + i < n; i++)
            {
                var t = i / (double)Rate;
                var env = Math.Exp(-4.0 * t);
                var s = Math.Sin(2 * Math.PI * f * t)
                        + 0.4 * Math.Sin(4 * Math.PI * f * t)
                        + 0.2 * Math.Sin(6 * Math.PI * f * t);
                x[start + i] = (float)(0.3 * env * s + 0.002 * (rng.NextDouble() - 0.5));
            }
        }

        return new MonoAudio(x, Rate);
    }
}

/// <summary>A deterministic, allocation-cheap stand-in for the offline engine:
/// gain → soft clip with a tone control → low-pass → feedback delay, every stage
/// driven by the rig's own knob values (catalogue defaults when the preset omits
/// one). Module types it does not model pass audio through, which is exactly how a
/// real chain behaves for a bypassed node.</summary>
internal sealed class FakeRigRenderer : IRigRenderer
{
    public int Renders { get; private set; }

    /// <summary>Called at the top of every render, for a test that wants to
    /// observe the world MID-search (the library growth hold, say).</summary>
    public Action? OnRender { get; set; }

    public Task<MonoAudio> RenderAsync(MonoAudio di, DcRigPreset rig, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Renders++;
        OnRender?.Invoke();
        var y = (float[])di.Samples.Clone();
        foreach (var m in rig.Modules)
        {
            if (m.Bypassed) continue;
            switch (m.ModuleTypeId)
            {
                case "gain":
                    Scale(y, (float)Math.Pow(10.0, P(m, "gainDb") / 20.0));
                    break;
                case "overdrive":
                    SoftClip(y, P(m, "drive"), P(m, "level"));
                    LowPass(y, di.SampleRate, 500f * MathF.Pow(16f, P(m, "tone")));
                    break;
                case "svf":
                    LowPass(y, di.SampleRate, P(m, "cutoffHz"));
                    break;
                case "delay":
                    Delay(y, di.SampleRate, P(m, "timeMs"), P(m, "feedback"), P(m, "mix"), P(m, "toneHz"));
                    break;
                case "eq":
                    Biquad.LowShelf(P(m, "lowFreq"), P(m, "lowGainDb"), di.SampleRate).Run(y);
                    Biquad.Peak(P(m, "midFreq"), P(m, "midQ"), P(m, "midGainDb"), di.SampleRate).Run(y);
                    Biquad.HighShelf(P(m, "highFreq"), P(m, "highGainDb"), di.SampleRate).Run(y);
                    Scale(y, (float)Math.Pow(10.0, P(m, "outputTrimDb") / 20.0));
                    break;
            }
        }

        return Task.FromResult(new MonoAudio(y, di.SampleRate));
    }

    private static float P(DcRigModule m, string id)
    {
        if (m.Params.TryGetValue(id, out var v)) return v;
        return DcRigTargetCatalogProbe.Default(m.ModuleTypeId, id);
    }

    private static void Scale(float[] y, float g)
    {
        for (var i = 0; i < y.Length; i++) y[i] *= g;
    }

    private static void SoftClip(float[] y, float drive, float level)
    {
        // 8×, not 24×: past a few times unity a tanh is already fully
        // saturated, so a drive knob scaled that hard is near-DEGENERATE over its
        // top half — two settings 0.12 apart render almost the same audio, which
        // is a property of the pedal and not something a search can fix.
        var pre = 1f + drive * 8f;
        var norm = MathF.Tanh(pre);
        for (var i = 0; i < y.Length; i++) y[i] = MathF.Tanh(pre * y[i]) / norm * (0.2f + level);
    }

    private static void LowPass(float[] y, int rate, float cutoffHz)
    {
        var a = MathF.Exp(-2f * MathF.PI * Math.Clamp(cutoffHz, 20f, rate / 2.2f) / rate);
        var z = 0f;
        for (var i = 0; i < y.Length; i++)
        {
            z = (1f - a) * y[i] + a * z;
            y[i] = z;
        }
    }

    private static void Delay(float[] y, int rate, float timeMs, float feedback, float mix, float toneHz)
    {
        var d = Math.Max(1, (int)(timeMs * 0.001f * rate));
        var buf = new float[d];
        var idx = 0;
        var a = MathF.Exp(-2f * MathF.PI * Math.Clamp(toneHz, 20f, rate / 2.2f) / rate);
        var z = 0f;
        for (var i = 0; i < y.Length; i++)
        {
            var echo = buf[idx];
            z = (1f - a) * echo + a * z;
            buf[idx] = y[i] + z * Math.Clamp(feedback, 0f, 0.95f);
            idx = idx + 1 >= d ? 0 : idx + 1;
            // The tone control shapes the AUDIBLE repeats, not only what is fed
            // back: a tone knob that only ever touched the second repeat is
            // near-inaudible, and an inaudible knob is one no search can recover.
            y[i] = y[i] * (1f - mix) + z * mix;
        }
    }
}

/// <summary>RBJ cookbook biquads for the fake EQ. A three-band EQ is the ideal
/// shape for a recovery test: every knob moves the long-term average spectrum
/// SMOOTHLY and in its own region, so a failure is the search's fault and not the
/// landscape's.</summary>
internal readonly struct Biquad(double b0, double b1, double b2, double a1, double a2)
{
    internal void Run(float[] y)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < y.Length; i++)
        {
            double x = y[i];
            var o = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = o;
            y[i] = (float)o;
        }
    }

    internal static Biquad Peak(double f, double q, double gainDb, int rate)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w = 2.0 * Math.PI * Math.Clamp(f, 20.0, rate / 2.2) / rate;
        var alpha = Math.Sin(w) / (2.0 * Math.Max(0.1, q));
        var a0 = 1 + alpha / a;
        return new Biquad(
            (1 + alpha * a) / a0, -2 * Math.Cos(w) / a0, (1 - alpha * a) / a0,
            -2 * Math.Cos(w) / a0, (1 - alpha / a) / a0);
    }

    internal static Biquad LowShelf(double f, double gainDb, int rate)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w = 2.0 * Math.PI * Math.Clamp(f, 20.0, rate / 2.2) / rate;
        var alpha = Math.Sin(w) / 2.0 * Math.Sqrt((a + 1 / a) * (1 / 0.9 - 1) + 2);
        var cs = Math.Cos(w);
        var sa = 2 * Math.Sqrt(a) * alpha;
        var a0 = (a + 1) + (a - 1) * cs + sa;
        return new Biquad(
            a * ((a + 1) - (a - 1) * cs + sa) / a0,
            2 * a * ((a - 1) - (a + 1) * cs) / a0,
            a * ((a + 1) - (a - 1) * cs - sa) / a0,
            -2 * ((a - 1) + (a + 1) * cs) / a0,
            ((a + 1) + (a - 1) * cs - sa) / a0);
    }

    internal static Biquad HighShelf(double f, double gainDb, int rate)
    {
        var a = Math.Pow(10.0, gainDb / 40.0);
        var w = 2.0 * Math.PI * Math.Clamp(f, 20.0, rate / 2.2) / rate;
        var alpha = Math.Sin(w) / 2.0 * Math.Sqrt((a + 1 / a) * (1 / 0.9 - 1) + 2);
        var cs = Math.Cos(w);
        var sa = 2 * Math.Sqrt(a) * alpha;
        var a0 = (a + 1) - (a - 1) * cs + sa;
        return new Biquad(
            a * ((a + 1) + (a - 1) * cs + sa) / a0,
            -2 * a * ((a - 1) + (a + 1) * cs) / a0,
            a * ((a + 1) + (a - 1) * cs - sa) / a0,
            2 * ((a - 1) - (a + 1) * cs) / a0,
            ((a + 1) - (a - 1) * cs - sa) / a0);
    }
}

/// <summary>Reaches the catalogue defaults from the test assembly (Services grants
/// it InternalsVisibleTo), so a fake never re-types a range.</summary>
internal static class DcRigTargetCatalogProbe
{
    internal static float Default(string moduleTypeId, string paramId)
        => DcRigTargetCatalog.TryGetParam(moduleTypeId, paramId, out var spec) ? spec.Default : 0f;
}

/// <summary>
///     A content-invariant descriptor: the time-averaged log spectrum folded into
///     24 bands, RMS and crest, and a MODULATION branch — the normalised
///     autocorrelation of the RMS envelope at 16 lags from 10 ms to 1 s.
///     <para>
///         The modulation branch is not decoration. Without it the descriptor is
///         exactly a long-term average spectrum, and a long-term average spectrum
///         CANNOT SEE a delay's feedback: repeats of a decaying echo have the same
///         spectrum as the dry signal, so the search recovered every knob except
///         <c>delay.feedback</c> and reported a distance far below the target's own
///         floor while doing it — a confident match to the wrong tone. That is the
///         failure the feasibility study cites (arXiv 2506.22628: envelope-DTW for
///         modulation, ρ = 0.86 with human agreement) and the reason the real
///         extractor must carry an envelope/modulation term too.
///     </para>
/// </summary>
internal sealed class FakeFeatureExtractor : IToneFeatureExtractor
{
    private const int Lags = 32;

    public string Id => "fake-logband24mod32-v1";

    public ToneFeatures Extract(MonoAudio audio)
    {
        var spec = MultiScaleSpectralDistance.AverageSpectrum(audio.Samples, 1024);
        const int bands = 24;
        var v = new float[bands + 2 + Lags];
        if (spec is not null)
        {
            // LOG-spaced bands, 40 Hz → Nyquist. A linear fold was the first thing
            // tried and it is useless here: at 16 kHz with 24 linear bands every
            // band is 333 Hz wide, so the entire low shelf AND the guitar's
            // fundamentals land in band 0 and a +8 dB bass boost is nearly
            // invisible — the search then drove lowGainDb to its MINIMUM while the
            // distance fell. Hearing is logarithmic in frequency and so is every
            // real front-end (mel, 1/3-octave); this mirrors that.
            var nyquist = audio.SampleRate / 2.0;
            var binHz = nyquist / (spec.Length - 1);
            for (var b = 0; b < bands; b++)
            {
                var lo = 40.0 * Math.Pow(nyquist / 40.0, b / (double)bands);
                var hi = 40.0 * Math.Pow(nyquist / 40.0, (b + 1) / (double)bands);
                var k0 = Math.Clamp((int)(lo / binHz), 0, spec.Length - 1);
                var k1 = Math.Clamp((int)(hi / binHz), k0 + 1, spec.Length);
                var acc = 0.0;
                for (var k = k0; k < k1; k++) acc += spec[k];
                v[b] = (float)Math.Log(acc / (k1 - k0) + 1e-8);
            }
        }

        double sum = 0, peak = 0;
        foreach (var s in audio.Samples)
        {
            sum += s * (double)s;
            peak = Math.Max(peak, Math.Abs(s));
        }

        var rms = Math.Sqrt(sum / Math.Max(1, audio.Samples.Length));
        v[bands] = (float)Math.Log(rms + 1e-8);
        v[bands + 1] = (float)(peak / (rms + 1e-8));

        var env = Envelope(audio, out var hopSeconds);
        for (var i = 0; i < Lags; i++)
        {
            var seconds = 0.01 * Math.Pow(100.0, i / (double)(Lags - 1)); // 10 ms … 1 s
            var lag = (int)Math.Round(seconds / hopSeconds);
            // Weighted so the modulation branch carries about as much of the
            // distance as the spectrum does. Unweighted it is 32 numbers in
            // [-1,1] against 24 log-magnitudes that swing over decades, and a
            // delay's feedback then moves the distance by less than the
            // optimiser's own noise — measured: recovered to 0.31 instead of
            // 0.47 while the distance kept falling.
            v[bands + 2 + i] = (float)(6.0 * AutoCorrelation(env, lag));
        }

        return new ToneFeatures(v, Id);
    }

    /// <summary>The mean-removed RMS envelope at a 5 ms hop.</summary>
    private static double[] Envelope(MonoAudio audio, out double hopSeconds)
    {
        var hop = Math.Max(1, audio.SampleRate / 200);
        hopSeconds = hop / (double)audio.SampleRate;
        var n = audio.Samples.Length / hop;
        var e = new double[Math.Max(1, n)];
        var mean = 0.0;
        for (var i = 0; i < n; i++)
        {
            var acc = 0.0;
            for (var j = i * hop; j < (i + 1) * hop; j++) acc += audio.Samples[j] * (double)audio.Samples[j];
            e[i] = Math.Sqrt(acc / hop);
            mean += e[i];
        }

        mean /= Math.Max(1, n);
        for (var i = 0; i < e.Length; i++) e[i] -= mean;
        return e;
    }

    private static double AutoCorrelation(double[] e, int lag)
    {
        if (lag <= 0 || lag >= e.Length) return 0.0;
        double num = 0, den = 0;
        for (var i = 0; i + lag < e.Length; i++) num += e[i] * e[i + lag];
        foreach (var x in e) den += x * x;
        return den <= 1e-18 ? 0.0 : num / den;
    }
}

/// <summary>Mean absolute difference over the feature vector.</summary>
internal sealed class FakeSimilarity(IToneFeatureExtractor extractor) : IToneSimilarity
{
    public IToneFeatureExtractor Extractor { get; } = extractor;

    public double Distance(ToneFeatures a, ToneFeatures b)
    {
        var n = Math.Min(a.Vector.Length, b.Vector.Length);
        if (n == 0) return 0.0;
        var acc = 0.0;
        for (var i = 0; i < n; i++) acc += Math.Abs(a.Vector[i] - b.Vector[i]);
        return acc / n;
    }
}

/// <summary>Draws rigs over a fixed chain shape with uniformly random continuous
/// knobs — enough structure for retrieval to have something to find.</summary>
internal sealed class FakeRigSampler(params string[] chain) : IRigSampler
{
    private readonly string[] _chain = chain.Length > 0 ? chain : ["gain", "overdrive"];

    public DcRigPreset Sample(Random rng, RigSamplingProfile profile)
    {
        var modules = new List<DcRigModule>(_chain.Length);
        foreach (var type in _chain)
            modules.Add(new DcRigModule(type, new Dictionary<string, float>(StringComparer.Ordinal)));
        var layout = KnobLayout.For(new DcRigPreset(modules));
        var v = new float[layout.Count];
        for (var i = 0; i < v.Length; i++) v[i] = (float)rng.NextDouble();
        return layout.Decode(v);
    }
}
