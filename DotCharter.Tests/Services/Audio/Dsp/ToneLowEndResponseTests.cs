using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Offline magnitude-response gate on the tone rig's LOW END, run through the
/// real native DLL (<c>dca_engine_dsp_process_buffer</c> — the same code the
/// input callback runs). No devices, no windows.
///
/// <para><b>Why this exists.</b> Every factory and user tone measured 9-18 dB
/// below its own midband at the guitar's lowest fundamentals (owner report:
/// "every tone sounds bass-light / thin"). The cause was not one filter: it was
/// three separate places that modelled a BOUNDED circuit network as an
/// UNBOUNDED high-pass and then cascaded them — the amp's inter-stage coupling
/// caps (2-4 first-order HPFs at 60-300 Hz), the overdrive's pre-clip bass cut
/// (a 2nd-order Butterworth in the MAIN path) and the distortion's tight/body
/// pair (two more). Each looked individually reasonable; they summed to 30 dB.
/// Each one is now a shelf with a finite depth (dcdsp_primitives.h
/// <c>OnePole::setHighpassShelf</c>, and <c>Biquad::setLowShelf</c> for the
/// pedals), and the cab gained the driver Fs resonance its IR bank never had.</para>
///
/// <para><b>Method.</b> Every stage here is non-linear, so "frequency response"
/// only exists as a describing function: drive a settled sine at an integer
/// frequency and read the output component AT that frequency with a single-bin
/// DFT over exactly one second (48 kHz / 48000 samples = 1 Hz bins, so the
/// probe is leakage-free and every harmonic lands in a different bin). Levels
/// are quoted against the log-mean of the 200 Hz-4 kHz probes — the same band
/// <c>dcdsp_cab_irs.h</c> normalises its IR bank over, and the only
/// notch-immune reference available (several cab IRs have a null at 1 kHz).</para>
///
/// <para><b>What the numbers mean.</b> The thresholds are deliberately loose —
/// they are a REGRESSION floor, not the tuning target. The measured values at
/// the time of writing are in each assertion message; a change that moves one
/// by a dB or two is fine, a change that puts the low E back 10 dB under the
/// midband is the bug this test exists to catch. The full per-stage tables are
/// reproducible with <c>JuceSharp/src/JuceSharp.Native/tools/tone_response.cpp</c>.</para>
/// </summary>
[Collection("NativeEngine")]
public sealed class ToneLowEndResponseTests : IClassFixture<ToneLowEndResponseTests.RigFixture>
{
    private const int Sr = 48000;

    /// <summary>E2 — the low E of a standard-tuned guitar (82.41 Hz), rounded to
    /// the exact 1 Hz DFT bin. This is the note the whole report was about.</summary>
    private const int LowE = 82;

    /// <summary>The midband reference probes (200 Hz - 4 kHz).</summary>
    private static readonly int[] Midband = [200, 315, 500, 800, 1250, 2000, 3150];

    private readonly RigFixture _fx;

    public ToneLowEndResponseTests(RigFixture fx) => _fx = fx;

    [Fact]
    public void Amp_AtCatalogueDefaults_KeepsTheLowEWithinAFewDbOfTheMidband()
    {
        var chain = Require();

        // Every voicing, because the coupling budget is per-voicing: the old
        // unbounded cascade was worst exactly on the hi-gain models people
        // reach for (Five-Oh-One measured -15.0 dB here, Modern Djent -13.1).
        for (var voicing = 0; voicing < 24; voicing++)
        {
            var amp = BuildAmp(chain, voicing);
            var rel = LowEVersusMidband(chain);
            chain.RemoveNode(amp.NodeId);

            Assert.True(rel > -6.0,
                $"amp voicing {voicing}: the low E sits {rel:F2} dB under the 200 Hz-4 kHz midband " +
                "(floor -6 dB). Before the 0.35 coupling-shelf fix the range was -0.1 .. -15.0 dB; " +
                "after it, -0.1 .. -3.5 dB. A regression here means an in-path bass filter went " +
                "back to being an unbounded high-pass — see dcdsp_amp.h's cplCutDb.");
            Assert.True(rel < 6.0,
                $"amp voicing {voicing}: the low E sits {rel:F2} dB ABOVE the midband (ceiling +6 dB) — " +
                "a coupling budget or a tone-stack offset has gone the other way.");
        }
    }

