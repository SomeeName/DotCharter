using System.Globalization;
using System.Text;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
/// REAL-engine tests for the 0.36 <c>namamp</c> work: the amp face and the rate
/// converter that replaced the 48 kHz dry-bypass. Everything runs offline through
/// <see cref="JuceDspChainService.ProcessOfflineBuffer"/> (the
/// <c>dca_engine_dsp_process_buffer</c> tooling export), so no audio DEVICE is
/// involved and the engine sample rate is whatever the first offline call asks
/// for — which is the only way to exercise the 44.1 kHz path deterministically.
/// Everything skips cleanly when the native DLL / engine is unavailable or the
/// DLL was built without <c>DOTCHARTER_DSP_NEURAL</c>.
/// </summary>
[Collection("NativeEngine")]
public sealed class NamAmpFaceNativeTests
{
    private const string Namamp = "namamp";
    // A deadlock detector, never a budget: the poll returns the moment the model
    // reports loaded, and this lane shares the box with the gate's parallel phase.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Every .nam is 48 kHz-native (the format defaults sample_rate to it).</summary>
    private const int NamCaptureRate = 48_000;

    /// <summary>
    /// The native catalogue and the managed hand-mirror must agree on every
    /// namamp param — id, range and DEFAULT. The default is the load-bearing half:
    /// <c>DcRigTargetCatalog.FillDefaults</c> pushes it into every preset that
    /// omits the param, so a drifted default silently re-voices every capture
    /// preset ever authored (exactly how the cab's <c>lowCutHz</c> 80→60 move bit
    /// in 0.35).
    /// </summary>
    [Fact]
    public void TheAmpFaceParams_MirrorTheNativeCatalogue()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable");
        var native = env!.Chain.Catalog.FirstOrDefault(m => m.Id == Namamp);
        Assert.SkipWhen(native is null, "native catalogue has no 'namamp' (built without DOTCHARTER_DSP_NEURAL)");

