using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion.Fidelity;

/// <summary>One converted tone, rendered and measured.</summary>
public sealed record ToneRender(
    string Name,
    DcRigPreset Preset,
    float EstimatedPeakDbfs,
    ToneMeasurement Measurement,
    DriveMeasurement Drive,
    float[] Output);

/// <summary>
///     Renders a converted <see cref="DcRigPreset" /> through the REAL native rig
///     offline (<c>dca_engine_dsp_process_buffer</c>, the code the input callback
///     runs) and measures it with <see cref="ToneMetrics" />. The preset goes on
///     through <see cref="DcRigApplier" />, the product's own apply path, so a
///     render hears exactly what a song's tone switch would.
///     <para>
///         One device-less engine per harness. Not thread-safe; callers live in
///         <c>[Collection("NativeEngine")]</c>.
///     </para>
/// </summary>
public sealed class RsToneFidelityHarness : IDisposable
{
    private const int Block = 512;

    /// <summary>Silence pushed through after an apply, before the DI: the applier
    /// un-bypasses new nodes through the chain's fade.</summary>
    private const int PreRollSamples = ReferenceDi.SampleRate;

    /// <summary>
    ///     How long the harness will drain the previous render's tail before giving
    ///     up. A REMOVED node still rings out (the rig keeps a removed reverb's or
    ///     delay's tail, like a tone switch in Rocksmith), and a fixed one-second
    ///     pre-roll left a 7 s hall from the previous tone in the next render —
    ///     measured: a dry tone read -27.9 dBFS RMS after a reverb tone and -39.3
    ///     after a dry one. So the harness drains to silence instead.
    /// </summary>
    private const int MaxDrainSamples = 30 * ReferenceDi.SampleRate;

    private readonly JuceAudioEngine? _engine;
    private readonly JuceDspChainService? _chain;
    private readonly DcRigApplier? _applier;

    public RsToneFidelityHarness()
    {
        try
        {
            _engine = new JuceAudioEngine(new JuceEngineConfig { EnableConsoleLog = false });
            _engine.Stop(); // the offline path must be the only processor
            _chain = new JuceDspChainService();
            _chain.LoadCatalog();
            _chain.AttachEngine(_engine);
            _applier = new DcRigApplier(_chain);
        }
        catch
        {
            _engine?.Dispose();
            _engine = null;
            _chain = null;
            _applier = null;
        }
    }

    /// <summary>False when the native DLL / engine cannot load here.</summary>
    public bool Available => _chain is not null;

    public ToneRender Render(string name, DcRigPreset preset)
    {
        var chain = _chain ?? throw new InvalidOperationException("native rig not available");
        foreach (var n in chain.GetNodes().ToList()) chain.RemoveNode(n.NodeId);
        DrainToSilence(chain);

        var result = _applier!.Apply(preset);
        if (result.FailedOps > 0)
            throw new InvalidOperationException($"{name}: {result.FailedOps} native op(s) failed applying the preset");

        Process(chain, new float[PreRollSamples]);
        var output = Process(chain, ReferenceDi.Samples);

        DrainToSilence(chain);
        var probe = ReferenceDi.Sine(220);
        var probeOut = Process(chain, probe);

        return new ToneRender(
            name,
            preset,
            DcRigLoudnessNormalizer.EstimateChainOutputDb(preset.Modules),
            ToneMetrics.Measure(output, ReferenceDi.SampleRate, ReferenceDi.PlayEndSample),
            ToneMetrics.Drive(probeOut, ReferenceDi.SampleRate, 220, ReferenceDi.PeakLinear),
            output);
    }

    /// <summary>
    ///     The cheap half of <see cref="Render" />: the rendered PEAK (dBFS) over the
    ///     playing part of <see cref="ReferenceDi" /> only — no tail, no sine probe.
    ///     What the amp-voicing trim is measured (and pinned) with.
    /// </summary>
    public double RenderPlayingPeakDbfs(DcRigPreset preset)
    {
        var chain = _chain ?? throw new InvalidOperationException("native rig not available");
        foreach (var n in chain.GetNodes().ToList()) chain.RemoveNode(n.NodeId);
        DrainToSilence(chain);
        if (_applier!.Apply(preset).FailedOps > 0)
            throw new InvalidOperationException("native op(s) failed applying the preset");

        Process(chain, new float[PreRollSamples]);
        var output = Process(chain, ReferenceDi.Samples.AsSpan(0, ReferenceDi.PlayEndSample).ToArray());
        var peak = 0f;
        foreach (var v in output) peak = MathF.Max(peak, MathF.Abs(v));
        return 20 * Math.Log10(peak + 1e-20);
    }

    /// <summary>
    ///     Applies <paramref name="preset" /> and renders <paramref name="input" /> (mono)
    ///     through it after the pre-roll: no measurement, no probe. What the idle-noise
    ///     targets (#155) feed a noise floor through instead of the DI.
    /// </summary>
    public float[] RenderInput(DcRigPreset preset, float[] input)
    {
        var chain = _chain ?? throw new InvalidOperationException("native rig not available");
        foreach (var n in chain.GetNodes().ToList()) chain.RemoveNode(n.NodeId);
        DrainToSilence(chain);
        if (_applier!.Apply(preset).FailedOps > 0)
            throw new InvalidOperationException("native op(s) failed applying the preset");

        Process(chain, new float[PreRollSamples]);
        return Process(chain, input);
    }

    /// <summary>Feeds silence until a whole 100 ms of output stays under -120 dBFS
    /// (or <see cref="MaxDrainSamples" /> pass — a tone that never falls silent
    /// then gets measured as it is, which the tail metric will show).</summary>
    private static void DrainToSilence(JuceDspChainService chain)
    {
        var silent = new float[Block];
        var quietRun = 0;
        for (var done = 0; done < MaxDrainSamples && quietRun < ReferenceDi.SampleRate / 10; done += Block)
        {
            Array.Clear(silent);
            var r = new float[Block];
            if (!chain.ProcessOfflineBuffer(silent, r, Block, ReferenceDi.SampleRate))
                throw new InvalidOperationException("dca_engine_dsp_process_buffer failed");
            var peak = 0f;
            for (var i = 0; i < Block; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(silent[i]), MathF.Abs(r[i])));
            quietRun = peak < 1e-6f ? quietRun + Block : 0;
        }
    }

    /// <summary>Runs mono <paramref name="input" /> through the chain, returns the
    /// left output (the rig is dual-mono until a stereo module; left is the
    /// channel every module writes).</summary>
    private static float[] Process(JuceDspChainService chain, float[] input)
    {
        var output = new float[input.Length];
        var l = new float[Block];
        var r = new float[Block];
        for (var off = 0; off < input.Length; off += Block)
        {
            var n = Math.Min(Block, input.Length - off);
            Array.Copy(input, off, l, 0, n);
            Array.Copy(input, off, r, 0, n);
            if (!chain.ProcessOfflineBuffer(l, r, n, ReferenceDi.SampleRate))
                throw new InvalidOperationException("dca_engine_dsp_process_buffer failed");
            Array.Copy(l, 0, output, off, n);
        }

        return output;
    }

    public void Dispose()
    {
        try { _engine?.Dispose(); }
        catch { /* teardown only */ }
    }
}