    [Fact]
    public void AmpIntoCab_AtCatalogueDefaults_KeepsTheLowEWithinAFewDbOfTheMidband()
    {
        var chain = Require();

        // The cab is the second half of the fix: the bundled IR bank has a box
        // rolloff but NO driver Fs resonance, so model 0 alone measured -7.4 dB
        // at the low E. dcdsp_cab.h's kResonance restores it. Every model,
        // including the 14 built from measured driver curves in #74 (12-25).
        for (var model = 0; model < 26; model++)
        {
            var amp = BuildAmp(chain, voicing: 1);          // Plexi 50, the catalogue default
            var cab = chain.AddNode("cab")
                      ?? throw new InvalidOperationException("cab node add failed");
            chain.SetParam(cab.NodeId, "model", model);

            var rel = LowEVersusMidband(chain);
            chain.RemoveNode(cab.NodeId);
            chain.RemoveNode(amp.NodeId);

            Assert.True(rel > -9.0,
                $"amp -> cab model {model}: the low E sits {rel:F2} dB under the 200 Hz-4 kHz midband " +
                "(floor -9 dB; the 1x8 Tweed and the open-back AC are legitimately the thinnest). " +
                "Before the 0.35 fixes the amp+cab presets measured -9 .. -18 dB here.");
        }
    }

    /// <summary>
    ///     #75: the loudness walker's mirror of the micpos off-axis LEVEL
    ///     (<c>DcRigLoudnessNormalizer.MicOffAxisLevelDb</c>) is what the native
    ///     core does — measured at 1 kHz on every mic, 45 and 60 degrees against
    ///     on axis, at 30 cm (no proximity, so only the polar stage moves).
    ///     Tolerance 0.5 dB: the 57's two bounded shelves reach 1 kHz a little.
    /// </summary>
    [Fact]
    public void MicOffAxisLevel_TheWalkersMirror_MatchesTheNativeCore()
    {
        var chain = Require();
        for (var mic = 0; mic <= 4; mic++)
        {
            foreach (var angle in new[] { 45f, 60f })
            {
                double At(float a)
                {
                    var node = chain.AddNode("micpos") ?? throw new InvalidOperationException("micpos add failed");
                    chain.SetParam(node.NodeId, "mic", mic);
                    chain.SetParam(node.NodeId, "position", 0f);
                    chain.SetParam(node.NodeId, "distanceCm", 15f);
                    chain.SetParam(node.NodeId, "level", 1f / 1.3f);
                    chain.SetParam(node.NodeId, "angleDeg", a);
                    var db = ProbeDb(chain, 1000);
                    chain.RemoveNode(node.NodeId);
                    return db;
                }

                var measured = At(angle) - At(0f);
                var mirrored = DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigLoudnessNormalizer.MicOffAxisLevelDb(mic, angle);
                Assert.True(Math.Abs(measured - mirrored) < 0.5,
                    $"micpos mic {mic} at {angle} deg: the core loses {measured:F2} dB at 1 kHz, the walker's mirror says {mirrored:F2}");
            }
        }
    }

    [Fact]
    public void ScreamerBoost_IsMidForward_ButStillPassesTheFundamental()
    {
        var chain = Require();

        // The screamer's pre-clip cut used to be a 2nd-order Butterworth in the
        // MAIN path, putting model 1 at -20.6 dB while model 0 (which has no
        // pre-cut at all) sat at -2.9. A real TS puts that cut in the op-amp's
        // FEEDBACK leg, so it is bounded by the depth of the mid hump.
        var od = chain.AddNode("overdrive")
                 ?? throw new InvalidOperationException("overdrive node add failed");
        chain.SetParam(od.NodeId, "model", 1);
        chain.SetParam(od.NodeId, "drive", 0.15f);
        chain.SetParam(od.NodeId, "tone", 0.65f);
        chain.SetParam(od.NodeId, "level", 0.85f);

        var rel = LowEVersusMidband(chain);
        chain.RemoveNode(od.NodeId);

        Assert.True(rel > -15.0,
            $"screamer overdrive: the low E sits {rel:F2} dB under its own midband (floor -15 dB). " +
            "It is SUPPOSED to be mid-forward — measured -11.9 dB after the fix, -20.6 before — " +
            "but a Tube Screamer does not remove the fundamental. See dcdsp_overdrive.h preCutDb.");
    }

