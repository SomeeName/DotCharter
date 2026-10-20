namespace DotCharter.Tools.RigDataset.Beds;

/// <summary>
///     The audio half of a bed, before the rig: three hand-written voices that turn a
///     <see cref="BedLayerPlan" />'s notes into samples.
///     <para>
///         <b>Why synthesise rather than reach for the engine's own instruments.</b>
///         The engine's pitch and synth modules (<c>basssynth</c>, <c>subharmonic</c>,
///         <c>harmonizer</c>) are all TRACKERS — they need an input signal to follow,
///         and the only input this pipeline has is guitar DI, which is precisely what a
///         bed must not contain. So the note sources are written here, in about a
///         hundred lines of oscillators and envelopes, and the CHARACTER comes from the
///         engine afterwards (<c>bassamp</c>, <c>transient</c>, <c>multiband</c>,
///         <c>chorus</c>, the reverbs). That split is also what keeps this file pure
///         and therefore in the default gate.
///     </para>
///     <para>
///         <b>Every random number is drawn from the bed's own seed.</b> Noise is the
///         one thing in a bed that is not a deterministic function of the plan, so it
///         is generated from a seeded generator rather than <c>Random.Shared</c>; a bed
///         must re-render bit-identically from its sidecar or the sidecar is a story
///         rather than a record.
///     </para>
/// </summary>
public static class BedSynth
{
    /// <summary>The level each layer is normalised to before it meets its rig, so a
    /// drive or a compressor in a bed rig sees a predictable input.</summary>
    public const float LayerRmsDbfs = -18f;

    /// <summary>Renders one layer to mono, un-rigged, normalised to
    /// <see cref="LayerRmsDbfs" />.</summary>
    /// <param name="plan">The bed the layer belongs to (tempo, rate, length).</param>
    /// <param name="ordinal">The layer's position in <c>plan.Layers</c>; folded into
    /// the noise seed so two layers never share a noise sequence.</param>
    public static float[] RenderLayer(BedPlan plan, int ordinal)
    {
        var layer = plan.Layers[ordinal];
        var rate = plan.SampleRate;
        var frames = (int)Math.Round(plan.Seconds * rate);
        var buf = new float[frames];
        var spb = 60.0 / plan.TempoBpm; // seconds per beat
        var noise = new Random(RenderCommand.SeedFor(plan.Seed, ordinal, attempt: 7));

        foreach (var note in layer.Notes)
        {
            var start = (int)Math.Round(note.StartBeats * spb * rate);
            if (start >= frames) continue;
            var held = note.LengthBeats * spb;
            switch (layer.Voice)
            {
                case BedVoices.Bass:
                    Bass(buf, start, rate, Hz(note.Midi), held, note.Velocity);
                    break;
                case BedVoices.Keys:
                    Pad(buf, start, rate, Hz(note.Midi), held, note.Velocity);
                    break;
                case BedVoices.Drums:
                    Drum(buf, start, rate, note.Midi, note.Velocity, noise);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(plan),
                        $"unknown bed voice '{layer.Voice}'.");
            }
        }

