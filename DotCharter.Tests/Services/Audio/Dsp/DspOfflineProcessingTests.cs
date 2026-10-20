using System.Runtime.InteropServices;
using DotCharter.Tone.Audio.Dsp;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// Headless offline coverage of the whole in-house DSP tone rig, exercised
/// through the real native DLL via the tooling export
/// <c>dca_engine_dsp_process_buffer</c> (stereo buffer through the live
/// <c>DspChain</c>, the same code the input callback runs). No devices, no
/// windows: the rig prepares lazily at 48 kHz when no device ever started.
///
/// The module list is enumerated from the LIVE native catalogue (not hard-coded),
/// so every module the DLL ships — 57 at 0.18 — is automatically covered, and a
/// new module is picked up the day it lands. Four properties are asserted per
/// module:
///
/// <list type="number">
/// <item><b>Block-size invariance</b> — the same deterministic input fed in
///   per-call chunks of {1, 32, 441, 1024} frames must produce the same output
///   (RMS of the difference &lt; -60 dBFS). The offline export chunks internally
///   to 512 and keeps module state across calls, so varying the call size varies
///   the block grid the module sees. Measured: every module is BIT-EXACT across
///   block sizes except <c>miku</c>, whose v1 autocorrelation pitch analysis is
///   tied to the block (documented in JuceSharp/docs/agents/dsp-tone-rig.md:
///   "miku v1 does tie it to the block and costs 3x more at block=64"); it is the lone
///   <see cref="BlockSizeVariant"/> and gets a bounded-output check instead.</item>
/// <item><b>Silence in → floor out</b> — a silent input must not wake a module
///   into audible output (catches a self-oscillator or a NaN-latched IIR). The
///   distortion family has a legitimate non-zero idle floor (a waveshaper maps
///   0 → a small DC/bias; fuzz is loudest at ~-48 dBFS), so the gate is a
///   generous -35 dBFS — still ~50 dB below a runaway.</item>
/// <item><b>Hostile input → finite</b> — NaN / +Inf / ±1e6 samples must never
///   leak a non-finite sample out. This pins <c>DspChain::process</c>'s scrub
///   (input + after every stage), the guard that stops one bad sample latching
///   a downstream feedback/IIR core into permanent NaN.</item>
/// <item><b>Bypass = bit-exact passthrough</b> — a bypassed node must return the
///   input unchanged, sample-for-sample, for every module.</item>
/// </list>
///
/// Everything skips cleanly with <see cref="Assert.SkipWhen"/> when the native
/// DLL / audio engine can't load, so it never depends on native being present.
/// Total runtime is a few seconds (block=1 over 0.5 s is ~10 ms per module), so
/// it stays in the default gate rather than behind a soak env-var.
/// </summary>
[Collection("NativeEngine")]
public sealed class DspOfflineProcessingTests : IClassFixture<DspOfflineProcessingTests.NativeDspFixture>
{
    private const string Lib = "juce_sharp";
    private const int Sr = 48000;
    private const int N = Sr / 2; // 0.5 s — enough sweep + LFO cycles; keeps block=1 fast.

    /// <summary>Modules whose output legitimately depends on the block grid, with
    /// the reason. Only <c>miku</c> (v1 autocorrelation vocal synth) qualifies —
    /// everything else, including the WSOLA pitch shifters (harmonizer, whammy)
    /// and mikustomp, is bit-exact across block sizes.</summary>
    private static readonly HashSet<string> BlockSizeVariant = new(StringComparer.Ordinal) { "miku" };

    private readonly NativeDspFixture _fx;

    public DspOfflineProcessingTests(NativeDspFixture fx) => _fx = fx;