    [Fact]
    public void DistortionPedal_AtCatalogueDefaults_StillPassesTheFundamental()
    {
        var chain = Require();

        // tight + body were two cascaded 2nd-order Butterworths; every model
        // measured -11.5 .. -19.7 dB at the low E. Now bounded shelves
        // (kTightCutDb / kBodyCutDb), measured -2.1 .. -6.5 dB.
        for (var model = 0; model < 4; model++)
        {
            var d = chain.AddNode("distortion")
                    ?? throw new InvalidOperationException("distortion node add failed");
            chain.SetParam(d.NodeId, "model", model);

            var rel = LowEVersusMidband(chain);
            chain.RemoveNode(d.NodeId);

            Assert.True(rel > -10.0,
                $"distortion model {model}: the low E sits {rel:F2} dB under its own midband " +
                "(floor -10 dB; measured -2.1 .. -6.5 after the fix, -11.5 .. -19.7 before). " +
                "See dcdsp_distortion.h kTightCutDb / kBodyCutDb.");
        }
    }

    // --- measurement ----------------------------------------------------------

    private DspNode BuildAmp(JuceDspChainService chain, int voicing)
    {
        var amp = chain.AddNode("amp") ?? throw new InvalidOperationException("amp node add failed");
        chain.SetParam(amp.NodeId, "voicing", voicing);
        return amp;
    }

    /// <summary>Level of the low-E fundamental relative to the log-mean of the
    /// midband probes, in dB. Positive = more low end than midband.</summary>
    private double LowEVersusMidband(JuceDspChainService chain)
    {
        var low = ProbeDb(chain, LowE);
        var mid = Midband.Average(f => ProbeDb(chain, f));
        return low - mid;
    }

    /// <summary>Drives a settled sine at <paramref name="freq"/> Hz through the
    /// chain and returns the output amplitude AT that frequency, in dB relative
    /// to the input amplitude. The input is -18 dBFS peak — the same reference
    /// guitar level <c>DcRigLoudnessNormalizer</c> uses, so the non-linear
    /// stages are measured where they actually run.</summary>
    private double ProbeDb(JuceDspChainService chain, int freq)
    {
        const double inAmp = 0.12589254; // -18 dBFS
        const int settle = (int)(Sr * 0.4); // amp supply sag releases in 220 ms
        const int window = Sr;              // exactly 1 s => 1 Hz bins
        const int block = 512;

        var w = 2.0 * Math.PI * freq / Sr;
        var l = new float[block];
        var r = new float[block];
        long phase = 0;
        double re = 0, im = 0;
        long n = 0;

        void Run(int count, bool measuring)
        {
            for (var i = 0; i < count; i++)
            {
                l[i] = (float)(inAmp * Math.Sin(w * (phase + i)));
                r[i] = l[i];
            }

            phase += count;
            if (!chain.ProcessOfflineBuffer(l, r, count, Sr))
                throw new InvalidOperationException("dca_engine_dsp_process_buffer failed");
            if (!measuring) return;
            for (var i = 0; i < count; i++)
            {
                var ph = w * (n + i);
                re += l[i] * Math.Cos(ph);
                im -= l[i] * Math.Sin(ph);
            }

            n += count;
        }

        for (var done = 0; done < settle; done += block) Run(Math.Min(block, settle - done), false);
        for (var done = 0; done < window; done += block) Run(Math.Min(block, window - done), true);

        var amp = n <= 0 ? 0 : 2.0 * Math.Sqrt(re * re + im * im) / n;
        return 20 * Math.Log10(amp / inAmp + 1e-20);
    }

    private JuceDspChainService Require()
    {
        Assert.SkipWhen(_fx.Chain is null,
            "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");
        var chain = _fx.Chain!;
        foreach (var n in chain.GetNodes().ToList()) chain.RemoveNode(n.NodeId);
        return chain;
    }

    /// <summary>One device-less engine + attached chain for the whole class.
    /// Null when native can't load, so every case skips.</summary>
    public sealed class RigFixture : IDisposable
    {
        private readonly JuceAudioEngine? _engine;

        public RigFixture()
        {
            try
            {
                _engine = new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
                _engine.Stop(); // the offline path must be the only processor
                var chain = new JuceDspChainService();
                chain.LoadCatalog();
                chain.AttachEngine(_engine);
                Chain = chain;
            }
            catch
            {
                _engine?.Dispose();
                _engine = null;
                Chain = null;
            }
        }

        public JuceDspChainService? Chain { get; }

        public void Dispose()
        {
            try { _engine?.Dispose(); } catch { /* ignored */ }
        }
    }
}
