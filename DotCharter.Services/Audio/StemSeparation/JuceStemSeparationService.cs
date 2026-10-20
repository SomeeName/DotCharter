using JuceSharp;

namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
/// Stem-separation backend that delegates to the native JUCE engine
/// (<see cref="JuceStemSeparation"/>), which in turn wraps sevagh/demucs.cpp
/// for fully in-process htdemucs inference. No Python required.
/// </summary>
/// <remarks>
/// Availability has two prerequisites:
///  1. <c>juce_sharp.dll</c> must have been built with
///     <c>-DDOTCHARTER_STEMS_DEMUCS_CPP=ON</c>.
///  2. A ggml htdemucs model file must exist on disk. We discover it in
///     this order:
///       a. The <c>DOTCHARTER_HTDEMUCS_MODEL</c> environment variable.
///       b. <c>%APPDATA%/DotCharter/models/ggml-model-htdemucs-4s-f16.bin</c>
///       c. <c>&lt;dll dir&gt;/models/ggml-model-htdemucs-4s-f16.bin</c>
///
/// CANCELLATION: the native run IS cancellable since ABI v36 — the progress
/// callback returns 0 to stop (<c>dca_stems_separate_file2</c>), the token is
/// checked between the coarse phases and between the per-target WAV writes, and
/// the wrapper answers a stopped run with <see cref="OperationCanceledException"/>.
/// This backend therefore passes the caller's token all the way down and lets
/// that exception PROPAGATE: a cancel is not a failure, and
/// <see cref="CompositeStemSeparationService"/> reads a failed result as "this
/// backend declined, try the next one" — which would start a second multi-minute
/// run on the user's Cancel. Latency is bounded, not instant: a cancel arriving
/// during demucs.cpp's own threaded inference lands when that call returns, and
/// stems already written are LEFT on disk.
/// <para>Keep this remark in step with the engine: a stale claim such as "NOT cancellable"
/// is exactly the class of rotten safety claim the root AGENTS.md warns about.</para>
/// </remarks>
public sealed class JuceStemSeparationService : IStemSeparationService
{
    private readonly IAppLogger? _logger;
    private readonly StemModelManager? _models;
    private readonly INativeStemSeparator _native;

    public JuceStemSeparationService(IAppLogger? logger = null, StemModelManager? models = null)
        : this(logger, models, JuceNativeStemSeparator.Instance)
    {
    }

