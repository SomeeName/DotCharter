// RigSampler part 2 of 3 — the KNOB priors: how a value is drawn once the module is
// chosen. Four layers, most specific first: discrete table → archetype-conditioned
// drive window → explicit per-(module, param) prior → per-param-id heuristic →
// Beta(2,2) over the middle 80 % of the catalogue range.
//
// Every value ends up inside DcRigTargetCatalog's declared range by construction, and
// never EXACTLY on the catalogue default (see Nudge) — a model trained on a corpus
// where the untouched knobs all sit on their defaults learns the default, not the
// sound.

using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match;

public sealed partial class RigSampler
{
    /// <summary>A Beta(<paramref name="A" />, <paramref name="B" />) over
    /// [<paramref name="Lo" />, <paramref name="Hi" />] in the param's OWN units.
    /// Symmetric shapes concentrate in the middle; (2,3) leans low, (3,2) leans
    /// high, (1,1) is uniform.</summary>
    private readonly record struct Prior(float Lo, float Hi, float A = 2f, float B = 2f);

    /// <summary>How close to the catalogue default a sampled value may land before it
    /// is pushed away, as a fraction of the param's range. 2 % — far enough that a
    /// model cannot learn "this knob is always at its default", close enough that the
    /// tone is the one the prior asked for.</summary>
    private const float DefaultAvoidance = 0.02f;

    /// <summary>How often a drawn <c>amp</c> has its bright switch on (see SampleRaw).</summary>
    private const double BrightOnShare = 0.15;

    // ------------------------------------------------------- discrete params

    /// <summary>Param ids that are DISCRETE wherever they appear. The engine declares
    /// this properly (<c>JuceDspParamInfo.Labels</c> for NAMED, <c>Step</c> &gt; 0 for
    /// ORDINAL) but only through an open engine, and this sampler must run in a unit
    /// test with no DLL — so it is a table, and
    /// <c>RigSamplerNativeCatalogueTests</c> re-checks it against the live metadata on
    /// a machine that has one.</summary>
    private static readonly HashSet<string> GlobalDiscrete = new(StringComparer.Ordinal)
    {
        "model", "voicing", "waveform", "lfoShape", "voices", "bits", "downsample",
        "taps", "steps", "bands", "pattern", "key", "chord", "octave", "scale",
        "carrier", "curve", "tubes", "mic", "clip", "law", "polarity", "source",
        "filterType", "space", "division", "repeats", "harmonic", "sweep", "mode",
        "direction", "shape",
        // switches: 0/1 integers, which is the same rounding rule
        "sync", "trails", "pingPong", "invertA", "invertB", "lowLatency", "formant",
        "reverse", "halfSpeed", "undo", "swap", "midPhase", "sidePhase", "phase",
        "piezoFix", "cabSim", "bright", "freeze", "trigger", "capture", "limit",
        "mute1", "mute2", "mute3", "solo1", "solo2", "solo3",
        // bassamp's Ultra Lo / Ultra Hi: Off / On
        "lowShift", "highShift"
    };

    /// <summary>Per-(module, param) discreteness that the global set would get wrong,
    /// and the one step that is not 1: a phaser's allpass count is forced EVEN by the
    /// core, so <c>stages</c> steps by 2 and a continuous draw parks the UI at
    /// "7 stages" while the core runs 6.</summary>
    private static readonly Dictionary<(string, string), float> DiscreteStepOverride = new()
    {
        [("phaser", "stages")] = 2f,
        [("barberpole", "stages")] = 2f,
        [("multitap", "spread")] = 1f,     // NAMED: Even / Golden / Dotted / Triplet
        [("noisereducer", "humFreq")] = 10f // 50 Hz or 60 Hz, nothing between
    };

    /// <summary>Param ids the global set WOULD claim and that are really continuous.
    /// <c>detector</c> is a peak↔RMS blend, <c>gateShape</c>/<c>swellShape</c> are
    /// shapes in name only, and <c>spread</c> is a width on every module except
    /// <c>multitap</c>.</summary>
    private static readonly HashSet<(string, string)> ForceContinuous =
    [
        ("compressor", "detector"), ("chorus", "spread"), ("doubler", "spread"),
        ("granular", "spray"), ("resonator", "spread"), ("micpos", "position")
    ];

