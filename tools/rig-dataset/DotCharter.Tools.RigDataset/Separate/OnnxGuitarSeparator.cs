using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tools.RigDataset.Separate;

/// <summary>
///     The app's OWN <c>htdemucs_6s</c> ONNX path, wrapped as an
///     <see cref="IGuitarSeparator" />. The label never changes; the audio gains the
///     separator's artefacts, which is the distribution the model meets at inference
///     and nowhere else in this pipeline.
///     <para>
///         <b>The GPU is chosen by NAME and handed to DirectML as its DXGI device id.</b>
///         The app's ONNX Runtime is the DirectML build (no CUDA), so
///         <c>CUDA_VISIBLE_DEVICES</c> pins nothing any more. <see cref="GpuSelector" />
///         still picks the card by its <c>nvidia-smi</c> name (refusing the owner's
///         gaming card), and <see cref="GpuSelector.DirectMlDeviceFor" /> maps that name
///         onto the DXGI adapter list — whose index 0 is the DISPLAY adapter, i.e.
///         possibly the very gaming card this type exists to protect. An ambiguous or
///         missing match refuses rather than guessing.
///     </para>
///     <para>
///         Serialised behind a semaphore of one: the chunker's scratch is not written
///         for two concurrent separations, and a second session on one GPU is
///         contention rather than throughput.
///     </para>
/// </summary>
internal sealed class OnnxGuitarSeparator : IGuitarSeparator, IDisposable
{
    private readonly OnnxStemSeparator _separator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnnxGuitarSeparator(OnnxStemSeparator separator) => _separator = separator;

    public string Backend => _separator.BackendDescription;

    /// <summary>Builds the separator for the stage-B pass on the card chosen by name.
    /// Returns null (with a logged reason) when there is no model, no usable GPU and
    /// no <c>--cpu</c>, or the model will not open.</summary>
    public static IGuitarSeparator? TryCreate(SeparateOptions o, Action<string> log)
    {
        if (o.ForceCpu)
        {
            log("Stem separation: --cpu — no GPU will be used (~4 s per 5 s clip per process).");
            return Open(o.StemModelPath, o.IntraOpThreads, deviceId: null, log);
        }

        GpuChoice choice;
        try
        {
            choice = GpuSelector.Select(GpuSelector.Probe(log), o.GpuName, o.AllowGamingGpu);
        }
        catch (GpuSelectionException ex)
        {
            log($"Stem separation: {ex.Message}");
            return null;
        }

        var deviceId = ResolveDirectMlDevice(choice.Device.Name, log);
        if (deviceId == null) return null;
        log($"Stem separation: {choice.Reason} DirectML device {deviceId}.");
        return Open(o.StemModelPath, o.IntraOpThreads, deviceId, log);
    }

    /// <summary>The render command's older entry point: the same separator, on the card
    /// at the raw <c>--cuda-device</c> nvidia-smi index the render has always taken
    /// (mapped to DirectML by name), or on DXGI adapter 0 when none is given.</summary>
    public static IGuitarSeparator? TryCreate(RenderOptions o, Action<string> log)
    {
        if (o.SeparateFraction <= 0) return null;
        var deviceId = 0;
        if (o.CudaDevice >= 0)
        {
            var card = GpuSelector.Probe(log).FirstOrDefault(d => d.Index == o.CudaDevice);
            if (card == null)
            {
                log($"Stem separation: nvidia-smi reports no GPU {o.CudaDevice} — refusing.");
                return null;
            }

            if (ResolveDirectMlDevice(card.Name, log) is not { } mapped) return null;
            deviceId = mapped;
            log($"Stem separation: --cuda-device {o.CudaDevice} is '{card.Name}', DirectML device {deviceId} "
                + "(the `separate` subcommand picks by NAME instead — prefer it).");
        }

        return Open(o.StemModelPath, 2, deviceId, log);
    }

    private static int? ResolveDirectMlDevice(string cardName, Action<string> log)
    {
        var adapters = OnnxGpuAdapters.Enumerate().Select(a => (a.DeviceId, a.Name)).ToList();
        var deviceId = GpuSelector.DirectMlDeviceFor(cardName, adapters);
        if (deviceId == null)
        {
            log($"Stem separation: '{cardName}' is not exactly one DXGI adapter "
                + $"({string.Join(", ", adapters.Select(a => $"{a.DeviceId}:{a.Name}"))}) — refusing rather than "
                + "guessing, because DXGI adapter 0 is the display (gaming) card.");
        }

        return deviceId;
    }

    private static IGuitarSeparator? Open(string? path, int intraOpThreads, int? deviceId, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            log($"Stem separation: no model at '{path ?? "<unset>"}' — pass --stem-model.");
            return null;
        }

        try
        {
            var forceCpu = deviceId == null;
            var provider = forceCpu ? OnnxExecutionProvider.Cpu : OnnxExecutionProvider.DirectML;
            var sep = new OnnxStemSeparator(path, OnnxStemModelKind.Htdemucs, provider, log,
                intraOpNumThreads: intraOpThreads, deviceId: deviceId ?? 0);
            log($"Stem separation: {sep.BackendDescription}.");

            // A SILENT FALL TO CPU IS THE FAILURE THIS CHECK EXISTS FOR.
            // `OnnxStemSeparator` degrades a GPU request to the CPU on its own and
            // returns a working session either way, so a GPU that cannot load the
            // model does not throw — it makes the pass fifteen times slower and says
            // so only in one log line nobody is reading at 05:10. Measured on this
            // box: 6.7 clips/min on CPU, which is ~100 hours for 40,000 clips.
            // Refuse, and let the operator decide.
            if (!forceCpu && sep.ActiveExecutionProvider != OnnxExecutionProvider.DirectML)
            {
                log($"Stem separation: asked for DirectML and got {sep.ActiveExecutionProvider} "
                    + $"({sep.FallbackReason}) — refusing. Pass --cpu if you really mean to spend "
                    + "~100 CPU-hours on 40,000 clips.");
                sep.Dispose();
                return null;
            }

            return new OnnxGuitarSeparator(sep);
        }
        catch (Exception ex)
        {
            log($"Stem separation: could not open the model ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>Mono in, mono guitar stem out, or null when the model produced no
    /// guitar source (a 4-stem export does not have one).</summary>
    public async Task<float[]?> IsolateGuitarAsync(float[] mono, int sampleRate, CancellationToken ct)
    {
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[2 * i + 1] = mono[i];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IReadOnlyList<OnnxStemBuffer> stems;
        try
        {
            stems = await _separator.SeparateAsync(stereo, sampleRate, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var guitar = stems.FirstOrDefault(s =>
            string.Equals(s.Name, "guitar", StringComparison.OrdinalIgnoreCase));
        if (guitar is null) return null;

        var outBuf = new float[mono.Length];
        var n = Math.Min(mono.Length, guitar.InterleavedStereo.Length / 2);
        for (var i = 0; i < n; i++)
            outBuf[i] = 0.5f * (guitar.InterleavedStereo[2 * i] + guitar.InterleavedStereo[2 * i + 1]);
        return outBuf;
    }

    public void Dispose()
    {
        _separator.Dispose();
        _gate.Dispose();
    }
}