    /// <summary>Test seam: the native entry point is an interface so the
    /// handling of what it THROWS — a cancel, a missing model, anything else —
    /// is reachable without a demucs build and without minutes of inference.</summary>
    internal JuceStemSeparationService(IAppLogger? logger, StemModelManager? models,
        INativeStemSeparator native)
    {
        _logger = logger;
        _models = models;
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_native.IsSupported) return Task.FromResult(false);
        return Task.FromResult(ResolveModelPath() != null);
    }

    public Task<string> DescribeBackendAsync(CancellationToken ct = default)
    {
        if (!_native.IsSupported)
            return Task.FromResult("Native (juce_sharp.dll) was built without DOTCHARTER_STEMS_DEMUCS_CPP=ON.");
        var model = ResolveModelPath();
        if (model == null)
        {
            return Task.FromResult(
                "Native demucs.cpp ready, but no htdemucs ggml model found. " +
                "Set DOTCHARTER_HTDEMUCS_MODEL or drop a `ggml-model-htdemucs-4s-f16.bin` into " +
                "%APPDATA%/DotCharter/models/.");
        }

        return Task.FromResult($"{JuceStemSeparation.DescribeBackend()} — model='{model}'.");
    }

    public Task<StemSeparationResult> SeparateAsync(
        string inputAudioPath,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!_native.IsSupported)
            return Task.FromResult(StemSeparationResult.Failure("Native stem separation backend not compiled in."));
        if (string.IsNullOrWhiteSpace(inputAudioPath) || !File.Exists(inputAudioPath))
            return Task.FromResult(StemSeparationResult.Failure($"Input audio not found: '{inputAudioPath}'."));
        var model = ResolveModelPath();
        if (model == null)
            return Task.FromResult(StemSeparationResult.Failure(ModelMissingMessage));

        Directory.CreateDirectory(outputDirectory);
        return Task.Run(() =>
        {
            try
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"Native stem separation starting. Input='{inputAudioPath}', model='{model}', out='{outputDirectory}'.");
                _native.Separate(
                    inputAudioPath, outputDirectory, model, numThreads: 0,
                    onProgress: (frac, stage) =>
                    {
                        progress?.Report(frac);
                        _logger?.Trace(AppLogCategory.Audio, $"[demucs.cpp] {frac:P0} {stage}");
                    },
                    // The TOKEN, not CancellationToken.None: ABI v36 made the
                    // native loop cancellable and the Task.Run token below only
                    // gates whether the work STARTS. Without this the Cancel
                    // button stopped the wrapper task while a multi-minute
                    // separation ran on regardless and wrote stems nobody wanted.
                    ct);

                var stems = CollectStems(outputDirectory);
                if (stems.Count == 0)
                    return StemSeparationResult.Failure($"Native stem separation reported success but no stems found under '{outputDirectory}'.");
                _logger?.Info(AppLogCategory.Audio, $"Native stem separation finished. Stems={stems.Count}.");
                return new StemSeparationResult
                {
                    Success = true,
                    OutputDirectory = outputDirectory,
                    Stems = stems,
                };
            }
            catch (OperationCanceledException)
            {
                // A CANCEL IS NOT A FAILURE, and this arm has to sit ABOVE the
                // catch-all or the user's Cancel becomes Success=false with a
                // stale message, the task reports RanToCompletion, every
                // await-side cancellation handler is bypassed, and
                // CompositeStemSeparationService reads the failed result as
                // "this backend declined" and starts the NEXT backend on a
                // separation the user just stopped. Same rule as
                // OnnxStemSeparationService's rethrow.
                _logger?.Info(AppLogCategory.Audio, "Native stem separation cancelled.");
                throw;
            }
            catch (FileNotFoundException ex)
            {
                // THE MODEL IS A DOWNLOAD, NOT A BUG. The wrapper distinguishes
                // "the weights are not on disk" from "no backend compiled in"
                // (native answers DCA_ERR_NOT_INITIALIZED to both), so this is
                // reachable when the file disappears between ResolveModelPath's
                // File.Exists and the call — a delete from Settings ▸ Stem
                // Models mid-run, or a half-written file swept by the verifier.
                // Answer with the SAME actionable sentence the pre-check uses so
                // the UI offers the download instead of reporting a path.
                _logger?.Error(AppLogCategory.Audio,
                    $"Native stem separation: model weights missing at '{ex.FileName ?? model}'.", ex);
                return StemSeparationResult.Failure(ModelMissingMessage);
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, "Native stem separation failed.", ex);
                return StemSeparationResult.Failure(ex.Message);
            }
        }, ct);
    }

    /// <summary>The roaming models folder (<c>%APPDATA%/DotCharter/models</c>), through
    /// <see cref="UserDataFolders" /> so a data-root override moves it.</summary>
    internal static string RoamingModelsDirectory =>
        Path.Combine(UserDataFolders.RoamingAppData, "DotCharter", "models");

    private string? ResolveModelPath()
    {
        // Prefer the central model manager (which honours the user's Settings choice).
        if (_models != null)
        {
            var activeId = _models.ActiveModelId;
            var activeDesc = StemModelCatalog.FindById(activeId);
            // demucs.cpp only understands ggml weights. If the user activated
            // an ONNX model, fall through to the next discovery step so this
            // backend cleanly soft-skips (the ONNX backend will pick it up).
            if (activeDesc == null || activeDesc.Kind == StemModelKind.DemucsGgml)
            {
                var p = _models.ResolveActiveModelPath();
                if (p != null) return p;
            }
        }
        var env = Environment.GetEnvironmentVariable("DOTCHARTER_HTDEMUCS_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var roamingModels = RoamingModelsDirectory;
        foreach (var name in new[]
                 {
                     "ggml-model-htdemucs-4s-f16.bin",
                     "ggml-model-htdemucs-6s-f16.bin",
                     "ggml-model-hdemucs_mmi-v3-f16.bin",
                 })
        {
            var p = Path.Combine(roamingModels, name);
            if (File.Exists(p)) return p;
        }

        // Beside the install, not the binaries: models/ is user state.
        var dllDir = InstallLocation.InstallRoot;
        foreach (var name in new[]
                 {
                     "ggml-model-htdemucs-4s-f16.bin",
                     "ggml-model-htdemucs-6s-f16.bin",
                 })
        {
            var p = Path.Combine(dllDir, "models", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IReadOnlyList<StemTrack> CollectStems(string dir)
    {
        var stems = new List<StemTrack>();
        foreach (var name in new[] { "drums", "bass", "other", "vocals", "guitar", "piano" })
        {
            var p = Path.Combine(dir, name + ".wav");
            if (File.Exists(p)) stems.Add(new StemTrack { Name = name, Path = p });
        }
        return stems;
    }

    /// <summary>
    ///     The ONE sentence this backend says about a missing model, used by the
    ///     pre-flight check and by the wrapper's <see cref="FileNotFoundException" />
    ///     alike so the two can never say different things about the same
    ///     condition. It names the in-app download (Settings ▸ Stem Models, which
    ///     is <c>StemModelManager.DownloadAsync</c>) FIRST, because that is the
    ///     action: the weights are a hash-pinned catalogue row the app can fetch,
    ///     not something a user is expected to find and copy.
    /// </summary>
    internal const string ModelMissingMessage =
        "No htdemucs ggml model is installed. Download one from Settings ▸ Stem Models, " +
        "or set DOTCHARTER_HTDEMUCS_MODEL / place `ggml-model-htdemucs-4s-f16.bin` under " +
        "%APPDATA%/DotCharter/models/.";
}
