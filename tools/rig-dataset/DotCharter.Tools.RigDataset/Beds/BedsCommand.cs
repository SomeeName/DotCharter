using System.Diagnostics;
using JuceSharp.AudioIo;
using JuceSharp.Dsp;

namespace DotCharter.Tools.RigDataset.Beds;

/// <summary>Everything <c>beds</c> takes.</summary>
public sealed class BedsOptions
{
    /// <summary>Where the WAVs and sidecars go. Normally
    /// <c>&lt;dataset root&gt;/beds</c>.</summary>
    public required string OutDir { get; init; }

    /// <summary>How many beds.</summary>
    public int Count { get; init; } = BedPlanner.DefaultCount;

    /// <summary>The run seed. A bed's own seed is derived from it and its index, so
    /// the same pair always produces the same bed.</summary>
    public int Seed { get; init; } = 20260913;

    /// <summary>48 000.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Re-render beds that already have a WAV and a sidecar.</summary>
    public bool Force { get; init; }

    /// <summary>Plan and write sidecars, render no audio. The fast way to eyeball the
    /// musical priors.</summary>
    public bool DryRun { get; init; }

    /// <inheritdoc cref="RenderOptions.RequireNullDevice" />
    public bool RequireNullDevice { get; init; }
}

/// <summary>
///     <c>beds</c> — generates the backing beds stage A (bleed) and stage B
///     (separation) both need, from nothing but the app's own DSP.
///     <para>
///         <b>The beds folder was empty, and that is why v1 has <c>0 separated</c>
///         rows.</b> Without a bed there is nothing to mix a clip INTO, and a
///         separator handed a bare guitar mostly hands it back — so the augmentation
///         that produces the distribution inference actually sees could not run at
///         all. Public multitracks would have filled the folder in an afternoon and
///         are all non-commercial (MUSDB18-HQ, MoisesDB), which is fine for a
///         notebook and fatal for a model that ships. Generating them is the only
///         answer that is both legal and unlimited.
///     </para>
///     <para>
///         Each bed is three layers — drums, bass, keys — synthesised by
///         <see cref="BedSynth" /> and then RENDERED THROUGH THE ENGINE, one rig per
///         layer, so the bed carries the same converter, the same non-linearities and
///         the same reverb tails the clips do. It is written mono 16-bit because that
///         is what <see cref="ClipSource" /> reads and what the corpus stores.
///     </para>
/// </summary>
public sealed class BedsCommand
{
    private readonly BedsOptions _o;
    private readonly IParamRanges _ranges;

    /// <summary>Creates the command.</summary>
    /// <param name="options">Parsed flags.</param>
    /// <param name="ranges">The param catalogue the layer rigs are built against.
    /// Defaults to the real one out of the native DLL.</param>
    public BedsCommand(BedsOptions options, IParamRanges? ranges = null)
    {
        _o = options;
        _ranges = ranges ?? EngineParamRanges.Instance;
    }

    /// <summary>Generates the beds. Resumable: a bed whose WAV and sidecar both exist
    /// is skipped unless <see cref="BedsOptions.Force" /> is set.</summary>
    /// <param name="ct">Ctrl-C.</param>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_o.OutDir);
        var plans = Enumerable.Range(0, _o.Count)
            .Select(i => BedPlanner.Plan(_o.Seed, i, _ranges, _o.SampleRate))
            .ToArray();

        var todo = plans.Where(p => _o.Force || !Exists(p)).ToArray();
        Console.WriteLine($"Beds: {plans.Length} planned, {plans.Length - todo.Length} already on disk, "
                          + $"{todo.Length} to render into {_o.OutDir}.");
        if (todo.Length == 0) return 0;

