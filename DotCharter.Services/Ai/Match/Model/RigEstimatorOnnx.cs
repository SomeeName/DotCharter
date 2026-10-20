using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using JuceSharp.StemSeparation.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DotCharter.Services.Ai.Match.Model;

/// <summary>
///     <see cref="IRigEstimator" /> over the ONNX rig estimator trained by
///     <c>tools/rig-model</c>: one 5 s clip of wet guitar in, a guessed chain
///     with knob values out, for the matcher to use as a prior.
///     <para>
///         The model and its sidecar live in
///         <c>%LOCALAPPDATA%\DotCharter\ai-match\rig-estimator\</c>. Neither is
///         bundled, so <see cref="IsAvailable" /> is false on a machine that has
///         not downloaded them and every caller degrades to search-only rather
///         than failing.
///     </para>
///     <para>
///         <b>CPU only, deliberately.</b> This runs while the audio engine is
///         live and a GPU session would contend with the stem separator's, so
///         the session is built with the CPU provider and a small thread count:
///         a tone guess is not worth a dropout. One 5 s clip is ~80 ms.
///     </para>
///     <para>
///         <b>Every number here has a twin in python.</b> The input
///         preparation, the <c>[0,1]</c> knob normalisation and the output
///         mapping are all reproduced from <c>tools/rig-model</c>, and
///         <c>RigEstimatorParityTests</c> runs the shipped ONNX on the sidecar's
///         fixture and compares against the numbers the export wrote. A
///         mismatch there means the two halves have drifted — which is silent
///         otherwise, because a drifted estimator still returns a plausible rig.
///     </para>
/// </summary>
public sealed class RigEstimatorOnnx : IRigEstimator, IDisposable
{
    /// <summary>The folder the model and sidecar are read from.</summary>
    public static string DefaultModelDirectory => Path.Combine(
        UserDataFolders.LocalAppData,
        "DotCharter", "ai-match", "rig-estimator");

    internal const string ModelFileName = "model.onnx";
    internal const string SidecarFileName = "model.sidecar.json";

    internal const string InputName = "audio";
    internal static readonly string[] OutputNames =
        ["family_presence", "slot_probs", "knob_values", "confidence"];

    /// <summary>The RMS the trainer normalises every clip to, in dBFS. The
    /// dataset renderer normalises its output the same way, so an un-normalised
    /// clip is simply a level the model has never seen.</summary>
    internal const float TargetRmsDbfs = -18f;

    private readonly string _directory;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    private InferenceSession? _session;
    private SessionOptions? _options;
    private RigLabelVocabularySnapshot? _vocab;
    private string _modelId = "rig-estimator";
    private bool _loadFailed;
    private bool _disposed;

    /// <summary>Creates an estimator over a model directory.</summary>
    /// <param name="modelDirectory">Where <c>model.onnx</c> and
    /// <c>model.sidecar.json</c> live; defaults to
    /// <see cref="DefaultModelDirectory" />.</param>
    /// <param name="log">Optional sink for load and fallback messages.</param>
    public RigEstimatorOnnx(string? modelDirectory = null, Action<string>? log = null)
    {
        _directory = modelDirectory ?? DefaultModelDirectory;
        _log = log;
    }

    /// <summary>The model file this instance would load.</summary>
    public string ModelPath => Path.Combine(_directory, ModelFileName);

    /// <summary>The sidecar this instance would load.</summary>
    public string SidecarPath => Path.Combine(_directory, SidecarFileName);

    /// <summary>
    ///     True when both the model and its sidecar are present and loadable.
    ///     A missing model is the NORMAL state — nothing is bundled — so this
    ///     never throws and callers fall back to search-only matching.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (_disposed || _loadFailed) return false;
            if (!File.Exists(ModelPath) || !File.Exists(SidecarPath)) return false;
            try
            {
                return EnsureLoaded() is not null;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Rig estimator unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Runs one estimate over a wet guitar clip.</summary>
    /// <param name="wet">The wet audio. Resampled and trimmed to the model's
    /// fixed 5 s window; hand it 48 kHz to avoid the fallback resampler.</param>
    /// <param name="ct">Cancels before the inference starts.</param>
    /// <returns>The guessed rig, per-family presence and a confidence.</returns>
    /// <exception cref="InvalidOperationException">The model is not available.</exception>
    public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wet);
        ct.ThrowIfCancellationRequested();