        Augmentation.Normalise(buf, new Random(plan.Seed ^ (ordinal + 1)), LayerRmsDbfs, 0f);
        return buf;
    }

    private static double Hz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

    // ----------------------------------------------------------------- voices

    /// <summary>Plucked bass: saw + a sub sine an octave down, through a one-pole
    /// whose cutoff follows the envelope. The cutoff tracking is what makes it read
    /// as a bass rather than as a buzz — a static filter on a saw sounds like neither
    /// a finger nor a pick.</summary>
    private static void Bass(float[] buf, int start, int rate, double hz, double heldSeconds, float vel)
    {
        var total = heldSeconds + 0.12;
        var n = Math.Min(buf.Length - start, (int)(total * rate));
        double phase = 0, sub = 0, lp = 0;
        var inc = hz / rate;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var env = Env(t, heldSeconds, attack: 0.004, decay: 0.25, sustain: 0.55, release: 0.12);
            phase += inc;
            if (phase >= 1) phase -= 1;
            sub += inc * 0.5;
            if (sub >= 1) sub -= 1;

            var saw = 2.0 * phase - 1.0;
            var raw = 0.7 * saw + 0.5 * Math.Sin(2 * Math.PI * sub);
            var cutoff = 180.0 + 2200.0 * env;
            var a = Math.Exp(-2 * Math.PI * cutoff / rate);
            lp = (1 - a) * raw + a * lp;
            buf[start + i] += (float)(lp * env * vel * 0.6);
        }
    }

    /// <summary>Slow pad voice: three saws detuned by ±7 cents. Detuning is the whole
    /// sound; one saw is an organ.</summary>
    private static void Pad(float[] buf, int start, int rate, double hz, double heldSeconds, float vel)
    {
        var total = heldSeconds + 0.45;
        var n = Math.Min(buf.Length - start, (int)(total * rate));
        Span<double> phase = [0.11, 0.47, 0.83];
        Span<double> inc =
        [
            hz * Math.Pow(2, -7 / 1200.0) / rate,
            hz / rate,
            hz * Math.Pow(2, 7 / 1200.0) / rate
        ];
        double lp = 0;
        var a = Math.Exp(-2 * Math.PI * 2600.0 / rate);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var env = Env(t, heldSeconds, attack: 0.18, decay: 0.4, sustain: 0.8, release: 0.45);
            double raw = 0;
            for (var v = 0; v < 3; v++)
            {
                phase[v] += inc[v];
                if (phase[v] >= 1) phase[v] -= 1;
                raw += 2.0 * phase[v] - 1.0;
            }

            lp = (1 - a) * (raw / 3.0) + a * lp;
            buf[start + i] += (float)(lp * env * vel * 0.35);
        }
    }

    /// <summary>Kick (36), snare (38), closed (42) and open (46) hat. Anything else
    /// is silence rather than a throw: a plan is data, and a drum key nobody wrote a
    /// voice for is a missing feature, not a corrupt bed.</summary>
    private static void Drum(float[] buf, int start, int rate, int key, float vel, Random noise)
    {
        switch (key)
        {
            case 36:
            {
                var n = Math.Min(buf.Length - start, (int)(0.35 * rate));
                double phase = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = i / (double)rate;
                    var hz = 45.0 + 85.0 * Math.Exp(-t / 0.028);
                    phase += hz / rate;
                    var amp = Math.Exp(-t / 0.17) * (1 - Math.Exp(-t / 0.0015));
                    buf[start + i] += (float)(Math.Sin(2 * Math.PI * phase) * amp * vel * 0.95);
                }

                break;
            }
            case 38:
            {
                var n = Math.Min(buf.Length - start, (int)(0.3 * rate));
                double bp1 = 0, bp2 = 0, body = 0;
                var a = Math.Exp(-2 * Math.PI * 1900.0 / rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i / (double)rate;
                    var w = noise.NextDouble() * 2 - 1;
                    bp1 = (1 - a) * w + a * bp1;          // lp
                    bp2 = (1 - a) * (w - bp1) + a * bp2;  // band-ish: hp then lp
                    body += 190.0 / rate;
                    var amp = Math.Exp(-t / 0.095);
                    buf[start + i] += (float)((bp2 * 2.2 + Math.Sin(2 * Math.PI * body) * 0.45)
                                               * amp * vel * 0.55);
                }

                break;
            }
            case 42:
            case 46:
            {
                var decay = key == 46 ? 0.24 : 0.032;
                var n = Math.Min(buf.Length - start, (int)(decay * 5 * rate));
                double lp = 0;
                var a = Math.Exp(-2 * Math.PI * 7000.0 / rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i / (double)rate;
                    var w = noise.NextDouble() * 2 - 1;
                    lp = (1 - a) * w + a * lp;
                    buf[start + i] += (float)((w - lp) * Math.Exp(-t / decay) * vel * 0.28);
                }

                break;
            }
        }
    }

    /// <summary>A plain ADSR with an exponential release, evaluated per sample. The
    /// attack is never zero — a hard edge on a saw is a click, and a corpus of clicks
    /// teaches the separator's artefacts about clicks.</summary>
    private static double Env(double t, double held, double attack, double decay, double sustain,
        double release)
    {
        if (t < attack) return t / attack;
        if (t < held)
        {
            var d = Math.Exp(-(t - attack) / decay);
            return sustain + (1 - sustain) * d;
        }

        var atEnd = sustain + (1 - sustain) * Math.Exp(-(held - attack) / decay);
        return atEnd * Math.Exp(-(t - held) / (release / 3.0));
    }
}