        if (_o.DryRun)
        {
            foreach (var plan in todo) WriteSidecar(plan, [], DateTimeOffset.UtcNow);
            Console.WriteLine("Dry run: sidecars written, no audio.");
            return 0;
        }

        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        using var engine = RenderCommand.OpenDevicelessEngine(_o.SampleRate, _o.RequireNullDevice);
        engine.Stop(); // OFFLINE MEANS OFFLINE
        var chain = engine.GetDspChain(0);

        var sw = Stopwatch.StartNew();
        var done = 0;
        foreach (var plan in todo)
        {
            if (ct.IsCancellationRequested) break;
            var audio = RenderBed(plan, chain);
            var sidecar = WriteSidecar(plan, audio, DateTimeOffset.UtcNow);

            var wav = Path.Combine(_o.OutDir, plan.WavName);
            var tmp = wav + ".tmp";
            using (var w = new WavWriter(tmp, _o.SampleRate, 1, WavSampleFormat.Pcm16)) w.Write(audio);
            File.Move(tmp, wav, overwrite: true);

            done++;
            Console.WriteLine($"[{done}/{todo.Length}] {sidecar.Describe()}");
            await Task.Yield();
        }

        Console.WriteLine($"Beds: {done} rendered in {sw.Elapsed:mm\\:ss}.");
        return 0;
    }

    private bool Exists(BedPlan p)
        => File.Exists(Path.Combine(_o.OutDir, p.WavName))
           && File.Exists(Path.Combine(_o.OutDir, p.JsonName));

    private BedSidecar WriteSidecar(BedPlan plan, float[] audio, DateTimeOffset now)
    {
        var sidecar = BedSidecar.For(plan, audio, now);
        var path = Path.Combine(_o.OutDir, plan.JsonName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sidecar.ToJson());
        File.Move(tmp, path, overwrite: true);
        return sidecar;
    }

    /// <summary>Synthesises every layer, runs each through its own rig, and sums them
    /// at their planned levels. The sum is normalised to −18 dBFS and peak-trimmed,
    /// so a bed arrives at the mixer at the same reference level a clip does and the
    /// guitar-to-mix draw means what it says.</summary>
    private float[] RenderBed(BedPlan plan, JuceDspChain chain)
    {
        var frames = (int)Math.Round(plan.Seconds * plan.SampleRate);
        var mix = new float[frames];

        for (var ordinal = 0; ordinal < plan.Layers.Count; ordinal++)
        {
            var layer = plan.Layers[ordinal];
            var dry = BedSynth.RenderLayer(plan, ordinal);
            var wet = RenderThroughRig(chain, layer, dry, frames, plan.SampleRate);
            var gain = Augmentation.FromDb(layer.LevelDb);
            for (var i = 0; i < frames; i++) mix[i] += wet[i] * gain;
        }

        Augmentation.Normalise(mix, new Random(plan.Seed ^ 0x0BED), Augmentation.TargetRmsDbfs, 0f);
        Augmentation.TrimPeak(mix);
        return mix;
    }

    private static float[] RenderThroughRig(JuceDspChain chain, BedLayerPlan layer, float[] dryMono,
        int frames, int sampleRate)
    {
        chain.Clear();
        foreach (var mod in layer.Rig)
        {
            var node = chain.AddNode(mod.ModuleTypeId);
            foreach (var (paramId, value) in mod.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
                node.SetParam(paramId, value);
        }

        // Same stereo-in / mono-out shape as the clip render, for the same reason:
        // the `channels: 1` overload folds the rig's OUTPUT down and allocates, where
        // the stereo overload works in place.
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = i < dryMono.Length ? dryMono[i] : 0f;
            interleaved[2 * i] = v;
            interleaved[2 * i + 1] = v;
        }

        var outBuf = new float[interleaved.Length];
        chain.ProcessOffline(interleaved, channels: 2, sampleRate, outBuf);

        var mono = new float[frames];
        for (var i = 0; i < frames; i++) mono[i] = 0.5f * (outBuf[2 * i] + outBuf[2 * i + 1]);
        return mono;
    }
}