        var session = EnsureLoaded()
                      ?? throw new InvalidOperationException(
                          $"No rig estimator model at {ModelPath}. Check IsAvailable first.");
        var vocab = _vocab!;

        var input = PrepareInput(wet, vocab.SampleRate, SamplesFor(vocab));
        ct.ThrowIfCancellationRequested();

        float[] family, slots, knobs, confidence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tensor = new DenseTensor<float>(input, [1, input.Length]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(InputName, tensor)]);
            // Only the four outputs this reader needs are materialised, BY NAME.
            // A model may publish MORE — the v2 chain-decoder export adds
            // `chain_tokens` and `chain_knobs` beside these four — and reading
            // every result into `float[]` couples this reader to the exporter's
            // whole output list: it throws on an output it has no opinion about,
            // which turns an ADDITIVE model change into "the shipped app cannot
            // load the new model", silently, at the first estimate.
            var byName = new Dictionary<string, DisposableNamedOnnxValue>(StringComparer.Ordinal);
            foreach (var r in results) byName[r.Name] = r;
            family = ReadOutput(byName, OutputNames[0]);
            slots = ReadOutput(byName, OutputNames[1]);
            knobs = ReadOutput(byName, OutputNames[2]);
            confidence = ReadOutput(byName, OutputNames[3]);
        }

        return Task.FromResult(Decode(vocab, family, slots, knobs, confidence));
    }

    /// <summary>One named output as a flat float array.</summary>
    /// <param name="byName">The run's results, keyed by output name.</param>
    /// <param name="name">The output to read.</param>
    /// <returns>The output's values.</returns>
    /// <exception cref="InvalidDataException">The model has no such output.</exception>
    private static float[] ReadOutput(
        IReadOnlyDictionary<string, DisposableNamedOnnxValue> byName, string name)
        => byName.TryGetValue(name, out var value)
            ? value.AsTensor<float>().ToArray()
            : throw new InvalidDataException(
                $"the model publishes no '{name}' output (it has: "
                + $"{string.Join(", ", byName.Keys)}). Every rig estimator must emit the four "
                + "v1 outputs, whatever else it adds.");

    internal static int SamplesFor(RigLabelVocabularySnapshot vocab)
        => (int)Math.Round(vocab.SampleRate * (double)vocab.ClipSeconds);

    /// <summary>
    ///     Wet audio to the model's exact input vector: mono at the model's
    ///     rate, the CENTRE 5 s, normalised to <see cref="TargetRmsDbfs" />.
    ///     The twin of <c>prepare_input</c> in <c>tools/rig-model/export_onnx.py</c>.
    /// </summary>
    /// <param name="wet">The source audio.</param>
    /// <param name="rate">The rate the model expects.</param>
    /// <param name="samples">The number of samples the model expects.</param>
    /// <returns>The input vector.</returns>
    internal static float[] PrepareInput(MonoAudio wet, int rate, int samples)
    {
        var source = wet.Samples ?? [];
        var resampled = wet.SampleRate == rate ? source : Resample(source, wet.SampleRate, rate);

        var window = new float[samples];
        if (resampled.Length > samples)
        {
            // The CENTRE of a longer clip: a span a user picked usually has its
            // interesting playing in the middle, and the head is often silence.
            var start = (resampled.Length - samples) / 2;
            Array.Copy(resampled, start, window, 0, samples);
        }
        else
        {
            Array.Copy(resampled, window, resampled.Length);
        }

        return NormaliseRms(window, TargetRmsDbfs);
    }

    /// <summary>Normalises to a target RMS in dBFS, in place, and returns the
    /// same array. Digital silence is left alone rather than amplified into
    /// noise, and the gain is capped so a near-silent clip cannot be blown up
    /// by 60 dB.</summary>
    /// <param name="samples">The buffer to normalise.</param>
    /// <param name="targetDbfs">The wanted RMS, in dBFS.</param>
    /// <returns><paramref name="samples" />.</returns>
    internal static float[] NormaliseRms(float[] samples, float targetDbfs)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            if (float.IsFinite(s))
                sum += (double)s * s;
        }

        var rms = Math.Sqrt(sum / Math.Max(samples.Length, 1));
        if (rms < 1e-8) return samples;

        var target = Math.Pow(10.0, targetDbfs / 20.0);
        var gain = Math.Min(target / rms, 64.0);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (float)(samples[i] * gain);
            // The trainer's clips cannot exceed full scale, so neither may this
            // -- and a NaN in the input is a NaN through every head.
            samples[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }

        return samples;
    }

    /// <summary>
    ///     Linear-interpolating fallback resampler. It is a FALLBACK: the model
    ///     was trained on 48 kHz renders and linear interpolation has audible
    ///     image distortion above ~8 kHz, which lands squarely on the brightness
    ///     cues the drive and cab heads key off. Hand this class 48 kHz.
    /// </summary>
    private static float[] Resample(float[] source, int from, int to)
    {
        if (source.Length == 0 || from <= 0) return [];
        var ratio = (double)to / from;
        var n = Math.Max(1, (int)Math.Round(source.Length * ratio));
        var output = new float[n];
        for (var i = 0; i < n; i++)
        {
            var x = i / ratio;
            var i0 = (int)x;
            var frac = (float)(x - i0);
            var a = i0 < source.Length ? source[i0] : 0f;
            var b = i0 + 1 < source.Length ? source[i0 + 1] : a;
            output[i] = a + (b - a) * frac;
        }

        return output;
    }

    /// <summary>Maps the four raw head outputs onto a rig.</summary>
    /// <param name="vocab">The sidecar's label space.</param>
    /// <param name="family">Family presence probabilities.</param>
    /// <param name="slots">Flattened slot probabilities, slots x classes.</param>
    /// <param name="knobs">The flat knob vector, in <c>[0,1]</c>.</param>
    /// <param name="confidence">The confidence head.</param>
    /// <returns>The decoded estimate.</returns>
    internal static RigEstimate Decode(
        RigLabelVocabularySnapshot vocab, float[] family, float[] slots, float[] knobs, float[] confidence)
    {
        var classes = vocab.Modules.Count + 1;
        var modules = new List<DcRigModule>();

        for (var slot = 0; slot < vocab.Slots; slot++)
        {
            var offset = slot * classes;
            if (offset + classes > slots.Length) break;

            var best = 0;
            var bestP = slots[offset];
            for (var c = 1; c < classes; c++)
            {
                if (slots[offset + c] > bestP)
                {
                    bestP = slots[offset + c];
                    best = c;
                }
            }

            if (best == 0) continue;   // the empty-slot label
            var entry = vocab.Modules[best - 1];

            // v1 is series-only, exactly as ToneDraftValidator has it: a
            // splitter with no merger (or the reverse) is not a rig, and a
            // per-slot argmax cannot guarantee the pair.
            if (string.Equals(entry.Category, "routing", StringComparison.Ordinal)) continue;

            // The MODEL's knob outputs, read through the MODEL's own label space
            // (its sidecar): offsets and ranges are the ones it was trained on.
            var predicted = new Dictionary<string, float>(entry.Params.Count, StringComparer.Ordinal);
            for (var p = 0; p < entry.Params.Count; p++)
            {
                var spec = entry.Params[p];
                var ix = entry.ParamOffset + p;
                var unit = ix < knobs.Length ? knobs[ix] : 0f;
                predicted[spec.Id] = RigLabelVocabulary.Denormalise(
                    new ParamRange(spec.Id, spec.Min, spec.Max, spec.Default), unit);
            }

            if (ConformToCatalogue(entry.TypeId, predicted) is { } parameters)
                modules.Add(new DcRigModule(entry.TypeId, parameters));
        }

        var presence = new Dictionary<string, float>(vocab.Families.Count, StringComparer.Ordinal);
        for (var i = 0; i < vocab.Families.Count && i < family.Length; i++)
            presence[vocab.Families[i]] = family[i];

        var conf = confidence.Length > 0 && float.IsFinite(confidence[0])
            ? Math.Clamp(confidence[0], 0f, 1f)
            : 0f;

        return new RigEstimate(new DcRigPreset(modules), presence, conf, "rig-estimator");
    }

    /// <summary>
    ///     Re-expresses one predicted module in TODAY'S catalogue, by param NAME.
    ///     <para>
    ///         A shipped model is decoded through the label space it was trained
    ///         on (its own sidecar), and the catalogue moves on after it ships:
    ///         <c>rig-estimator-v1.zip</c> (v0.55.0) was exported against 573 knobs
    /// and the amp / bass-head params made it 582. So
    ///         every param the catalogue has and the model does not predict takes
    ///         its catalogue DEFAULT — which for those params is the sound the
    ///         model was trained on, they are all skipped at their default — every
    ///         predicted value is clamped to today's range, and a param or module
    ///         the catalogue lacks is dropped. That is what keeps an older
    ///         model loading and useful without a retrain
    ///         (<c>RigEstimatorV1CompatibilityTests</c>).
    ///     </para>
    /// </summary>
    /// <param name="moduleTypeId">The predicted module's type id.</param>
    /// <param name="predicted">Its predicted params, in catalogue units, by id.</param>
    /// <returns>The module's full param set in today's catalogue, or null when
    /// the catalogue has no such module.</returns>
    internal static Dictionary<string, float>? ConformToCatalogue(
        string moduleTypeId, IReadOnlyDictionary<string, float> predicted)
    {
        var specs = DcRigTargetCatalog.AllModules
            .FirstOrDefault(m => string.Equals(m.ModuleTypeId, moduleTypeId, StringComparison.Ordinal)).Params;
        if (specs is null) return null;

        var parameters = new Dictionary<string, float>(specs.Count, StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            parameters[spec.Id] = predicted.TryGetValue(spec.Id, out var v) && float.IsFinite(v)
                ? Math.Clamp(v, spec.Min, spec.Max)
                : spec.Default;
        }

        return parameters;
    }

    private InferenceSession? EnsureLoaded()
    {
        if (_session is not null) return _session;
        if (_loadFailed) return null;

        lock (_gate)
        {
            if (_session is not null) return _session;
            if (_loadFailed || _disposed) return null;
            if (!File.Exists(ModelPath) || !File.Exists(SidecarPath)) return null;

            try
            {
                var snapshot = JsonSerializer.Deserialize<RigLabelVocabularySnapshot>(
                    File.ReadAllText(SidecarPath)) ?? throw new InvalidDataException($"{SidecarPath} did not deserialise");
                if (snapshot.SchemaVersion != RigLabelVocabulary.SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"sidecar schemaVersion {snapshot.SchemaVersion} != "
                        + $"{RigLabelVocabulary.SchemaVersion}; the model was exported against a "
                        + "different label space and its knob indices no longer mean anything");
                }

                // CPU, and few threads: the audio engine owns this machine.
                var factory = new OnnxSessionFactory(_log);
                var bundle = factory.Create(OnnxExecutionProvider.Cpu, intraOpNumThreads: 2);
                _options = bundle.Options;
                _session = new InferenceSession(ModelPath, _options);
                _vocab = snapshot;
                _modelId = ReadModelId(SidecarPath) ?? "rig-estimator";
                _log?.Invoke($"Rig estimator loaded: {_modelId} ({bundle.Description})");
                return _session;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _options?.Dispose();
                _options = null;
                _session?.Dispose();
                _session = null;
                _log?.Invoke($"Rig estimator failed to load: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private static string? ReadModelId(string sidecarPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        return doc.RootElement.TryGetProperty("export", out var export)
               && export.TryGetProperty("modelId", out var id)
            ? id.GetString()
            : null;
    }

    /// <summary>Releases the ONNX session and its options.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
            _options?.Dispose();
            _options = null;
        }
    }
}