    private static bool TryDiscreteStep(string typeId, string paramId, out float step)
    {
        if (DiscreteStepOverride.TryGetValue((typeId, paramId), out step)) return true;
        if (ForceContinuous.Contains((typeId, paramId))) { step = 0f; return false; }
        if (GlobalDiscrete.Contains(paramId)) { step = 1f; return true; }
        step = 0f;
        return false;
    }

    // ------------------------------------------------- archetype drive window

    /// <summary>The (module, param) pairs that ARE "how hard is this thing driven".
    /// They are the only knobs whose prior depends on the archetype, and that is the
    /// single most important conditioning in the sampler: the same
    /// <c>overdrive.drive</c> value is a wrong label for a clean rig and a wrong
    /// label for a metal rig, and drawing it from one distribution would make the
    /// two indistinguishable at the knob head.</summary>
    private static readonly HashSet<(string, string)> DriveAmountParams =
    [
        ("amp", "gain"), ("namamp", "gain"), ("bassamp", "gain"), ("bassamp", "drive"),
        ("poweramp", "drive"), ("tubepre", "gain"), ("overdrive", "drive"),
        ("distortion", "gain"), ("fuzz", "fuzz"), ("rat", "distortion"),
        ("ds1", "dist"), ("hm2", "dist"), ("bigmuff", "sustain"), ("klon", "gain"),
        ("octfuzz", "fuzz"), ("tapesat", "drive"), ("bassod", "drive")
    ];

    private static Prior DriveWindow(string kind) => kind switch
    {
        RigProfileKinds.Clean => new Prior(0.05f, 0.36f, 2.2f, 2.6f),
        RigProfileKinds.Crunch => new Prior(0.30f, 0.64f, 2.4f, 2.4f),
        RigProfileKinds.HighGain => new Prior(0.55f, 0.96f, 2.6f, 2.0f),
        RigProfileKinds.Bass => new Prior(0.15f, 0.58f, 2.2f, 2.4f),
        RigProfileKinds.Ambient => new Prior(0.06f, 0.46f, 2.0f, 2.6f),
        RigProfileKinds.Direct => new Prior(0.10f, 0.48f, 2.0f, 2.4f),
        _ => new Prior(0.10f, 0.85f, 2.0f, 2.0f)
    };

    // ------------------------------------------------------- explicit priors

    /// <summary>Modules whose <c>mix</c> is full-wet BY NATURE — a cabinet, a wah, a
    /// filter, a pitch corrector. "Mix knobs rarely at 100 %" is true of the
    /// time-and-modulation drawer and false of these, and applying one rule to both
    /// would ship a corpus of half-bypassed cabinets.</summary>
    private static readonly HashSet<string> FullWetByNature = new(StringComparer.Ordinal)
    {
        "cab", "micpos", "acousticsim", "wah", "autowah", "envfilter", "svf",
        "formant", "talkbox", "vocoder", "ringmod", "bitcrusher", "rotary",
        "pitchcorrect", "tapestop", "filterseq", "whammy", "transient", "tapesat",
        "comb"
    };

