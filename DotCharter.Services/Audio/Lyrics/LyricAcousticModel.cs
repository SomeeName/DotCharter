using System.Text.Json;
using DotCharter.Analysis.Lyrics;
using JuceSharp.StemSeparation.Onnx;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     How the waveform is scaled before the model sees it.
///     <para>
///         This is an experiment seam, not a user setting. The published
///         preprocessor normalises every utterance to zero-mean unit-variance, and
///         <see cref="PerWindow" /> reproduces that — but a training utterance was
///         always SPEECH, and a decode window here can be twenty seconds of an
///         instrumental break on a separated vocal stem, where the only thing
///         present is bleed. Normalising that window multiplies the bleed up to
///         full scale and hands the model something that looks like a voice.
///     </para>
/// </summary>
internal enum LyricInputNormalization
{
    /// <summary>Zero-mean unit-variance per decode window — what the published preprocessor does.</summary>
    PerWindow,

    /// <summary>One gain for the whole song, so quiet passages stay quiet relative to loud ones.</summary>
    PerSong,

    /// <summary>Per window, but the gain is capped so a near-silent window is never amplified to full scale.</summary>
    PerWindowFloored,
}

/// <summary>
///     The wav2vec2 CTC acoustic model behind lyric auto-sync, wrapped so the
///     rest of the app only ever sees a <see cref="CtcEmissions" /> matrix.
///     <para>
///         <b>The model is MMS-FA</b>
///         (<c>facebook/mms-300m-1130-forced-aligner</c>). Its output alphabet is
///         31 tokens — a blank, three control tokens, and the 26 roman letters
///         plus an apostrophe — because it was trained to align <i>romanized</i>
///         text in any of 1,100+ languages. That is exactly the input the owner
///         already writes: Japanese charted as romaji, English as itself. One
///         model covers both with no kana conversion and no pronunciation
///         lexicon.
///     </para>
///     <para>
///         <b>Ship the int8 export.</b> Measured over the hand-synced corpus the
///         two are indistinguishable — 76.1% of onsets within 50 ms either way,
///         90.4% vs 90.2% within 100 ms — while the graph goes from 1.26 GB
///         (fp32, which needs a separate <c>model.onnx.data</c> for its external
///         weights) to a single 340 MB file. Quantisation costs nothing here
///         because alignment reads which symbol wins per frame, not the fine
///         structure of the distribution.
///     </para>
///     <para>
///         <b>Audio must be 16 kHz mono, and each chunk is normalised on its
///         own.</b> The published preprocessor sets <c>do_normalize</c>, meaning
///         every training utterance was zero-mean unit-variance; feeding a raw
///         waveform instead shifts the input distribution and quietly costs
///         accuracy. Normalising per chunk rather than per song is what matches
///         training, where the unit was one utterance.
///     </para>
/// </summary>
public sealed class LyricAcousticModel : IDisposable
{
    /// <summary>wav2vec2's convolutional front end decimates by exactly this much.</summary>
    public const int SamplesPerFrame = 320;

    public const int RequiredSampleRate = 16000;

    /// <summary>Frames of real audio each pass keeps. 20 s at 50 fps.</summary>
    private const int CoreFrames = 1000;

    /// <summary>
    ///     Extra frames decoded either side of the core and then thrown away.
    ///     The transformer sees them, so the frames we keep are never the ones
    ///     that sat against an artificial edge — a chunk boundary otherwise
    ///     lands mid-syllable and both halves emit into a discontinuity.
    /// </summary>
    private const int ContextFrames = 100;

    private readonly InferenceSession _session;

    /// <summary>The native <c>SessionOptions</c> the graph was opened with. It is
    /// an unmanaged handle and the session does NOT own it, so it has to be
    /// disposed alongside — two of these are built per auto-sync run.</summary>
    private readonly SessionOptions _sessionOptions;

    private readonly string _inputName;
    private readonly string? _attentionMaskName;
    private readonly string _outputName;
    private readonly Action<string>? _log;