    [DllImport(Lib, EntryPoint = "dca_engine_dsp_process_buffer", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DspProcessBuffer(nint engine, float[] left, float[] right, int numFrames, int sampleRate);

    [DllImport(Lib, EntryPoint = "dca_engine_dsp_add_node", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DspAddNode(nint engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string typeId);

    [DllImport(Lib, EntryPoint = "dca_engine_dsp_set_bypass", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DspSetBypass(nint engine, int nodeId, int bypassed);

    [DllImport(Lib, EntryPoint = "dca_engine_dsp_clear", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DspClear(nint engine);

    /// <summary>Catalogue module ids, enumerated from the native catalogue at
    /// discovery time; a single sentinel when the native surface can't load so
    /// the theory reports one clean skip instead of "no data".</summary>
    public static TheoryData<string> ModuleIds()
    {
        var data = new TheoryData<string>();
        try
        {
            var svc = new JuceDspChainService();
            svc.LoadCatalog();
            var ids = svc.Catalog.Select(m => m.Id).ToList();
            if (ids.Count == 0) { data.Add(Sentinel); return data; }
            foreach (var id in ids) data.Add(id);
        }
        catch
        {
            data.Add(Sentinel);
        }

        return data;
    }

    private const string Sentinel = "__native_unavailable__";

    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void BlockSizeInvariance_SameOutputAcrossCallChunkSizes(string moduleId)
    {
        SkipIfUnavailable(moduleId);
        var h = _fx.Handle;

        var inL = MakeInput(N, 11);
        var inR = MakeInput(N, 22);

        // Reference: the largest chunk (internally 512-blocked).
        var reference = FreshProcess(h, moduleId, inL, inR, 1024);
        AssertFinite(reference, moduleId, "reference");

        if (BlockSizeVariant.Contains(moduleId))
        {
            // Block-tied module: we don't require nulling, only that every block
            // size stays finite and bounded (no divergence / blow-up).
            foreach (var cs in new[] { 1, 32, 441 })
            {
                var v = FreshProcess(h, moduleId, inL, inR, cs);
                AssertFinite(v, moduleId, $"cs={cs}");
                Assert.True(Peak(v.L) < 4f && Peak(v.R) < 4f,
                    $"{moduleId}: block-variant output blew up at cs={cs} (peak {Math.Max(Peak(v.L), Peak(v.R)):F2}).");
            }

            return;
        }

        foreach (var cs in new[] { 1, 32, 441 })
        {
            var v = FreshProcess(h, moduleId, inL, inR, cs);
            var db = Math.Max(NullDb(v.L, reference.L), NullDb(v.R, reference.R));
            Assert.True(db < -60.0,
                $"{moduleId}: output at chunk={cs} does not null against chunk=1024 " +
                $"(RMS diff {db:F1} dBFS >= -60). The module's processing depends on the host block grid.");
        }
    }

    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void Bypass_IsBitExactPassthrough(string moduleId)
    {
        SkipIfUnavailable(moduleId);
        var h = _fx.Handle;

        var inL = MakeInput(N, 11);
        var inR = MakeInput(N, 22);

        DspClear(h);
        var nid = DspAddNode(h, moduleId);
        Assert.True(nid >= 0, $"{moduleId}: add failed");
        Assert.Equal(0, DspSetBypass(h, nid, 1));

        var outL = (float[])inL.Clone();
        var outR = (float[])inR.Clone();
        ProcessChunked(h, outL, outR, 512);

        Assert.True(outL.AsSpan().SequenceEqual(inL), $"{moduleId}: bypassed L is not bit-exact passthrough");
        Assert.True(outR.AsSpan().SequenceEqual(inR), $"{moduleId}: bypassed R is not bit-exact passthrough");
    }

    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void SilenceIn_StaysBelowFloor(string moduleId)
    {
        SkipIfUnavailable(moduleId);
        var h = _fx.Handle;

        var v = FreshProcess(h, moduleId, new float[N], new float[N], 512);
        AssertFinite(v, moduleId, "silence");

        // No module (even the self-oscillating feedbacker, which is input-driven)
        // may wake into audible output from digital silence. The distortion family
        // has a real idle floor (fuzz ~-48 dBFS is the loudest); -35 dBFS leaves
        // that ~13 dB of head-room while still catching a runaway near unity.
        var db = Math.Max(RmsDb(v.L), RmsDb(v.R));
        Assert.True(db < -35.0, $"{moduleId}: silent input produced {db:F1} dBFS out (>= -35 dBFS floor).");
    }

    [Theory]
    [MemberData(nameof(ModuleIds))]
    public void HostileInput_OutputStaysFinite(string moduleId)
    {
        SkipIfUnavailable(moduleId);
        var h = _fx.Handle;

        var inL = MakeInput(N, 11);
        var hostileL = new float[N];
        var hostileR = new float[N];
        for (var i = 0; i < N; i++)
        {
            hostileL[i] = i % 7 == 0 ? float.NaN
                : i % 5 == 0 ? float.PositiveInfinity
                : i % 11 == 0 ? -1e6f
                : i % 3 == 0 ? 1e6f
                : inL[i];
            hostileR[i] = hostileL[i];
        }

        var v = FreshProcess(h, moduleId, hostileL, hostileR, 512);
        // DspChain::process scrubs non-finite at input and after every stage.
        AssertFinite(v, moduleId, "hostile");
    }

    private void SkipIfUnavailable(string moduleId)
    {
        Assert.SkipWhen(moduleId == Sentinel || _fx.Handle == nint.Zero,
            "native DSP ABI (juce_sharp.dll) / audio engine not loadable in this test environment");
        Assert.SkipWhen(!_fx.Catalog.Contains(moduleId), $"native catalogue has no '{moduleId}' module");
    }

    // --- processing helpers ---------------------------------------------------

    /// <summary>Adds a fresh single-module chain and processes a copy of the input
    /// fed in <paramref name="chunk"/>-frame calls (state persists across calls;
    /// a fresh node resets the module's own state).</summary>
    private static (float[] L, float[] R) FreshProcess(nint h, string moduleId, float[] l, float[] r, int chunk)
    {
        DspClear(h);
        var nid = DspAddNode(h, moduleId);
        Assert.True(nid >= 0, $"{moduleId}: add failed");
        var ol = (float[])l.Clone();
        var or = (float[])r.Clone();
        ProcessChunked(h, ol, or, chunk);
        return (ol, or);
    }

    private static void ProcessChunked(nint h, float[] l, float[] r, int chunk)
    {
        var sl = new float[chunk];
        var sr = new float[chunk];
        for (var pos = 0; pos < l.Length; pos += chunk)
        {
            var n = Math.Min(chunk, l.Length - pos);
            Array.Copy(l, pos, sl, 0, n);
            Array.Copy(r, pos, sr, 0, n);
            Assert.Equal(0, DspProcessBuffer(h, sl, sr, n, Sr));
            Array.Copy(sl, 0, l, pos, n);
            Array.Copy(sr, 0, r, pos, n);
        }
    }

    private static float[] MakeInput(int n, int seed)
    {
        var rng = new Random(seed);
        var buf = new float[n];
        var dur = n / (double)Sr;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Sr;
            // log sine sweep 80 Hz -> 6 kHz + seeded broadband noise.
            var sweep = Math.Sin(2 * Math.PI * (80.0 * Math.Pow(75.0, t / dur)) * t);
            buf[i] = (float)((rng.NextDouble() * 2 - 1) * 0.15 + 0.2 * sweep);
        }

        return buf;
    }

    private static double Db(double lin) => 20 * Math.Log10(lin + 1e-20);

    private static double NullDb(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) { var d = (double)a[i] - b[i]; s += d * d; }
        return Db(Math.Sqrt(s / a.Length));
    }

    private static double RmsDb(float[] a)
    {
        double s = 0;
        foreach (var v in a) s += (double)v * v;
        return Db(Math.Sqrt(s / a.Length));
    }

    private static float Peak(float[] a)
    {
        var p = 0f;
        foreach (var v in a) { var m = Math.Abs(v); if (m > p) p = m; }
        return p;
    }

    private static void AssertFinite((float[] L, float[] R) v, string moduleId, string what)
    {
        foreach (var x in v.L) Assert.True(float.IsFinite(x), $"{moduleId}: non-finite L sample ({what})");
        foreach (var x in v.R) Assert.True(float.IsFinite(x), $"{moduleId}: non-finite R sample ({what})");
    }

    /// <summary>One live engine + attached chain for the whole class (creating an
    /// engine per theory case would be 200+ engine spins). Null-safe: if native
    /// can't load, <see cref="Handle"/> stays zero and every case skips.</summary>
    public sealed class NativeDspFixture : IDisposable
    {
        private readonly JuceAudioEngine? _engine;

        public NativeDspFixture()
        {
            try
            {
                _engine = new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
                _engine.Stop(); // offline path must be the only processor
                Handle = _engine.NativeHandle;

                var svc = new JuceDspChainService();
                svc.LoadCatalog();
                Catalog = svc.Catalog.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                _engine?.Dispose();
                _engine = null;
                Handle = nint.Zero;
                Catalog = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        public nint Handle { get; }
        public HashSet<string> Catalog { get; }

        public void Dispose()
        {
            try { _engine?.Dispose(); } catch { /* ignored */ }
        }
    }
}