    private static readonly Dictionary<(string, string), Prior> ExplicitPriors = new()
    {
        // --- modulation rates. Slow is musical; a 10 Hz chorus is a special effect.
        [("chorus", "rateHz")] = new Prior(0.15f, 1.8f, 2f, 3f),
        [("chorus", "depthMs")] = new Prior(2f, 9f),
        [("ensemble", "rateHz")] = new Prior(0.2f, 1.2f, 2f, 3f),
        [("flanger", "rateHz")] = new Prior(0.08f, 1.0f, 2f, 3f),
        [("tzflanger", "rateHz")] = new Prior(0.05f, 1.2f, 2f, 3f),
        [("phaser", "rateHz")] = new Prior(0.1f, 1.8f, 2f, 3f),
        [("barberpole", "rateHz")] = new Prior(0.05f, 0.8f, 2f, 3f),
        [("univibe", "rateHz")] = new Prior(0.3f, 3.5f, 2f, 2.5f),
        [("vibrato", "rateHz")] = new Prior(3f, 7f),
        [("tremolo", "rateHz")] = new Prior(1.5f, 8f, 2.2f, 2.2f),
        [("harmtremolo", "rateHz")] = new Prior(1.5f, 7f),
        [("autopan", "rateHz")] = new Prior(0.2f, 3f, 2f, 3f),
        [("svf", "lfoRateHz")] = new Prior(0.1f, 4f, 2f, 3f),
        [("filterseq", "rateHz")] = new Prior(1f, 8f),
        // --- feedbacks. Stay clear of the self-oscillation ceiling.
        [("delay", "feedback")] = new Prior(0.08f, 0.6f, 2f, 2.6f),
        [("tapedelay", "feedback")] = new Prior(0.08f, 0.6f, 2f, 2.6f),
        [("bbddelay", "feedback")] = new Prior(0.08f, 0.62f, 2f, 2.6f),
        [("duckdelay", "feedback")] = new Prior(0.08f, 0.6f, 2f, 2.6f),
        [("multitap", "feedback")] = new Prior(0.0f, 0.45f, 2f, 3f),
        [("rhythmdelay", "feedback")] = new Prior(0.1f, 0.6f),
        [("reversedelay", "feedback")] = new Prior(0.05f, 0.5f),
        [("granular", "feedback")] = new Prior(0.0f, 0.45f, 2f, 3f),
        // --- reverb sizes and tails.
        [("reverb", "roomSize")] = new Prior(0.2f, 0.85f, 2.2f, 2.2f),
        [("platereverb", "decay")] = new Prior(0.2f, 0.8f),
        [("springreverb", "decay")] = new Prior(0.25f, 0.8f),
        [("shimmer", "decay")] = new Prior(0.45f, 0.9f),
        [("hallreverb", "decayS")] = new Prior(1.2f, 6f, 2f, 3f),
        [("gatedreverb", "decayS")] = new Prior(1.5f, 6f),
        [("reversereverb", "decayS")] = new Prior(1f, 3.5f),
        [("hallreverb", "predelayMs")] = new Prior(5f, 60f, 2f, 3f),
        [("platereverb", "predelayMs")] = new Prior(0f, 50f, 2f, 3f),
        // --- dynamics. A compressor a player would actually switch on.
        [("compressor", "thresholdDb")] = new Prior(-32f, -8f),
        [("compressor", "ratio")] = new Prior(1.8f, 8f, 2f, 3f),
        [("compressor", "attackMs")] = new Prior(2f, 30f, 2f, 3f),
        [("compressor", "releaseMs")] = new Prior(60f, 400f, 2f, 3f),
        [("compressor", "makeupDb")] = new Prior(0f, 9f, 2f, 2.5f),
        [("optocomp", "peakReduction")] = new Prior(0.2f, 0.7f),
        [("optocomp", "gainDb")] = new Prior(0f, 10f, 2f, 2.5f),
        [("gate", "thresholdDb")] = new Prior(-72f, -38f),
        [("gate", "ratio")] = new Prior(4f, 30f, 2f, 2.5f),
        [("noisereducer", "thresholdDb")] = new Prior(-72f, -40f),
        [("limiter", "ceilingDb")] = new Prior(-6f, -0.3f, 3f, 2f),
        [("expander", "thresholdDb")] = new Prior(-62f, -28f),
        [("ducker", "duckDb")] = new Prior(4f, 20f),
        // --- amp and cab shaping.
        [("amp", "master")] = new Prior(0.42f, 0.78f, 3f, 3f),
        [("namamp", "master")] = new Prior(0.4f, 0.75f, 3f, 3f),
        [("bassamp", "master")] = new Prior(0.45f, 0.8f, 3f, 3f),
        [("amp", "presence")] = new Prior(0.15f, 0.65f),
        [("cab", "lowCutHz")] = new Prior(55f, 140f, 2f, 3f),
        [("cab", "highCutHz")] = new Prior(4500f, 11000f, 2.5f, 2f),
        [("cab", "mix")] = new Prior(0.85f, 1f, 3f, 1.6f),
        [("micpos", "distanceCm")] = new Prior(1f, 12f, 2f, 3f),
        [("micpos", "room")] = new Prior(0f, 0.35f, 1.6f, 3f),
        // --- the amp / bass-head controls. The bassamp bands
        // widened from +/-12 to +/-20 dB for the SVT-CL's -20 dB cuts; these keep
        // the corpus's band draws where they were in dB (the range-relative
        // 20..82 % heuristic over +/-12 dB), and the new sweepable mid draws the
        // same. Its centre spans the documented selectors (SVT-CL 220 Hz-3 kHz),
        // the bell offsets stay within an octave, and the amp's resonance leans
        // low, so a corpus rig is not a bass-boosted one by default.
        [("bassamp", "bass")] = new Prior(-7.2f, 7.68f, 3f, 3f),
        [("bassamp", "lowMid")] = new Prior(-7.2f, 7.68f, 3f, 3f),
        [("bassamp", "hiMid")] = new Prior(-7.2f, 7.68f, 3f, 3f),
        [("bassamp", "treble")] = new Prior(-7.2f, 7.68f, 3f, 3f),
        [("bassamp", "mid")] = new Prior(-7.2f, 7.68f, 3f, 3f),
        [("bassamp", "midHz")] = new Prior(220f, 3000f, 2f, 2.5f),
        [("bassamp", "lowMidOct")] = new Prior(-1f, 1f, 3f, 3f),
        [("bassamp", "hiMidOct")] = new Prior(-1f, 1f, 3f, 3f),
        [("amp", "resonance")] = new Prior(0.02f, 0.6f, 1.6f, 3f),
        // --- eq: small moves, mostly.
        [("eq", "lowGainDb")] = new Prior(-7f, 7f, 3f, 3f),
        [("eq", "midGainDb")] = new Prior(-8f, 7f, 3f, 3f),
        [("eq", "highGainDb")] = new Prior(-6f, 8f, 3f, 3f),
        [("eq", "midQ")] = new Prior(0.5f, 3f, 2f, 3f),
        [("tilt", "tilt")] = new Prior(-6f, 6f, 3f, 3f),
        // --- pitch. Musical intervals come from the discrete path; these are widths.
        [("detune", "cents")] = new Prior(5f, 22f, 2f, 2.5f),
        [("doubler", "detuneCents")] = new Prior(5f, 22f),
        [("twelvestring", "detuneCents")] = new Prior(4f, 16f),
        [("granular", "grainMs")] = new Prior(30f, 200f, 2f, 3f)
    };