    private LyricAcousticModel(
        InferenceSession session, SessionOptions sessionOptions, string inputName,
        string? attentionMaskName, string outputName,
        LyricVocabulary vocabulary, Action<string>? log)
    {
        _session = session;
        _sessionOptions = sessionOptions;
        _inputName = inputName;
        _attentionMaskName = attentionMaskName;
        _outputName = outputName;
        Vocabulary = vocabulary;
        _log = log;
    }

    public LyricVocabulary Vocabulary { get; }

    /// <summary>
    ///     Opens the ONNX graph and reads the vocabulary beside it.
    ///     <para>
    ///         Input and output names are <b>discovered, not hard-coded</b>. The
    ///         published exports disagree — some carry an
    ///         <c>attention_mask</c> input, some do not — and a wrong guess
    ///         surfaces as an opaque ONNX Runtime error at the first inference
    ///         rather than at load.
    ///     </para>
    /// </summary>
    public static LyricAcousticModel Load(
        string modelPath, string vocabularyPath,
        OnnxExecutionProvider provider = OnnxExecutionProvider.Auto,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabularyPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Alignment model not found.", modelPath);
        if (!File.Exists(vocabularyPath))
            throw new FileNotFoundException("Alignment model vocabulary not found.", vocabularyPath);

        var vocabulary = ReadVocabulary(vocabularyPath);

        var bundle = new OnnxSessionFactory(log).Create(provider);

        // Three native handles, acquired in order, and every step after the first
        // can throw: a corrupt or half-downloaded graph fails the session build,
        // and an export whose IO names make no sense fails the checks below.
        // Before this, NONE of them released anything — the SessionOptions leaked
        // on EVERY load (two per auto-sync run) and the whole InferenceSession
        // leaked on the two metadata throws. Model-load failure is a routine,
        // user-triggerable path. Same shape as OnnxStemSeparator's ctor.
        var session = BuildOrRelease(() => new InferenceSession(modelPath, bundle.Options), bundle.Options);

        return BuildOrRelease(() =>
        {
            var (inputName, maskName, outputName) = ResolveIoNames(
                session.InputMetadata.Keys, session.OutputMetadata.Keys);

            log?.Invoke($"Lyric alignment model loaded on {bundle.Description}: " +
                        $"input='{inputName}'{(maskName != null ? $", mask='{maskName}'" : "")}, " +
                        $"output='{outputName}', vocabulary={vocabulary.SymbolCount} symbols " +
                        $"(blank={vocabulary.BlankId}).");

            return new LyricAcousticModel(
                session, bundle.Options, inputName, maskName, outputName, vocabulary, log);
        }, bundle.Options, session);
    }

    /// <summary>
    ///     Runs <paramref name="build" /> and, if it throws, disposes the handles
    ///     it was handed — in REVERSE acquisition order — before letting the
    ///     exception out. A failing dispose never masks the original fault.
    ///     <para>
    ///         Internal so the ownership rule is testable without an ONNX graph:
    ///         a real <c>InferenceSession</c> cannot be constructed headlessly (it
    ///         needs the 340 MB model file), so this generic seam is what
    ///         <c>LyricAcousticModelDisposalTests</c> drives with fakes.
    ///     </para>
    /// </summary>
    internal static T BuildOrRelease<T>(Func<T> build, params IDisposable?[] onFailure)
    {
        try
        {
            return build();
        }
        catch
        {
            for (var i = onFailure.Length - 1; i >= 0; i--)
            {
                try { onFailure[i]?.Dispose(); }
                catch { /* the original fault is the one worth reporting */ }
            }

            throw;
        }
    }

