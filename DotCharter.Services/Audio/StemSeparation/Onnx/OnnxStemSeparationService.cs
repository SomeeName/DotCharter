using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Youtube;
using JuceSharp.AudioIo;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.StemSeparation.Onnx;

/// <summary>
/// DotCharter-specific adapter around <see cref="OnnxStemSeparator"/>. The
/// actual ONNX inference, GPU session setup and chunking logic all live in
/// the reusable <c>JuceSharp.StemSeparation.Onnx</c> library — this class
/// only does the bits that are specific to DotCharter's app shell:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Looks up the active stem model in
///         <see cref="StemModelManager"/> / <see cref="StemModelCatalog"/>.</item>
///   <item>Implements <b>GPU auto-promotion</b>: when the active model is the
///         <see cref="StemModelKind.DemucsGgml"/> <c>htdemucs-6s</c> entry AND the
///         user has requested a GPU EP, transparently maps to
///         <c>htdemucs-6s-onnx</c> (downloading it on demand). Only 6s has a
///         twin: see <see cref="MapGgmlToOnnxTwin"/>.</item>
///   <item>Reads the input <c>.wav</c>, hands the float[] buffer to
///         <see cref="OnnxStemSeparator"/>, and writes the returned stems
///         back to disk via <see cref="WavIo"/>.</item>
///   <item>Bridges DotCharter's <see cref="IAppLogger"/> to the library's
///         <c>Action&lt;string&gt;</c> log callback.</item>
/// </list>
/// </remarks>
public sealed class OnnxStemSeparationService : IStemSeparationService
{
    /// <summary>Bounded like the export decode: a full song transcodes in
    /// seconds; a wedged ffmpeg must not hold the separation worker forever.</summary>
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(5);

    private readonly StemModelManager? _manager;
    private readonly IAppLogger? _logger;
    private readonly Action<string> _logBridge;
    private readonly Func<string?> _ffmpegResolver;
    private readonly IStemGpuEnvironment? _gpu;
    private string? _lastLoggedPromotion;

    // Why the LAST run did not run where it was asked to (the provider would not
    // attach, or it attached and could not build the model's session). Shown on the
    // status line until a run honours its request again.
    private volatile string? _lastFallback;

    // configuredFfmpegPath reads AppConfig.FfmpegPath. The resolution must not
    // hard-code `configuredPath: null`, or this service would share the stem
    // encoder's defect: a user who pointed Settings at a specific ffmpeg would still
    // get their input transcoded by a different one.
    public OnnxStemSeparationService(
        StemModelManager? manager = null,
        IAppLogger? logger = null,
        Func<string?>? ffmpegResolver = null,
        IStemGpuEnvironment? gpu = null,
        Func<string?>? configuredFfmpegPath = null)
    {
        _manager = manager;
        _logger = logger;
        _logBridge = msg => logger?.Info(AppLogCategory.Audio, msg);
        _ffmpegResolver = ffmpegResolver ?? (() => MediaToolResolver.Resolve(
            "ffmpeg", AppContext.BaseDirectory, configuredFfmpegPath?.Invoke(),
            MediaToolResolver.CurrentPathDirs(), File.Exists, OperatingSystem.IsWindows()));
        _gpu = gpu;
    }

    /// <summary>
    /// Decides which execution provider and GPU to request from the ONNX session
    /// factory (<see cref="StemExecutionDecision" />): DirectML on the stored or
    /// automatically chosen adapter (<see cref="StemGpuRanking" />), else the CPU with the reason. Without an environment (tests,
    /// tools) the preference is passed through and the factory's own ladder decides.
    /// </summary>
    private StemExecutionPlan ResolvePlan() => ResolvePlan(_manager, _gpu);

    /// <summary>The plan for a manager's preferences on a GPU environment; shared with
    /// <see cref="StemDefaultModelProvisioner"/> so the default it offers is the one a run
    /// would pick.</summary>
    internal static StemExecutionPlan ResolvePlan(
        StemModelManager? manager, IStemGpuEnvironment? gpu, IReadOnlyList<StemGpuAdapter>? adapters = null)
    {
        var userPref = manager?.ExecutionProvider;
        if (gpu == null)
        {
            var parsed = OnnxExecutionProviderParser.Parse(userPref);
            return new StemExecutionPlan(parsed, 0,
                parsed == OnnxExecutionProvider.Cpu ? StemExecutionReason.UserChoseCpu : StemExecutionReason.DirectMl, null);
        }

        return StemExecutionDecision.Decide(userPref, gpu.IsDirectMlAvailable, adapters ?? gpu.Adapters(), manager?.GpuAdapterKey);
    }