    // ---------------------------------------------------------- tempo-ish time

    /// <summary>Musical subdivisions of a beat. A delay time a player dials is a
    /// subdivision of the song, not a number out of a hat — so 75 % of delay times
    /// are drawn as (60000 / bpm) × one of these, and the remaining 25 % are free so
    /// the model does not learn that every echo lands on a grid.</summary>
    private static readonly float[] Subdivisions = [0.25f, 0.375f, 0.5f, 0.6667f, 0.75f, 1f, 1.5f, 2f];

    private static readonly HashSet<string> TempoishTimeParams = new(StringComparer.Ordinal)
    { "timeMs", "stepMs", "delayMs" };

    private static bool IsTempoish(string typeId, string paramId)
        => TempoishTimeParams.Contains(paramId)
           && FamilyOf.TryGetValue(typeId, out var fam)
           && fam == "delay";

    // --------------------------------------------------------------- sampling

    /// <summary>Params that exist for the Rocksmith conversion: the gate's
    /// noise-floor mode, which a clean rendered DI never exercises, and the bit
    /// crusher's FutzBox stages, which only the game's Bit Cruncher sets. The corpus
    /// keeps them at their defaults WITHOUT drawing, so its distribution — and the
    /// random stream every other knob is drawn from — is what it was before them.</summary>
    internal static readonly HashSet<(string, string)> HeldAtDefault =
    [
        ("gate", "autoFloor"), ("bitcrusher", "inputDb"), ("bitcrusher", "drive"),
        ("bitcrusher", "intensity"), ("bitcrusher", "rectify"), ("bitcrusher", "filter"),
        ("bitcrusher", "outputDb")
    ];