        foreach (var id in new[] { "gain", "bass", "mid", "treble", "presence", "resonance", "master" })
        {
            var n = native!.Params.FirstOrDefault(p => p.Id == id);
            Assert.True(n is not null, $"native namamp is missing the '{id}' param");
            Assert.True(DcRigTargetCatalog.TryGetParam(Namamp, id, out var m),
                $"DcRigTargetCatalog is missing the namamp '{id}' param");

            Assert.Equal(n!.Min, m.Min, 5);
            Assert.Equal(n.Max, m.Max, 5);
            Assert.Equal(n.Default, m.Default, 5);
            // Neutral IS the default for the whole face — that is what makes an
            // omitted param a strict no-op.
            Assert.Equal(0.5f, m.Default);
            // A face knob is a continuous blend, never a picker or a stepped ordinal.
            Assert.Null(n.ValueLabels);
            Assert.Equal(0f, n.Step);
        }
    }

    /// <summary>
    /// NEUTRAL IS A STRICT NO-OP. Setting every face knob to its documented
    /// default must produce output BIT-IDENTICAL to never touching them, so every
    /// preset and user tone authored before the face existed sounds exactly as it
    /// did. (The native side proves the stronger claim — the post-capture face is
    /// a bit-exact identity over 2.05 M samples — with
    /// <c>tone_response.exe face neutral</c>; this is the end-to-end half.)
    /// The third leg proves the test is not vacuous: a moved knob must change the
    /// output.
    /// </summary>
    [Fact]
    public void NeutralFace_IsABitIdenticalNoOp_AndAMovedKnobIsNot()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable");
        Assert.SkipWhen(!env!.HasNamAmp, "native catalogue has no 'namamp'");
        var capture = FirstBundledCapture();
        Assert.SkipWhen(capture is null, "no bundled .nam capture found");

        env.Warm();
        var input = Sine(env.ActualRate, 220.0, 0.15f, env.ActualRate / 2);

        var untouched = env.RunLeg(capture!, input, _ => { });
        Assert.SkipWhen(untouched is null, "capture did not load on the real engine");

        var explicitDefaults = env.RunLeg(capture!, input, node =>
        {
            foreach (var id in new[] { "gain", "bass", "mid", "treble", "presence", "resonance", "master" })
                Assert.True(env.Chain.SetParam(node, id, 0.5f), $"namamp rejected '{id}'");
        });
        Assert.NotNull(explicitDefaults);
        Assert.Equal(untouched!.Length, explicitDefaults!.Length);
        for (var i = 0; i < untouched.Length; i++)
        {
            Assert.True(untouched[i] == explicitDefaults[i],
                $"the neutral face changed sample {i}: {untouched[i]:R} vs {explicitDefaults[i]:R}");
        }

        var bassUp = env.RunLeg(capture!, input, node => env.Chain.SetParam(node, "bass", 1.0f));
        Assert.NotNull(bassUp);
        Assert.True(Rms(Sub(bassUp!, untouched)) > 0.02f * Rms(untouched),
            "bass at maximum produced (near-)identical output — the face is not reaching the signal");
    }

    /// <summary>
    /// THE HEADLINE REGRESSION. <c>namamp</c> used to DRY-BYPASS at any engine
    /// rate other than the capture's own 48 kHz, so a device pinned to 44.1 kHz
    /// heard clean DI instead of the amp. Since 0.36 the module resamples around
    /// the capture. Revert the resampling and this test goes RED: the old path
    /// returns the input untouched, so <c>out - in</c> is exactly zero.
    /// </summary>
    [Fact]
    public void At44100_TheCaptureIsHeard_NotDryBypassed()
    {
        using var env = NativeChainEnv.TryCreate(44_100);
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable");
        Assert.SkipWhen(!env!.HasNamAmp, "native catalogue has no 'namamp'");
        var capture = FirstBundledCapture();
        Assert.SkipWhen(capture is null, "no bundled .nam capture found");
        Assert.SkipWhen(env.ActualRate == NamCaptureRate,
            $"this host's audio device would not open at 44.1 kHz (opened at {env.ActualRate} Hz), " +
            "so there is no off-capture-rate path to exercise here");

        env.Warm();
        var input = Sine(env.ActualRate, 220.0, 0.15f, env.ActualRate / 2);
        var node = env.Chain.AddNode(Namamp);
        Assert.NotNull(node);
        try
        {
            Assert.True(env.Chain.SetNodeString(node!.NodeId, "modelPath", capture!));
            Assert.SkipWhen(!env.WaitLoaded(node.NodeId), "capture did not load on the real engine");

            Assert.Equal(1f, env.Chain.GetParam(node.NodeId, "resampling"));
            Assert.Equal(0f, env.Chain.GetParam(node.NodeId, "rateMismatch"));

            var output = env.Process(input);
            Assert.All(output, s => Assert.True(float.IsFinite(s), "namamp emitted a non-finite sample"));
            Assert.True(Rms(output) > 0f, "namamp emitted silence at 44.1 kHz");
            Assert.True(Rms(Sub(output, input)) > 0.10f * Rms(input),
                "the 44.1 kHz output is (near-)identical to the input — the capture is being dry-bypassed");
        }
        finally
        {
            env.Chain.RemoveNode(node!.NodeId);
        }
    }

    /// <summary>
    /// NAM **A2** support in the runtime we actually ship. The fixture is a
    /// SYNTHETIC A2 capture generated here, never a bundled model file: it carries
    /// the exact geometry NeuralAudio's A2 fast path checks for (version &gt; 0.5.4,
    /// architecture WaveNet, ONE layer array, the 23-entry A2 kernel-size and
    /// dilation sequences, channels 3, a 16-tap biased head) and exactly the 1871
    /// weights that geometry consumes. The weight COUNT is validated by
    /// <c>WaveNetModelT::SetWeights</c> ("Wrong number of weights. Expected N but
    /// got M" throws, which the module swallows into loaded == 0), so a load here
    /// can only mean the shipped DLL constructed a real A2 WaveNet. The weight
    /// VALUES are musical nonsense on purpose — this tests the loader, not a tone.
    /// </summary>
    [Fact]
    public void AnA2GeometryCapture_LoadsAndRunsOnTheShippedRuntime()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable");
        Assert.SkipWhen(!env!.HasNamAmp, "native catalogue has no 'namamp'");

        var dir = Path.Combine(Path.GetTempPath(), "dotcharter-a2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // The EXTENSION is load-bearing — NeuralAudio dispatches its loader on it
        // (NamCaptureIndex.ExtensionFor exists for exactly this reason).
        var path = Path.Combine(dir, "synthetic-a2.nam");
        try
        {
            File.WriteAllText(path, BuildA2Nam(), new UTF8Encoding(false));

            env.Warm();
            var node = env.Chain.AddNode(Namamp);
            Assert.NotNull(node);
            try
            {
                Assert.True(env.Chain.SetNodeString(node!.NodeId, "modelPath", path));
                Assert.True(env.WaitLoaded(node.NodeId),
                    "an A2-geometry .nam did NOT load — the shipped NeuralAudio cannot construct A2 models");
                Assert.Equal(0f, env.Chain.GetParam(node.NodeId, "rateMismatch"));

                var output = env.Process(Sine(env.ActualRate, 220.0, 0.1f, 9_600));
                Assert.All(output, s => Assert.True(float.IsFinite(s), "the A2 model emitted a non-finite sample"));
            }
            finally
            {
                env.Chain.RemoveNode(node!.NodeId);
            }
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    // ----- the synthetic A2 fixture ------------------------------------------

    private static readonly int[] A2KernelSizes =
        [6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 15, 15, 6, 6, 6, 6, 6, 6, 6];

    private static readonly int[] A2Dilations =
        [1, 3, 7, 17, 41, 101, 239, 1, 3, 7, 17, 41, 101, 239, 1, 13, 1, 3, 7, 17, 41, 101, 239];

    /// <summary>Exact weight count of
    /// <c>WaveNetModelT&lt;WaveNetLayerArrayT&lt;1,1,1,16,1,3,A2KernelSizes,A2Dilations,true,LeakyReLU&gt;&gt;</c>
    /// — the internal static A2 kernel NeuralAudio dispatches a standard A2
    /// capture to. Measured by instantiating that exact template and calling
    /// <c>GetNumWeights()</c>; its receptive field is 6346 samples.</summary>
    private const int A2StandardWeightCount = 1871;

    private static string BuildA2Nam()
    {
        var inv = CultureInfo.InvariantCulture;
        // Small, deterministic, non-zero weights: big enough that the model is not
        // a silent identity, small enough that 23 layers cannot blow up.
        var weights = new StringBuilder(A2StandardWeightCount * 12);
        var rng = new Random(20260726);
        for (var i = 0; i < A2StandardWeightCount; i++)
        {
            if (i > 0) weights.Append(',');
            weights.Append((rng.NextDouble() * 0.04 - 0.02).ToString("G9", inv));
        }

        // Every key below is checked by NeuralModel.cpp's NAMIsA2Standard. Note
        // IsActive() treats an ABSENT key as ACTIVE, so the optional blocks have
        // to be present-and-inactive rather than omitted.
        var inactive = "{\"active\":false}";
        return "{" +
            "\"version\":\"0.6.0\"," +
            "\"architecture\":\"WaveNet\"," +
            "\"sample_rate\":48000," +
            "\"metadata\":{\"loudness\":-18.0,\"input_level_dbu\":12.0,\"output_level_dbu\":12.0}," +
            "\"config\":{" +
                "\"in_channels\":1," +
                "\"head_scale\":1.0," +
                "\"layers\":[{" +
                    "\"input_size\":1,\"condition_size\":1,\"channels\":3,\"bottleneck\":3," +
                    "\"kernel_sizes\":[" + string.Join(",", A2KernelSizes) + "]," +
                    "\"dilations\":[" + string.Join(",", A2Dilations) + "]," +
                    "\"activation\":[{\"type\":\"LeakyReLU\",\"negative_slope\":0.01}]," +
                    "\"head\":{\"out_channels\":1,\"kernel_size\":16,\"head_dilation\":1,\"bias\":true}," +
                    "\"layer1x1\":{\"active\":true,\"groups\":1}," +
                    "\"head1x1\":" + inactive + "," +
                    "\"conv_pre_film\":" + inactive + ",\"conv_post_film\":" + inactive + "," +
                    "\"input_mixin_pre_film\":" + inactive + ",\"input_mixin_post_film\":" + inactive + "," +
                    "\"activation_pre_film\":" + inactive + ",\"activation_post_film\":" + inactive + "," +
                    "\"layer1x1_post_film\":" + inactive + ",\"head1x1_post_film\":" + inactive + "," +
                    "\"groups_input\":1,\"groups_input_mixin\":1" +
                "}]" +
            "}," +
            "\"weights\":[" + weights + "]" +
        "}";
    }

    // ----- helpers ------------------------------------------------------------

    private static float[] Sine(int sampleRate, double hz, float amp, int frames)
    {
        var x = new float[frames];
        for (var i = 0; i < frames; i++)
            x[i] = (float)(amp * Math.Sin(2.0 * Math.PI * hz * i / sampleRate));
        return x;
    }

    private static float[] Sub(float[] a, float[] b)
    {
        var d = new float[Math.Min(a.Length, b.Length)];
        for (var i = 0; i < d.Length; i++) d[i] = a[i] - b[i];
        return d;
    }

    /// <summary>
    ///     Every bundled capture actually loads in the real neural runtime, and
    ///     actually makes sound.
    ///     <para>
    ///         This is the one failure mode the managed gates structurally cannot
    ///         see. <c>CaptureBackedPresetTests</c> proves a preset's
    ///         <c>modelPath</c> names a file that exists and is in the manifest;
    ///         <c>BundledNamCaptureAttributionTests</c> proves that file has
    ///         provenance. Neither opens it. A truncated download, an HTML error
    ///         page saved with a <c>.nam</c> extension, or an architecture this
    ///         build of NeuralAudio does not support all pass both — and then
    ///         produce a MODELLESS namamp at runtime, which is silence, logged
    ///         nowhere, on whichever preset happened to name it.
    ///     </para>
    ///     <para>
    ///         Since 0.37 the whole capture-backed half of the factory catalogue
    ///         resolves into this folder, so "one file is bad" now means "one
    ///         tone is silent" rather than "one example is missing".
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryBundledCapture_LoadsInTheNativeEngine_AndPassesAudio()
    {
        using var env = NativeChainEnv.TryCreate();
        Assert.SkipWhen(env is null, "native DSP ABI (juce_sharp.dll) / audio engine not loadable");
        Assert.SkipWhen(!env!.HasNamAmp, "native catalogue has no 'namamp' (built without DOTCHARTER_DSP_NEURAL)");

        var captures = BundledCaptures();
        Assert.SkipWhen(captures.Count == 0, "bundled capture folder not found (running outside a source tree)");
        env.Warm();

        // A quiet-ish sine: loud enough that a working amp produces obvious
        // output, not so loud that the comparison depends on the capture's gain.
        var input = new float[2048];
        for (var i = 0; i < input.Length; i++)
            input[i] = 0.1f * MathF.Sin(2f * MathF.PI * 220f * i / NamCaptureRate);

        var failures = new List<string>();
        foreach (var path in captures)
        {
            var name = Path.GetFileName(path);
            var node = env.Chain.AddNode(Namamp);
            if (node is null)
            {
                failures.Add($"{name}: could not create a namamp node");
                continue;
            }

            try
            {
                if (!env.Chain.SetNodeString(node.NodeId, "modelPath", path))
                {
                    failures.Add($"{name}: the native module REJECTED the path outright");
                    continue;
                }

                if (!env.WaitLoaded(node.NodeId))
                {
                    failures.Add($"{name}: never reported loaded within {LoadTimeout.TotalSeconds:0}s " +
                                 "(unreadable, truncated, or an unsupported architecture)");
                    continue;
                }

                // Loaded is not the same as working: assert it renders something.
                // A model that loads and outputs silence is the same user-visible
                // bug as one that never loads.
                var output = env.Process(input);
                var rms = Rms(output);
                if (!(rms > 1e-5f) || float.IsNaN(rms) || float.IsInfinity(rms))
                    failures.Add($"{name}: loaded but rendered {rms} RMS — a silent or non-finite amp");
            }
            finally
            {
                env.Chain.RemoveNode(node.NodeId);
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {captures.Count} bundled capture(s) do not work:\n  " +
            string.Join("\n  ", failures));
    }

    private static float Rms(float[] x)
    {
        if (x.Length == 0) return 0f;
        double sum = 0;
        foreach (var v in x) sum += (double)v * v;
        return (float)Math.Sqrt(sum / x.Length);
    }

    private static string? FirstBundledCapture()
        => BundledCaptures().FirstOrDefault();

    private static IReadOnlyList<string> BundledCaptures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        if (dir == null) return [];
        var bundle = UiSourceTree.Resolve(Path.Combine(dir.FullName, "DotCharter.UI", "NamCaptures"));
        if (!Directory.Exists(bundle)) return [];
        return UiSourceTree.EnumerateFiles(bundle, "*.nam")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A device-less engine + attached chain driven purely offline.</summary>
    private sealed class NativeChainEnv : IDisposable
    {
        public required JuceAudioEngine Engine { get; init; }
        public required JuceDspChainService Chain { get; init; }
        private int _rate;
        private IDisposable? _guard;

        public bool HasNamAmp => Chain.Catalog.Any(m => m.Id == Namamp);

        /// <summary>
        /// An engine at a KNOWN sample rate, on the NULL device in MANUAL-pump
        /// mode. Three reasons it has to be the null device:
        /// <list type="number">
        /// <item>The rate must come from the engine config, not the offline call:
        /// opening the device prepares the DSP rig, and
        /// <c>AudioEngine::dspProcessOffline</c> only prepares when the rig is
        /// <i>not already</i> prepared — an offline call cannot re-rate a rig the
        /// device already prepared.</item>
        /// <item>A real device negotiates. Asking a shared-mode WASAPI endpoint
        /// for 44.1 kHz commonly opens 48 kHz anyway, which silently turns the
        /// off-capture-rate test into a no-op. The null device accepts the exact
        /// config it is handed.</item>
        /// <item>MANUAL mode (<c>DCA_NULL_DEVICE_MANUAL</c>) skips the pacing
        /// thread, so <c>audioDeviceAboutToStart</c> still prepares the rig but no
        /// callback ever fires — nothing races the offline renders and the
        /// bit-identity comparison is exact.</item>
        /// </list>
        /// Both guards are process-wide env vars; registration only APPENDS the
        /// type (never the default pick), so a concurrent test creating an
        /// ordinary engine is unaffected.
        /// </summary>
        public static NativeChainEnv? TryCreate(int sampleRate = 48_000)
        {
            var guard = new NullDeviceScope();
            JuceAudioEngine? engine = null;
            try
            {
                engine = new JuceAudioEngine(new JuceEngineConfig
                {
                    OutputTypeName = "Null",
                    InputTypeName = "Null",
                    SampleRate = sampleRate,
                    BufferFrames = 1024,   // >= the 512-frame offline chunk
                    EnableConsoleLog = false,
                });
                engine.Stop(); // transport off — this rig is driven offline only
                var chain = new JuceDspChainService();
                chain.LoadCatalog();
                chain.AttachEngine(engine);
                var actual = 0;
                try { actual = engine.SampleRate; } catch { /* ignored */ }
                return new NativeChainEnv
                {
                    Engine = engine,
                    Chain = chain,
                    _rate = actual > 0 ? actual : sampleRate,
                    _guard = guard,
                };
            }
            catch
            {
                engine?.Dispose();
                guard.Dispose();
                return null;
            }
        }

        /// <summary>Sets both null-device guards for the engine's lifetime. They
        /// are read natively at engine start (via GetEnvironmentVariable, not the
        /// CRT's cached getenv), so they must be live before construction.</summary>
        private sealed class NullDeviceScope : IDisposable
        {
            public NullDeviceScope()
            {
                Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
                Environment.SetEnvironmentVariable("DCA_NULL_DEVICE_MANUAL", "1");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", null);
                Environment.SetEnvironmentVariable("DCA_NULL_DEVICE_MANUAL", null);
            }
        }

        public int ActualRate => _rate;

        /// <summary>Makes sure the rig is prepared (a no-op when the device
        /// already did it) and warms the offline path.</summary>
        public void Warm()
        {
            var l = new float[512];
            var r = new float[512];
            Chain.ProcessOfflineBuffer(l, r, l.Length, _rate);
        }

        public bool WaitLoaded(int nodeId)
        {
            var deadline = DateTime.UtcNow + LoadTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Chain.GetParam(nodeId, "loaded") is 1f) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        public float[] Process(float[] input)
        {
            var l = (float[])input.Clone();
            var r = (float[])input.Clone();
            Assert.True(Chain.ProcessOfflineBuffer(l, r, l.Length, _rate));
            return l;
        }

        /// <summary>One cold namamp: add a fresh node (so the model reloads and
        /// prewarms from the same deterministic state), apply the leg's params,
        /// render, tear it down.</summary>
        public float[]? RunLeg(string capturePath, float[] input, Action<int> configure)
        {
            var node = Chain.AddNode(Namamp);
            if (node is null) return null;
            try
            {
                if (!Chain.SetNodeString(node.NodeId, "modelPath", capturePath)) return null;
                if (!WaitLoaded(node.NodeId)) return null;
                configure(node.NodeId);
                return Process(input);
            }
            finally
            {
                Chain.RemoveNode(node.NodeId);
            }
        }

        public void Dispose()
        {
            try { Chain.AttachEngine(nint.Zero); } catch { /* ignored */ }
            try { Engine.Dispose(); } catch { /* ignored */ }
            try { _guard?.Dispose(); } catch { /* ignored */ }
        }
    }
}