    /// <summary>
    ///     Pure: picks the input, optional attention-mask and output names out of
    ///     a graph's metadata. Discovered rather than hard-coded because the
    ///     published exports disagree on whether an <c>attention_mask</c> input
    ///     exists, and a wrong guess surfaces as an opaque ONNX Runtime error at
    ///     the first inference rather than at load.
    /// </summary>
    internal static (string Input, string? Mask, string Output) ResolveIoNames(
        IEnumerable<string> inputMetadata, IEnumerable<string> outputMetadata)
    {
        var inputs = inputMetadata.ToList();
        var inputName = inputs.FirstOrDefault(n => n.Contains("input", StringComparison.OrdinalIgnoreCase)
                                                   && !n.Contains("mask", StringComparison.OrdinalIgnoreCase))
                        ?? inputs.FirstOrDefault()
                        ?? throw new InvalidOperationException("The alignment model exposes no inputs.");
        var maskName = inputs.FirstOrDefault(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase));
        var outputName = outputMetadata.FirstOrDefault()
                         ?? throw new InvalidOperationException("The alignment model exposes no outputs.");
        return (inputName, maskName, outputName);
    }

    /// <summary>
    ///     <c>vocab.json</c> is a token→id map; the aligner needs it flattened to
    ///     id order so an index into a logits row can be named.
    /// </summary>
    internal static LyricVocabulary ReadVocabulary(string vocabularyPath)
    {
        using var stream = File.OpenRead(vocabularyPath);
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
                  ?? throw new InvalidOperationException($"'{vocabularyPath}' is not a token→id map.");

        var ordered = new string[map.Count == 0 ? 0 : map.Values.Max() + 1];
        foreach (var (token, id) in map)
        {
            if (id >= 0 && id < ordered.Length)
                ordered[id] = token;
        }

        return LyricVocabulary.FromTokens(ordered);
    }

    /// <summary>
    ///     Runs the whole song and returns log-softmax emissions.
    ///     <paramref name="mono16k" /> must already be 16 kHz mono in [-1, 1].
    /// </summary>
    public CtcEmissions Encode(float[] mono16k, CancellationToken cancellationToken = default) =>
        Encode(mono16k, LyricInputNormalization.PerWindow, DefaultFloorRms, cancellationToken);

    /// <summary>
    ///     The gain cap for <see cref="LyricInputNormalization.PerWindowFloored" />,
    ///     as the quietest RMS still treated as worth normalising up. −40 dBFS: a
    ///     window quieter than this is not a sung phrase, it is bleed.
    /// </summary>
    internal const double DefaultFloorRms = 0.01;

    /// <inheritdoc cref="Encode(float[], CancellationToken)" />
    internal CtcEmissions Encode(
        float[] mono16k, LyricInputNormalization normalization, double floorRms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mono16k);
        if (mono16k.Length < SamplesPerFrame)
            throw new ArgumentException("The audio is shorter than one model frame.", nameof(mono16k));

        // A song-wide gain has to be measured before the first window runs.
        double songMean = 0, songScale = 1;
        if (normalization == LyricInputNormalization.PerSong)
            (songMean, songScale) = Statistics(mono16k, 0, mono16k.Length);

        var width = Vocabulary.SymbolCount;
        var nominalFrames = mono16k.Length / SamplesPerFrame;
        var emissions = new float[(long)nominalFrames * width];

        var coreSamples = CoreFrames * SamplesPerFrame;
        var contextSamples = ContextFrames * SamplesPerFrame;
        var written = 0;

        foreach (var w in LyricWindowStitch.Plan(nominalFrames, CoreFrames, ContextFrames))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var coreStart = w.CoreStart * SamplesPerFrame;
            var windowStart = Math.Max(0, coreStart - contextSamples);
            var windowEnd = Math.Min(mono16k.Length, coreStart + coreSamples + contextSamples);
            var windowLength = windowEnd - windowStart;
            if (windowLength < SamplesPerFrame) break;

            var (mean, scale) = normalization switch
            {
                LyricInputNormalization.PerSong => (songMean, songScale),
                LyricInputNormalization.PerWindowFloored => Floored(
                    Statistics(mono16k, windowStart, windowLength), floorRms),
                _ => Statistics(mono16k, windowStart, windowLength),
            };

            var produced = RunWindow(mono16k, windowStart, windowLength, width, mean, scale, out var window);

            // How many of this window's own rows really belong to the song, and
            // really arrived — see LyricWindowStitch.
            var copied = LyricWindowStitch.CopyCount(w, produced);
            for (var k = 0; k < copied; k++)
                Array.Copy(window, (w.Skip + k) * width, emissions, (long)(w.CoreStart + k) * width, width);

            if (w.CoreStart > written) break;
            written = Math.Max(written, w.CoreStart + copied);
        }

        // The honest frame count. wav2vec2's conv front end emits n−1 rows for
        // 320n samples, so the last window is routinely one short and the final
        // target row stayed as it was allocated: all zeros. In a LOG-probability
        // matrix a zero row is probability 1.0 for every symbol — the cheapest
        // possible frame in the trellis, strictly ≥ every real emission — which
        // inflated CtcAlignment.TotalLogProb and the last syllable's confidence,
        // and that confidence feeds the user-visible LowConfidenceCount the
        // threshold was calibrated against. The model produced no frame there;
        // reporting one is the lie.
        if (written < nominalFrames)
            emissions = emissions.AsSpan(0, (int)((long)written * width)).ToArray();

        return new CtcEmissions(emissions, written, width, (double)SamplesPerFrame / RequiredSampleRate);
    }

    /// <summary>Zero-mean unit-variance statistics over one span, as the published preprocessor computes them.</summary>
    private static (double Mean, double Scale) Statistics(float[] audio, int start, int length)
    {
        var mean = 0.0;
        for (var i = 0; i < length; i++) mean += audio[start + i];
        mean /= length;

        var variance = 0.0;
        for (var i = 0; i < length; i++)
        {
            var d = audio[start + i] - mean;
            variance += d * d;
        }

        return (mean, 1.0 / Math.Sqrt(variance / length + 1e-7));
    }

    /// <summary>
    ///     Caps how far a window may be amplified. The gain is <c>1/rms</c>, so a
    ///     window at −80 dBFS would otherwise be lifted by 10,000× — turning the
    ///     instrumental bleeding through a vocal stem into something the model
    ///     reads as a voice, at full confidence, for as long as the break lasts.
    /// </summary>
    private static (double Mean, double Scale) Floored((double Mean, double Scale) stats, double floorRms) =>
        (stats.Mean, Math.Min(stats.Scale, 1.0 / Math.Max(floorRms, 1e-9)));

    /// <summary>One forward pass, returning log-softmax rows for the window.</summary>
    private int RunWindow(
        float[] audio, int start, int length, int width, double mean, double scale, out float[] logProbs)
    {
        var input = new DenseTensor<float>([1, length]);
        var span = input.Buffer.Span;

        for (var i = 0; i < length; i++) span[i] = (float)((audio[start + i] - mean) * scale);

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        if (_attentionMaskName != null)
        {
            var mask = new DenseTensor<long>([1, length]);
            mask.Buffer.Span.Fill(1L);
            inputs.Add(NamedOnnxValue.CreateFromTensor(_attentionMaskName, mask));
        }

        using var results = _session.Run(inputs, [_outputName]);
        var logits = results.First().AsTensor<float>();

        var frames = logits.Dimensions[^2];
        logProbs = new float[frames * width];

        for (var f = 0; f < frames; f++)
        {
            var max = float.NegativeInfinity;
            for (var c = 0; c < width; c++) max = Math.Max(max, logits[0, f, c]);

            var sum = 0.0;
            for (var c = 0; c < width; c++) sum += Math.Exp(logits[0, f, c] - max);

            var logSum = max + Math.Log(sum);
            for (var c = 0; c < width; c++) logProbs[f * width + c] = (float)(logits[0, f, c] - logSum);
        }

        return frames;
    }

    public void Dispose()
    {
        _session.Dispose();
        // The session does NOT own the options — a separate native handle.
        _sessionOptions.Dispose();
    }
}