    private float SampleParam(Random rng, string typeId, string paramId,
        in DcRigTargetCatalog.ParamSpec spec, string kind)
    {
        if (HeldAtDefault.Contains((typeId, paramId))) return spec.Default;
        var value = SampleRaw(rng, typeId, paramId, spec, kind);
        value = Math.Clamp(value, spec.Min, spec.Max);

        if (TryDiscreteStep(typeId, paramId, out var step) && step > 0f)
        {
            var snapped = spec.Min + MathF.Round((value - spec.Min) / step) * step;
            return Math.Clamp(snapped, spec.Min, spec.Max);
        }

        return Nudge(value, spec);
    }

    private float SampleRaw(Random rng, string typeId, string paramId,
        in DcRigTargetCatalog.ParamSpec spec, string kind)
    {
        // 0. the amp's bright switch is a SOMETIMES thing, not a
        //    coin flip: on at a low volume setting it lifts the top end by up to
        //    ~20 dB (the AB763 bright cap the core models), and drawn 50/50 it
        //    put that spike on half the corpus's amps.
        if (typeId == "amp" && paramId == "bright")
            return rng.NextDouble() < BrightOnShare ? 1f : 0f;

        // 1. discrete — uniform over the legal values. A cab model or an amp voicing
        //    is a CHOICE of gear, and the corpus wants every cabinet equally.
        if (TryDiscreteStep(typeId, paramId, out var step) && step > 0f)
        {
            var count = (int)MathF.Floor((spec.Max - spec.Min) / step) + 1;
            return spec.Min + rng.Next(Math.Max(1, count)) * step;
        }

        // 2. how hard is it driven — the one archetype-conditioned family.
        if (DriveAmountParams.Contains((typeId, paramId)))
        {
            var w = DriveWindow(kind);
            return Rescale(SampleBeta(rng, w.A, w.B), w.Lo, w.Hi, spec);
        }

        // 3. an explicit prior for this exact knob.
        if (ExplicitPriors.TryGetValue((typeId, paramId), out var p))
            return Lerp(p.Lo, p.Hi, SampleBeta(rng, p.A, p.B));

        // 4. tempo-ish delay times.
        if (IsTempoish(typeId, paramId))
        {
            if (rng.NextDouble() < 0.75)
            {
                var bpm = 70f + (float)rng.NextDouble() * 100f;
                var beatMs = 60_000f / bpm;
                return beatMs * Subdivisions[rng.Next(Subdivisions.Length)];
            }

            return Lerp(spec.Min, spec.Max, SampleBeta(rng, 2f, 3f));
        }

        // 5. per-param-id heuristics.
        var range = spec.Max - spec.Min;
        switch (paramId)
        {
            case "mix":
                return FullWetByNature.Contains(typeId)
                    ? Lerp(spec.Min + 0.72f * range, spec.Max, SampleBeta(rng, 2.4f, 1.5f))
                    : Lerp(spec.Min + 0.08f * range, spec.Min + 0.58f * range, SampleBeta(rng, 2f, 2.6f));
            case "level" or "volume" or "output" or "master" or "loopLevel":
                return Lerp(spec.Min + 0.38f * range, spec.Min + 0.85f * range, SampleBeta(rng, 3f, 3f));
            case "tone" or "brightness" or "damping" or "emphasis" or "character"
                or "bass" or "mid" or "treble" or "lowMid" or "hiMid" or "presence"
                or "filter" or "blend" or "bias" or "body":
                return Lerp(spec.Min + 0.2f * range, spec.Min + 0.82f * range, SampleBeta(rng, 3f, 3f));
            case "depth" or "amount" or "sensitivity" or "resonance" or "sustain"
                or "width" or "spread" or "diffusion" or "drip" or "shimmer":
                return Lerp(spec.Min + 0.15f * range, spec.Min + 0.9f * range, SampleBeta(rng, 2.2f, 2.2f));
            case "feedback":
                // Signed feedbacks (flanger, phaser, comb, tzflanger, barberpole): a
                // draw centred on the range's middle would be a draw centred on ZERO,
                // i.e. the module switched off. Pick a SIGN, then a magnitude.
                if (spec.Min < -0.01f)
                {
                    var mag = Lerp(0.15f, 0.85f, SampleBeta(rng, 2f, 2.2f));
                    var sign = rng.NextDouble() < 0.5 ? -1f : 1f;
                    return sign * mag * MathF.Min(MathF.Abs(spec.Min), spec.Max);
                }

                return Lerp(spec.Min, spec.Min + 0.65f * range, SampleBeta(rng, 2f, 2.6f));
            default:
                if (paramId.EndsWith("Db", StringComparison.Ordinal) && spec.Min < 0f && spec.Max > 0f)
                    // A symmetric dB knob is a CUT or a BOOST and mostly a small one.
                    return Lerp(spec.Min * 0.45f, spec.Max * 0.45f, SampleBeta(rng, 3f, 3f));
                break;
        }

        // 6. nothing known: the middle 80 % of the declared range.
        return Lerp(spec.Min + 0.1f * range, spec.Max - 0.1f * range, SampleBeta(rng, 2f, 2f));
    }