    private static string Describe(StemExecutionPlan plan) => StemExecutionDecision.Describe(plan);


    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (path, kind, _) = await ResolveActiveDescriptorAsync(allowDownload: false, ct).ConfigureAwait(false);
            return path != null && IsOnnxKind(kind);
        }
        catch { return false; }
    }

    private static bool IsOnnxKind(StemModelKind k) =>
        k is StemModelKind.MdxNetOnnx or StemModelKind.BsRoformerOnnx or StemModelKind.HtdemucsOnnx;

    private static OnnxStemModelKind MapKind(StemModelKind k) => k switch
    {
        StemModelKind.HtdemucsOnnx => OnnxStemModelKind.Htdemucs,
        StemModelKind.BsRoformerOnnx => OnnxStemModelKind.BsRoformer,
        StemModelKind.MdxNetOnnx => OnnxStemModelKind.MdxNet,
        _ => throw new InvalidOperationException($"Cannot map StemModelKind.{k} to OnnxStemModelKind."),
    };

    public async Task<string> DescribeBackendAsync(CancellationToken ct = default)
    {
        // The first provider query initialises the native ONNX Runtime
        // environment (~50 ms). Callers hit this during service wiring on the
        // UI thread, so keep the probe on a worker.
        var providers = await Task.Run(OnnxStemSeparator_AvailableProviders, ct).ConfigureAwait(false);
        var ep = OnnxExecutionProviderParser.Parse(_manager?.ExecutionProvider);
        var providerList = providers.Count == 0 ? "(none reported)" : string.Join(", ", providers);
        var plan = await Task.Run(ResolvePlan, ct).ConfigureAwait(false);
        var fallback = _lastFallback;
        var gpu = $" Runs on: {Describe(plan)}." +
                  (fallback == null ? "" : $" The last run fell back to the CPU: {fallback}");

        if (_manager != null && StemDefaultModel.IsOnDefault(_manager.ActiveModelId))
        {
            var tier = StemDefaultModel.TierFor(plan);
            var chain = StemDefaultModel.DownloadChain(tier, _manager.CanDownload);
            var installed = StemDefaultModel.FirstInstalled(tier, _manager.IsInstalled);
            var state = installed != null
                ? $"model='{StemModelCatalog.FindById(installed)?.FileName}'"
                : $"none is installed; Separate Stems offers to download '{chain[0]}' " +
                  $"(~{(StemModelCatalog.FindById(chain[0])?.ExactBytes ?? 0) / (1024 * 1024)} MB)";
            return $"ONNX backend (default model, none set as Active, tier {tier}: '{installed ?? chain[0]}', " +
                   $"fallback '{chain[^1]}'): preferred EP={ep}, available=[{providerList}], {state}.{gpu}";
        }

        var (path, kind, promoted) = await ResolveActiveDescriptorAsync(allowDownload: false, ct).ConfigureAwait(false);

        if (path == null)
        {
            var twinId = MapGgmlToOnnxTwin(_manager?.ActiveModelId);
            if (twinId != null && IsGpuRequested(ep))
            {
                return $"ONNX backend: preferred EP={ep}, available=[{providerList}]. " +
                       $"Active model is Demucs ggml; will auto-download '{twinId}' for GPU on first run.";
            }

            return $"ONNX backend: preferred EP={ep}, available=[{providerList}]. " +
                   "Active model file not present on disk.";
        }

        if (!IsOnnxKind(kind))
        {
            return $"ONNX backend: preferred EP={ep}, available=[{providerList}]. " +
                   $"Active model is not an ONNX model (kind={kind}). " +
                   "Native demucs.cpp backend will run.";
        }

        var note = promoted ? " (auto-promoted from Demucs ggml for GPU)" : "";
        return $"ONNX backend READY ({kind}){note}: preferred EP={ep}, available=[{providerList}], " +
               $"model='{Path.GetFileName(path)}'.{gpu}";
    }

    public Task<StemSeparationResult> SeparateAsync(
        string inputAudioPath,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            string? scratchWav = null;
            try
            {
                progress?.Report(0.005);
                var (modelPath, kind, promoted) = await ResolveActiveDescriptorAsync(
                    allowDownload: true,
                    ct,
                    downloadProgress: new Progress<double>(p => progress?.Report(0.005 + 0.045 * p)))
                    .ConfigureAwait(false);

                if (modelPath == null || !IsOnnxKind(kind))
                    return StemSeparationResult.Failure("ONNX backend not engaged — no ONNX stem-separation model is currently active.");

                // ONNX inference reads a WAV via the managed WavIo reader. Any other
                // container (.flac/.mp3/.ogg/.m4a…) is transcoded to a temp stereo
                // float WAV with ffmpeg first — without this, every non-WAV song
                // fell straight through to the native backend, which most release
                // builds don't ship, so separation "failed" with no real cause.
                var wavInput = inputAudioPath;
                if (!string.Equals(Path.GetExtension(inputAudioPath), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    scratchWav = TranscodeToWav(inputAudioPath, ct);
                    if (scratchWav == null)
                    {
                        return StemSeparationResult.Failure(
                            $"ONNX backend could not read '{Path.GetFileName(inputAudioPath)}': the native decoder " +
                            "could not decode it and ffmpeg was not found to transcode it. Install ffmpeg " +
                            "(Settings → Components) or supply the audio as WAV.");
                    }

                    wavInput = scratchWav;
                    _logger?.Info(AppLogCategory.Audio,
                        $"ONNX stem separation: transcoded '{Path.GetFileName(inputAudioPath)}' to a temp WAV for inference.");
                }

                Directory.CreateDirectory(outputDirectory);

                var adapters = _gpu?.Adapters();
                var plan = ResolvePlan(_manager, _gpu, adapters);
                if (adapters != null)
                {
                    _logger?.Info(AppLogCategory.Audio,
                        $"Stem separation GPUs (DirectML device id = DXGI adapter index): {StemGpuRanking.DescribeAdapters(adapters)}. " +
                        $"Chosen: {(plan.Adapter == null ? "none" : $"#{plan.DeviceId} '{plan.Adapter.Name}'")} — " +
                        $"{(plan.GpuChoiceReason.Length > 0 ? plan.GpuChoiceReason : plan.Reason.ToString())}.");
                }

                _logger?.Info(AppLogCategory.Audio,
                    $"ONNX stem separation starting ({kind}{(promoted ? "; auto-promoted from ggml" : "")}). " +
                    $"Input='{inputAudioPath}', model='{modelPath}', out='{outputDirectory}', " +
                    $"preferredEP={plan.Provider} device={plan.DeviceId} ({Describe(plan)}).");
                progress?.Report(0.06);

                var audio = WavIo.Read(wavInput);
                if (audio.Channels != 2)
                    return StemSeparationResult.Failure($"ONNX backend requires stereo input, got {audio.Channels} channel(s).");
                progress?.Report(0.10);

                // Cap the CPU intra-op thread pool so separation never saturates
                // every core and starves the UI / audio thread. GPU EPs ignore
                // this; the CPU EP honours it.
                var threadCap = StemSeparationResourcePolicy.RecommendedWorkerThreads();
                using var separator = new OnnxStemSeparator(
                    modelPath, MapKind(kind), plan.Provider, _logBridge, threadCap, plan.DeviceId);
                _logger?.Info(AppLogCategory.Audio,
                    $"ONNX session bound to {separator.BackendDescription} (intraOpThreads={threadCap}). " +
                    $"Separating {audio.FrameCount} frames @ {audio.SampleRate} Hz.");
                // A GPU request that became a CPU run must never look like a GPU run:
                // the reason goes to the log as a warning and onto the status line.
                _lastFallback = StemExecutionDecision.CpuFallbackReason(
                    plan.Provider, separator.ActiveExecutionProvider, separator.FallbackReason);
                if (_lastFallback != null)
                {
                    _logger?.Warning(AppLogCategory.Audio,
                        $"ONNX stem separation asked for {plan.Provider} and runs on the CPU: {_lastFallback}");
                }

                // Synchronous on the inference thread, one report per finished chunk: the
                // toast gets "chunk i of N" and the log every chunk's time.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var chunkProgress = new StemChunkProgressReporter(
                    () => clock.Elapsed,
                    p => progress?.Report(0.10 + 0.80 * p),
                    progress as IStemChunkProgress,
                    msg => _logger?.Info(AppLogCategory.Audio, msg));
                var stems = await separator.SeparateAsync(
                    audio.Interleaved,
                    audio.SampleRate,
                    chunkProgress,
                    ct).ConfigureAwait(false);
                progress?.Report(0.92);

                var tracks = new List<StemTrack>(stems.Count);
                foreach (var stem in stems)
                {
                    var path = Path.Combine(outputDirectory, stem.Name + ".wav");
                    WavIo.WriteFloat32(path, new AudioBuffer(stem.InterleavedStereo, stem.SampleRate, 2));
                    tracks.Add(new StemTrack { Name = stem.Name, Path = path });
                }
                progress?.Report(1.0);

                _logger?.Info(AppLogCategory.Audio,
                    $"ONNX stem separation finished. Stems={tracks.Count}.");
                return new StemSeparationResult
                {
                    Success = true,
                    OutputDirectory = outputDirectory,
                    Stems = tracks,
                };
            }
            catch (OperationCanceledException)
            {
                // Rethrow, never StemSeparationResult.Failure(): CompositeStemSeparationService reads a
                // failed result as "this backend declined" and starts the NEXT
                // backend, so converting a cancel here restarts the very work the
                // user just stopped.
                _logger?.Info(AppLogCategory.Audio, "ONNX stem separation cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, "ONNX stem separation failed.", ex);
                return StemSeparationResult.Failure(ex.Message);
            }
            finally
            {
                if (scratchWav != null)
                {
                    BestEffortDelete.File(scratchWav);
                }
            }
        }, ct);
    }

    /// <summary>
    ///     Decodes any ffmpeg-readable container to a temp stereo float32 WAV that
    ///     <see cref="WavIo" /> can read, preserving the source sample rate (the
    ///     separator resamples internally). Returns null if ffmpeg is unavailable
    ///     or the transcode failed — the caller turns that into a clear message.
    /// </summary>
    /// <remarks>
    ///     <b>This phase honours the token.</b>
    ///     <c>SeparateAsync</c> threads <c>ct</c> through inference AND through
    ///     the pre-pass, whose two halves are a synchronous full-file JUCE decode
    ///     and an ffmpeg run bounded only by the five-minute
    ///     <see cref="TranscodeTimeout" />. Without that, pressing Cancel on the status pill would do
    ///     nothing observable until the phase ends — up to five minutes of a dead
    ///     affordance on a wedged ffmpeg. Cancellation propagates as
    ///     <see cref="OperationCanceledException" /> rather than a null return,
    ///     because a null here is reported to the user as "could not read this
    ///     file", which a cancel is not.
    /// </remarks>
    private string? TranscodeToWav(string inputAudioPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var temp = Path.Combine(Path.GetTempPath(), $"dotcharter_stem_src_{Guid.NewGuid():N}.wav");

        // The native engine already decodes FLAC/OGG Vorbis/AIFF (and MP3 on
        // Windows) through the JUCE format manager — and juce_sharp.dll ships
        // with EVERY build, while the bundled ffmpeg only ships in packaged
        // releases. Native decode therefore comes FIRST (fixes stem separation
        // of .flac in dev builds, owner report); ffmpeg stays the
        // fallback for anything JUCE can't read (.m4a/.aac/.wma).
        if (TryNativeDecodeToWav(inputAudioPath, temp, ct)) return temp;

        var ffmpeg = _ffmpegResolver();
        if (ffmpeg == null) return null;

        try
        {
            var result = MediaProcess.Run(
                ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-i", inputAudioPath,
                    .. FfmpegAudioOnly.Args,
                    "-ac", "2",
                    "-c:a", "pcm_f32le",
                    "-f", "wav",
                    temp
                ],
                TranscodeTimeout,
                ct);

            if (result.Succeeded && File.Exists(temp) && new FileInfo(temp).Length > 0) return temp;

            _logger?.Error(AppLogCategory.Audio,
                $"ONNX stem separation: ffmpeg transcode of '{Path.GetFileName(inputAudioPath)}' failed. {result.StandardError}".Trim());
            BestEffortDelete.File(temp);
            return null;
        }
        catch (OperationCanceledException)
        {
            // A cancel is not a decode failure. Returning null here would report
            // "could not read this file" and, via the composite's soft-skip, start
            // the NEXT backend on work the user just stopped.
            BestEffortDelete.File(temp);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio,
                $"ONNX stem separation: ffmpeg transcode threw for '{Path.GetFileName(inputAudioPath)}'.", ex);
            BestEffortDelete.File(temp);
            return null;
        }
    }

    /// <summary>Decodes any JUCE-readable file to a temp stereo float32 WAV
    /// via the native engine (no external tool). Returns false when the
    /// format is unsupported or the native DLL is unavailable — the caller
    /// then falls back to ffmpeg.</summary>
    private bool TryNativeDecodeToWav(string inputAudioPath, string outputWavPath, CancellationToken ct = default)
    {
        try
        {
            // The decode itself is a synchronous full-file native call with no
            // abort channel, so these two are the only cancellation points there
            // are: refuse to START one the user has already cancelled, and drop
            // the result of one that was cancelled while it ran.
            ct.ThrowIfCancellationRequested();
            var buf = JuceSharp.AudioIo.JuceAudioDecoder.Decode(inputAudioPath);
            ct.ThrowIfCancellationRequested();
            WriteFloat32StereoWav(outputWavPath, ToStereoInterleaved(buf), buf.SampleRate);
            if (!File.Exists(outputWavPath) || new FileInfo(outputWavPath).Length == 0) return false;
            _logger?.Info(AppLogCategory.Audio,
                $"ONNX stem separation: decoded '{Path.GetFileName(inputAudioPath)}' via the native engine (no ffmpeg needed).");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Never "unavailable, try ffmpeg" — that would start the SECOND decoder
            // on work the user just cancelled.
            BestEffortDelete.File(outputWavPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"ONNX stem separation: native decode of '{Path.GetFileName(inputAudioPath)}' unavailable ({ex.Message}); trying ffmpeg.");
            BestEffortDelete.File(outputWavPath);
            return false;
        }
    }

    private static float[] ToStereoInterleaved(JuceSharp.AudioIo.AudioBuffer buf)
    {
        if (buf.Channels == 2) return buf.Interleaved;

        var frames = buf.FrameCount;
        var dst = new float[frames * 2];
        if (buf.Channels == 1)
        {
            for (var f = 0; f < frames; f++)
            {
                dst[2 * f] = buf.Interleaved[f];
                dst[2 * f + 1] = buf.Interleaved[f];
            }
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                dst[2 * f] = buf.Interleaved[f * buf.Channels];
                dst[2 * f + 1] = buf.Interleaved[f * buf.Channels + 1];
            }
        }

        return dst;
    }

    private static void WriteFloat32StereoWav(string path, float[] interleavedStereo, int sampleRate)
    {
        const int channels = 2;
        var dataBytes = checked((uint)((long)interleavedStereo.Length * sizeof(float)));

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16u);
        w.Write((ushort)3); // WAVE_FORMAT_IEEE_FLOAT
        w.Write((ushort)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)(sampleRate * channels * sizeof(float)));
        w.Write((ushort)(channels * sizeof(float)));
        w.Write((ushort)32);
        w.Write("data"u8);
        w.Write(dataBytes);
        w.Flush();
        fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleavedStereo.AsSpan()));
    }

    public IReadOnlyList<string> AvailableExecutionProviders() => OnnxStemSeparator_AvailableProviders();

    private static IReadOnlyList<string> OnnxStemSeparator_AvailableProviders()
    {
        try { return Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders(); }
        catch { return Array.Empty<string>(); }
    }

    private async Task<(string? Path, StemModelKind Kind, bool Promoted)> ResolveActiveDescriptorAsync(
        bool allowDownload,
        CancellationToken ct,
        IProgress<double>? downloadProgress = null)
    {
        if (_manager == null) return (null, default, false);
        var active = _manager.ActiveModelId;
        if (StemDefaultModel.IsOnDefault(active))
            return await ResolveDefaultAsync(ct).ConfigureAwait(false);

        var desc = StemModelCatalog.FindById(active);
        if (desc == null) return (null, default, false);

        if (IsOnnxKind(desc.Kind))
        {
            var path = _manager.ResolveActiveModelPath();
            return (path != null && File.Exists(path) ? path : null, desc.Kind, false);
        }

        var ep = OnnxExecutionProviderParser.Parse(_manager.ExecutionProvider);
        if (desc.Kind == StemModelKind.DemucsGgml && IsGpuRequested(ep))
        {
            var twinId = MapGgmlToOnnxTwin(active);
            if (twinId != null)
            {
                var twin = StemModelCatalog.FindById(twinId);
                if (twin != null)
                {
                    var twinPath = _manager.GetLocalPath(twin);
                    if (!File.Exists(twinPath))
                    {
                        if (!allowDownload)
                            return (null, twin.Kind, false);

                        _logger?.Info(AppLogCategory.Audio,
                            $"Auto-promoting Demucs ggml '{active}' to ONNX twin '{twinId}' for GPU EP={ep}. " +
                            $"Downloading {twin.FileName} from {twin.DownloadUrl}.");
                        await _manager.DownloadAsync(
                            twinId,
                            new Progress<ModelDownloadProgress>(p => downloadProgress?.Report(p.Fraction)),
                            ct).ConfigureAwait(false);
                    }
                    if (File.Exists(twinPath))
                    {
                        // Resolution runs on every availability / describe query
                        // (three times during startup alone) — log the promotion
                        // once per model pair instead of on each resolve.
                        var promotionKey = $"{active}→{twinId}";
                        if (Interlocked.Exchange(ref _lastLoggedPromotion, promotionKey) != promotionKey)
                        {
                            _logger?.Info(AppLogCategory.Audio,
                                $"Stem separation auto-promoted: '{active}' (ggml) → '{twinId}' (ONNX) for GPU.");
                        }

                        return (twinPath, twin.Kind, true);
                    }
                }
            }
        }

        return (null, desc.Kind, false);
    }

    /// <summary>
    /// The model a user on the default separates with (<see cref="StemDefaultModel"/>):
    /// the first INSTALLED model of the tier's ladder — BS-RoFormer SW 13 s, then 4 s, on a
    /// GPU with enough memory, htdemucs_6s first otherwise. It never downloads: fetching
    /// 358 MB is the user's call, asked by the Separate Stems verb
    /// (<see cref="IStemDefaultModelProvisioner"/>).
    /// </summary>
    private async Task<(string? Path, StemModelKind Kind, bool Promoted)> ResolveDefaultAsync(CancellationToken ct)
    {
        var id = await new StemDefaultModelProvisioner(_manager!, _gpu, _logger).InstalledDefaultAsync(ct)
            .ConfigureAwait(false);
        if (id == null) return (null, default, false);
        var desc = StemModelCatalog.FindById(id);
        var path = _manager!.GetLocalPathIfInstalled(id);
        return desc == null || path == null ? (null, default, false) : (path, desc.Kind, false);
    }

    private static bool IsGpuRequested(OnnxExecutionProvider ep) =>
        ep is OnnxExecutionProvider.DirectML or
        OnnxExecutionProvider.Cuda or
        OnnxExecutionProvider.Auto;

    /// <summary>
    /// The ONNX model a ggml active model is promoted to for a GPU run, or
    /// <c>null</c> when it has none — the ggml model then runs as chosen.
    /// <para><b><c>htdemucs-4s</c> has NO twin</b>. Its twin was
    /// <c>htdemucs-4s-onnx</c>, MrCitron's export, which first could not bind
    /// at all and, once JuceSharp could drive it, measured defective: its stems
    /// add back up to 69 % of the mix at a correlation of 0.84, against the
    /// 6-stem export's 100 % / 0.999. It left both catalogues, and promoting a
    /// working ggml model to a 300 MB download of it would have swapped good
    /// stems for bad ones without asking.</para>
    /// </summary>
    internal static string? MapGgmlToOnnxTwin(string? ggmlId)
    {
        if (string.IsNullOrWhiteSpace(ggmlId)) return null;
        return ggmlId.ToLowerInvariant() switch
        {
            "htdemucs-6s" => "htdemucs-6s-onnx",
            _ => null,
        };
    }
}