    private static float Rescale(float unit01, float lo, float hi, in DcRigTargetCatalog.ParamSpec spec)
    {
        // The drive windows are written in 0..1 "how hard" units because every drive
        // knob in the catalogue IS 0..1; map them onto the param's own range anyway so
        // one that is not (a future dB-valued drive) still lands somewhere sane.
        var t = lo + unit01 * (hi - lo);
        return spec is { Min: 0f, Max: 1f } ? t : spec.Min + t * (spec.Max - spec.Min);
    }

    private static float Lerp(float lo, float hi, float t) => lo + (hi - lo) * t;

    /// <summary>Pushes a value off the catalogue default if it landed on it. The
    /// direction is whichever side has room, so the result is always still in range.</summary>
    private static float Nudge(float value, in DcRigTargetCatalog.ParamSpec spec)
    {
        var range = spec.Max - spec.Min;
        if (range <= 0f) return value;
        var margin = range * DefaultAvoidance;
        if (MathF.Abs(value - spec.Default) > margin) return value;

        var up = spec.Default + margin;
        var down = spec.Default - margin;
        if (up <= spec.Max) return up;
        return down >= spec.Min ? down : value;
    }

    // ------------------------------------------------------------------ Beta

    /// <summary>Beta(a, b) on (0, 1) from two Marsaglia–Tsang gammas. <see cref="Random" />
    /// only offers a uniform, and the whole point of these priors is that they are NOT
    /// uniform — a knob drawn flat across its range spends most of the corpus at
    /// settings no player has ever used.</summary>
    private static float SampleBeta(Random rng, float a, float b)
    {
        var x = SampleGamma(rng, a);
        var y = SampleGamma(rng, b);
        var s = x + y;
        if (s <= 0d || double.IsNaN(s)) return 0.5f;
        return (float)Math.Clamp(x / s, 0d, 1d);
    }

    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1d)
        {
            // Johnk / boost: Gamma(a) = Gamma(a+1) * U^(1/a).
            var u = rng.NextDouble();
            while (u <= 0d) u = rng.NextDouble();
            return SampleGamma(rng, shape + 1d) * Math.Pow(u, 1d / shape);
        }

        var d = shape - 1d / 3d;
        var c = 1d / Math.Sqrt(9d * d);
        while (true)
        {
            double x, v;
            do
            {
                x = NextGaussian(rng);
                v = 1d + c * x;
            } while (v <= 0d);

            v = v * v * v;
            var u = rng.NextDouble();
            if (u < 1d - 0.0331d * x * x * x * x) return d * v;
            if (Math.Log(u) < 0.5d * x * x + d * (1d - v + Math.Log(v))) return d * v;
        }
    }

    private static double NextGaussian(Random rng)
    {
        var u1 = rng.NextDouble();
        while (u1 <= 0d) u1 = rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2);
    }
}
