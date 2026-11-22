using DotCharter.Localization;
using DotCharter.Analysis.Lyrics;
using DotCharter.Analysis.Lyrics.Syllables;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Audio.StemSeparation;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

public sealed record LyricAutoSyncOptions
{
    public static LyricAutoSyncOptions Default { get; } = new();

    /// <summary>Project stem references captured by the editor. A nonempty list is authoritative.</summary>
    public IReadOnlyList<Stem>? ProjectStems { get; init; }

    /// <summary>The captured project path and audio reference used by the mixer's resolver.</summary>
    public string? ProjectPath { get; init; }

    public string? ProjectAudioFileName { get; init; }

    /// <summary>Shifts every onset. Negative is earlier. See <see cref="LyricAlignmentOptions" />.</summary>
    public double OnsetBiasSeconds { get; init; }

    /// <summary>
    ///     See <see cref="LyricAlignmentOptions.StarLogProb" /> — 0.0 measured
    ///     better than any penalty over the hand-synced corpus.
    /// </summary>
    public double StarLogProb { get; init; }

    public OnnxExecutionProvider Provider { get; init; } = OnnxExecutionProvider.Auto;

    /// <summary>
    ///     Duration cap without further voice evidence. A stem can support a longer
    ///     hold bounded by the next onset; reaching the cap remains a review finding.
    /// </summary>
    public double MaxSyllableSeconds { get; init; } = 4.0;

    /// <summary>
    ///     Span the last syllable of a WORD toward the next word's start
    ///     (minus the minimum gap) instead of ending it where the CTC model
    ///     stopped hearing it. ON by default — the raw CTC ends leave short
    ///     pills with dead gaps between words that are actually sung legato.
    ///     Syllables INSIDE a word always span; this flag decides the
    ///     word-to-word boundary within one phrase.
    /// </summary>
    public bool SpanWordEndToNextWord { get; init; } = true;

    /// <summary>
    ///     Span the last syllable of a PHRASE toward the next phrase's first
    ///     syllable. OFF by default: a phrase break usually IS a breath or an
    ///     instrumental gap, so holding the last syllable across it reads as a
    ///     sustain that was never sung.
    /// </summary>
    public bool SpanPhraseEndToNextPhrase { get; init; }

    /// <summary>
    ///     Thresholds for following the vocal envelope (only used when the sync
    ///     listened to a vocal stem). A measured end moves with the voice in both
    ///     directions: it holds on while the voice sounds and stops where the voice
    ///     falls silent. See <see cref="VocalEndRefiner" />. The chart then applies
    ///     in-phrase spacing to those measured ends.
    /// </summary>
    public VocalEndOptions VocalEnd { get; init; } = VocalEndOptions.Default;

    /// <summary>
    ///     Spacing rules the span keeps as a minimum gap before the next
    ///     syllable — the same <see cref="AppConfig.MinNoteDistanceMode" />
    ///     family notes use (<see cref="ChartSpacingEnforcer.GetMinDistance" />).
    ///     Null means product defaults (measure fraction, 1/64).
    /// </summary>
    public AppConfig? SpacingConfig { get; init; }

    /// <summary>
    ///     The app-config projection — the ONE place the editor settings
    ///     (<see cref="AppConfig.LyricSpanWordGap" /> /
    ///     <see cref="AppConfig.LyricSpanPhraseGap" /> and the note-spacing
    ///     rules) become sync options. Call sites build options through this so
    ///     a settings change is live on the next sync.
    /// </summary>
    public static LyricAutoSyncOptions FromConfig(AppConfig config) => new()
    {
        SpanWordEndToNextWord = config.LyricSpanWordGap,
        SpanPhraseEndToNextPhrase = config.LyricSpanPhraseGap,
        SpacingConfig = config
    };

    /// <summary>
    ///     Projects the chart-free half down to <see cref="LyricSyncOptions" /> —
    ///     the options the SEAM takes. Resolves captured stem references on the sync
    ///     worker; callers must not perform this filesystem work on the UI thread.
    ///     <para>
    ///         <b>One-way, deliberately.</b> Everything dropped here (the span
    ///         rules, <see cref="MaxSyllableSeconds" />, the spacing config) is
    ///         about folding syllables onto a beat grid, which is the only thing
    ///         above the seam. Nothing below it may learn that a chart exists, or
    ///         the standalone consumer starts needing an <see cref="AppConfig" />.
    ///     </para>
    /// </summary>
    public LyricSyncOptions ToSyncOptions()
    {
        var declared = ProjectStems is { Count: > 0 };
        var warnings = new List<string>();
        var audio = ProjectAudioLocator.TryResolve(ProjectPath, ProjectAudioFileName);
        var stem = declared
            ? VocalStemLocator.TryLocate(Path.GetDirectoryName(ProjectPath ?? "") ?? "",
                Path.GetDirectoryName(audio ?? "") ?? "", ProjectStems!, warnings)
            : VocalStemLocator.TryLocate(audio, Path.GetDirectoryName(ProjectPath ?? ""));
        return new LyricSyncOptions
        {
            OnsetBiasSeconds = OnsetBiasSeconds,
            StarLogProb = StarLogProb,
            Provider = Provider,
            VocalStemPath = stem,
            DiscoverVocalStem = !declared,
            StemWarnings = warnings
        };
    }
}

/// <summary>What one auto-sync run produced.</summary>
/// <param name="Ran">False when the run could not happen at all; <paramref name="Error" /> says why.</param>
/// <param name="Error">Null on success.</param>
/// <param name="Phrases">One phrase per line of the pasted sheet, ready for the vocal track.</param>
/// <param name="SyllableCount">Syllables parsed out of the pasted text.</param>
/// <param name="InterpolatedCount">
///     Syllables placed from their neighbours rather than measured, because they
///     had no alignable characters.
/// </param>
/// <param name="LowConfidenceCount">Syllables the model was not confident about; worth a human pass.</param>
/// <param name="AudioSource">Which audio was listened to — the vocal stem or the mix.</param>
public sealed record LyricAutoSyncOutcome(
    bool Ran,
    string? Error,
    IReadOnlyList<LyricPhrase> Phrases,
    int SyllableCount,
    int InterpolatedCount,
    int LowConfidenceCount,
    string AudioSource)
{
    public string AudioPath { get; init; } = "";

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static LyricAutoSyncOutcome Failed(string error) => new(false, error, [], 0, 0, 0, "");
}

public interface ILyricAutoSyncService
{
    /// <summary>Where the acoustic model must be installed.</summary>
    string ModelDirectory { get; }

    bool IsModelInstalled { get; }

    /// <summary>
    ///     Aligns pasted lyrics to a song and returns phrases positioned on the
    ///     supplied beat grid. Background-threaded; cancellable; never throws
    ///     except for cancellation.
    /// </summary>
    Task<LyricAutoSyncOutcome> SyncAsync(
        string lyricsText,
        string audioPath,
        IList<BeatMarker> beats,
        LyricAutoSyncOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Paste a song's lyrics, get them timed. Wraps the pure aligner in
///     <c>DotCharter.Analysis.Lyrics</c> with model loading, audio decoding and
///     the fold back onto the chart's beat grid.
///     <para>
///         <b>Accuracy depends on the corpus, and the headline figure is the
///         optimistic one — quote both.</b> Over the 2,036 hand-synced ROMAJI
///         syllables in <c>TestData/EOF</c>, 90% of onsets land within 100 ms of
///         where a human put them and 76% within 50 ms (median |error| 28 ms,
///         median signed −13 ms, i.e. slightly early — the direction listeners
///         forgive). over 40 stock ENGLISH Rocksmith arrangements (14,272
///         syllables, measured) the same code on the same settings gets
///         66% within 100 ms and 42% within 50 ms.
///     </para>
///     <para>
///         That gap is not a regression and not a language effect in the model —
///         it is what the two corpora ARE. <c>TestData/EOF</c> is four charts the
///         owner hand-synced against sparse, vocal-forward mixes; the English set
///         is arena rock where the voice is buried. The stem preference below is
///         the direct response to it. Rerun <c>LocalOnly_LyricAlignmentProbe</c>
///         (romaji) and <c>LocalOnly_LyricAlignmentPsarcProbe</c> (English) before
///         changing anything here, and never report one of them as "the" number.
///     </para>
///     <para>
///         <b>It listens to an already-separated vocal stem when one exists, and to
///         the mix otherwise</b> (<see cref="VocalStemLocator" />) — it never starts
///         a separation of its own.
///     </para>
///     <para>
///         That preference is measured, not assumed: the published
///         ablations on source separation are mostly negative, but they measure
///         systems trained on polyphonic music, for which separation is a
///         distribution SHIFT — this model is trained on speech, so isolating the
///         voice should move the input TOWARDS its training distribution.
///         <b>Measured over 40 stock ENGLISH Rocksmith vocal
///         arrangements (14,272 syllables), the mechanism holds:</b>
///     </para>
///     <code>
///                   &lt;=50ms   &lt;=100ms   &lt;=200ms   |median|    p90
///       mix          41.6%     66.2%     74.7%      61 ms   5862 ms
///       vocal stem   46.8%     75.5%     83.3%      53 ms   1082 ms
///     </code>
///     <para>
///         Paired per song the stem WINS 36, loses 2, ties 2 — so this is not one
///         catastrophic song flattering the mean. The median barely moves because
///         songs that already worked were never the problem; what collapses is the
///         TAIL, and the tail is what a user notices. The worst song on the mix
///         (20.9% within 100 ms, median absolute error 3,281 ms) lands at 52.7%
///         and 87 ms on its stem.
///     </para>
///     <para>
///         <b>Two honest limits.</b> Those numbers are from LOSSLESS stems out of a
///         fresh separation; the durable stem cache stores Opus, which is accepted
///         on the reasoning that this model hears 16 kHz mono — nothing above
///         8 kHz, which Opus keeps well — but that path is NOT itself measured.
///         And two songs got worse, by up to 5.6 pp. Rerun
///         <c>LocalOnly_LyricAlignmentPsarcProbe</c> before changing any of this.
///     </para>
/// </summary>
public sealed class LyricAutoSyncService : ILyricAutoSyncService, ILyricSyncCore
{
    /// <summary>
    ///     Below this confidence a syllable is worth a human look.
    ///     <para>
    ///         <b>Calibrated against real error, not chosen.</b> The number is
    ///         only useful if the syllables it flags are ENRICHED for genuinely
    ///         bad onsets; a threshold that flags half the chart and finds
    ///         nothing is noise the user learns to ignore. Measured over the
    ///         2,036 hand-synced syllables in <c>TestData/EOF</c>, where 7-8% of
    ///         onsets land worse than 100 ms overall:
    ///     </para>
    ///     <code>
    ///       threshold   flagged   share of flagged &gt;100 ms   vs unflagged
    ///         0.05       15.8%            20.9%                  7.6%
    ///         0.20       39.8%            13.6%                  7.1%
    ///         0.35       54.2%            11.4%                  7.7%
    ///         0.70       76.1%             9.6%                  9.9%
    ///     </code>
    ///     <para>
    ///         So 0.05 flags a sixth of the chart and those are <b>2.8× more
    ///         likely to be wrong</b>, while 0.35 — the value this shipped with
    ///         for one release — flagged 54% at barely better than chance. The
    ///         owner reported it as "191 worth checking" out of 360.
    ///     </para>
    ///     <para>
    ///         Absolute confidences look low because CTC emits a letter on ONE
    ///         frame of its span and blank on the rest, so the per-syllable mean
    ///         is diluted by frames where blank is correct. Do not "fix" that by
    ///         eyeballing a nicer-looking number — re-run
    ///         <c>LocalOnly_LyricAlignmentProbe</c>, which prints this table.
    ///     </para>
    /// </summary>
    internal const double LowConfidenceThreshold = LyricAlignmentReview.LowConfidenceThreshold;

    private readonly IAppLogger? _logger;
    private readonly Func<string, string?> _vocalStem;
    private readonly IReadOnlyList<string> _roots;
    private readonly LyricModel? _pinned;
    private readonly LyricAcousticSessionOpener _openSession;
    private readonly Func<string, IList<string>, float[]> _loadAudio;
    private readonly ILocalizationService _localization;
    private readonly LyricProviderMemory _providerMemory;

    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="logger">App logger.</param>
    /// <param name="modelDirectoryOverride">Test hook: use this exact folder.</param>
    /// <param name="vocalStemLocator">
    ///     Test hook: resolves a mix path to an already-separated vocal stem.
    ///     Defaults to <see cref="VocalStemLocator.TryLocate(string)" />.
    /// </param>
    /// <param name="sessionOpener">
    ///     Test hook: opens the acoustic head. Defaults to
    ///     <see cref="LyricAcousticSessionFactory.Default" />, the real ONNX graph.
    ///     The shipping model is 340 MB and cannot run in the default gate, so this
    ///     is how the orchestration around it is asserted rather than merely read.
    /// </param>
    /// <param name="audioLoader">
    ///     Test hook: decodes a file to 16 kHz mono. Defaults to
    ///     <see cref="LyricAudioSource.LoadMono16k" />.
    /// </param>
    /// <param name="modelRoots">
    ///     Where to LOOK for models, in priority order; null uses
    ///     <see cref="LyricModelCatalog.ModelRoots" />. Unlike
    ///     <paramref name="modelDirectoryOverride" /> this does not disable
    ///     selection — it is how the standalone CLI's <c>--model-dir</c> can still
    ///     find both heads and ensemble them.
    /// </param>
    /// <param name="providerMemory">
    ///     Which models already failed on a GPU provider and open on the CPU; null
    ///     uses <see cref="LyricProviderMemory.Process" />, so the choice outlives
    ///     one sync. A test hook for the same reason as the other seams.
    /// </param>
    public LyricAutoSyncService(
        ILocalizationService localization,
        IAppLogger? logger = null,
        string? modelDirectoryOverride = null,
        Func<string, string?>? vocalStemLocator = null,
        LyricAcousticSessionOpener? sessionOpener = null,
        Func<string, IList<string>, float[]>? audioLoader = null,
        IReadOnlyList<string>? modelRoots = null,
        LyricProviderMemory? providerMemory = null)
    {
        _localization = localization;
        _providerMemory = providerMemory ?? LyricProviderMemory.Process;
        _logger = logger;
        _vocalStem = vocalStemLocator ?? VocalStemLocator.TryLocate;
        _openSession = sessionOpener ?? LyricAcousticSessionFactory.Default;
        _loadAudio = audioLoader ?? LyricAudioSource.LoadMono16k;

        // An explicit override pins ONE folder and disables model selection
        // entirely — a test that says "use this model" means it, and silently
        // reaching for an English head installed on the machine would make the
        // test depend on what happens to be on disk.
        if (!string.IsNullOrWhiteSpace(modelDirectoryOverride))
        {
            _pinned = new LyricModel(LyricModelCatalog.MultilingualId, modelDirectoryOverride, 0.0);
            _roots = [];
            ModelDirectory = modelDirectoryOverride;
        }
        else
        {
            // Explicit roots still SELECT — they say where to look, not which head
            // to run. That is the difference from modelDirectoryOverride above, and
            // it is what lets the CLI's --model-dir find an ensemble.
            _roots = modelRoots is { Count: > 0 } ? modelRoots : LyricModelCatalog.ModelRoots;
            ModelDirectory = LyricModelCatalog.Multilingual(_roots).Directory;
        }
    }

    public string ModelDirectory { get; }

    public string ModelPath => Path.Combine(ModelDirectory, "model.onnx");

    public string VocabularyPath => Path.Combine(ModelDirectory, "vocab.json");

    /// <summary>
    ///     Model roots to search, in priority order. Null uses
    ///     <see cref="LyricModelCatalog.ModelRoots" />; the test hook
    ///     <c>modelDirectoryOverride</c> pins a single explicit folder instead.
    /// </summary>
    private IReadOnlyList<string> Roots => _roots;

    /// <summary>
    ///     Whether ANY model is installed. The multilingual one is what makes the
    ///     feature work at all; the English head is an optional upgrade, so its
    ///     absence must never read as "not installed".
    /// </summary>
    public bool IsModelInstalled =>
        _pinned?.IsInstalled ?? LyricModelCatalog.Multilingual(Roots).IsInstalled;

    public Task<LyricAutoSyncOutcome> SyncAsync(
        string lyricsText, string audioPath, IList<BeatMarker> beats,
        LyricAutoSyncOptions? options = null, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => SyncCore(lyricsText, audioPath, beats, options ?? LyricAutoSyncOptions.Default,
            progress, cancellationToken), cancellationToken);

    /// <summary>
    ///     The chart half, and the ONLY thing this service adds over the seam: the
    ///     beat grid, the span rules and the phrase fold. Everything before
    ///     <see cref="BuildPhrases" /> is <see cref="Align" />, which the
    ///     standalone CLI calls directly.
    /// </summary>
    private LyricAutoSyncOutcome SyncCore(
        string lyricsText, string audioPath, IList<BeatMarker> beats,
        LyricAutoSyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        // A beat grid is the one precondition the seam knows nothing about, so it
        // is checked here and FIRST — the order the outcomes were always reported in.
        if (beats.Count == 0) return LyricAutoSyncOutcome.Failed("no-beats");

        var result = Align(lyricsText, audioPath, options.ToSyncOptions(), progress, ct);
        if (!result.Ran) return LyricAutoSyncOutcome.Failed(result.Error!) with { Warnings = result.Warnings };

        var phrases = BuildPhrases(result.Sheet, result.Aligned, beats, options, result.Envelope);

        _logger?.Info(AppLogCategory.Audio,
            $"Lyric auto-sync: {result.Sheet.Syllables.Count} syllables over {phrases.Count} phrases " +
            $"folded onto {beats.Count} beats.");

        return new LyricAutoSyncOutcome(true, null, phrases, result.Sheet.Syllables.Count,
            result.InterpolatedCount, result.LowConfidenceCount, result.AudioSource)
        {
            AudioPath = result.AudioPath,
            Warnings = result.Warnings
        };
    }

    /// <inheritdoc />
    public Task<LyricSyncResult> AlignAsync(
        string lyricsText, string audioPath, LyricSyncOptions? options = null,
        IProgress<string>? status = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Align(lyricsText, audioPath, options ?? LyricSyncOptions.Default,
            status, cancellationToken), cancellationToken);

    private static LyricSheet ParseSheet(string lyricsText, LyricSyncOptions options)
    {
        var sheet = LyricSheetParser.Parse(lyricsText);
        if (!options.SplitUnhyphenatedWords) return sheet;
        return LyricSheetSyllabifier.DetectLanguage(sheet) is { } language
            ? LyricSheetSyllabifier.Apply(sheet, language)
            : LyricSheetSyllabifier.ApplyJapanese(sheet);
    }

    /// <summary>
    ///     The SEAM: text + audio in, syllable seconds out. Synchronous because
    ///     both callers already own a worker — <see cref="SyncAsync" /> and
    ///     <see cref="AlignAsync" /> are the same <c>Task.Run</c>, so the app's
    ///     threading is unchanged by the split.
    /// </summary>
    internal LyricSyncResult Align(
        string lyricsText, string audioPath, LyricSyncOptions options,
        IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return LyricSyncResult.Failed("no-audio");
        if (!IsModelInstalled) return LyricSyncResult.Failed("no-model");

        var sheet = ParseSheet(lyricsText, options);
        if (sheet.Syllables.Count == 0) return LyricSyncResult.Failed("no-lyrics");

        var warnings = new List<string>(options.StemWarnings);
        foreach (var warning in warnings) _logger?.Warning(AppLogCategory.Audio, warning);

        // Prefer a vocal stem that ALREADY exists (see the class summary for the
        // A/B). Never separate one: the user asked for lyrics to be timed, not for
        // a minutes-long GPU job to start behind their back. An EXPLICIT stem is
        // the caller naming the file, so a missing one fails instead of quietly
        // becoming a mix alignment the caller would read as a bad model.
        string? stemPath;
        if (!string.IsNullOrWhiteSpace(options.VocalStemPath))
        {
            if (!File.Exists(options.VocalStemPath)) return LyricSyncResult.Failed("no-stem");
            stemPath = options.VocalStemPath;

            // Silent-failure guard: every separator also emits the COMPLEMENT, and
            // aligning against `no_vocals` still returns every syllable and simply
            // places them badly. Warn rather than refuse — a hand-renamed stem is legitimate.
            if (!VocalStemLocator.IsVocalStemName(Path.GetFileNameWithoutExtension(stemPath)))
            {
                warnings.Add(
                    $"'{Path.GetFileName(stemPath)}' is not named like a vocal stem. If this is the " +
                    "instrumental (the 'no_vocals' half), every syllable will be placed badly.");
            }
        }
        else
        {
            stemPath = options.DiscoverVocalStem ? SafeLocateVocalStem(audioPath) : null;
        }

        var listenPath = stemPath ?? audioPath;
        var sourceLabel = stemPath != null ? "vocal stem" : "mix";

        try
        {
            progress?.Report(_localization["Progress_DecodingAudio"]);

            // Decode failure is reported with its own PREFIX rather than as a bare
            // message. It is the one failure a caller can act on without reading
            // English — install ffmpeg, or hand over a .wav — and the CLI turns it
            // into its own exit code. Everything else stays an opaque message.
            float[] audio;
            try
            {
                audio = LoadCheckedAudio(audioPath, ref stemPath, warnings);
                listenPath = stemPath ?? audioPath;
                sourceLabel = stemPath != null ? "vocal stem" : "mix";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Error(AppLogCategory.Audio, $"Lyric auto-sync could not decode mix '{audioPath}' or stem '{stemPath}'.", ex);
                return LyricSyncResult.Failed(
                    $"{ILyricSyncCore.DecodeFailedPrefix}{ex.Message}", warnings);
            }

            ct.ThrowIfCancellationRequested();

            // The voice's envelope comes from the samples the aligner already
            // decoded, and only from a stem — a mix's loudness is the band's.
            var envelope = stemPath != null
                ? VocalEnvelope.FromSamples(audio, LyricAcousticModel.RequiredSampleRate)
                : null;

            // The English head only for text that is clearly English, and only if
            // it is installed; everything else goes to the multilingual model.
            var selection = _pinned != null
                ? new LyricModelSelection(_pinned, null, 1.0, _pinned.OnsetBiasSeconds)
                : LyricModelCatalog.SelectForSheet(
                    sheet, Roots, options.ForceEnglishModel, options.AllowEnsemble);
            var chosen = selection.Primary;

            progress?.Report(_localization["Progress_ListeningVocal"]);
            using var model = OpenSession(chosen, options.Provider, warnings, progress);

            var emissions = model.Encode(audio, ct);
            ct.ThrowIfCancellationRequested();

            // The ensemble: a second head's opinion on the same frames. It costs
            // one more encode pass and it is where the accuracy is — the errors
            // arrive in coherent runs, and two heads lose the thread in different
            // places, so blending halves the tail rather than shaving the median.
            if (selection.Secondary is { } secondary)
            {
                progress?.Report(_localization["Progress_ListeningAgain"]);
                emissions = BlendOptionalSecondary(emissions, primary =>
                    {
                        using var other = OpenSession(secondary, options.Provider, warnings, progress);

                        return CtcEmissionBlender.Blend(
                            primary, other.Encode(audio, ct),
                            model.Vocabulary, other.Vocabulary, selection.PrimaryWeight);
                    },
                    ex => selection = UsePrimaryAlone(chosen, secondary, ex, warnings));
                ct.ThrowIfCancellationRequested();

                emissions = WithoutPooledForRomaji(emissions, sheet);
            }

            progress?.Report(_localization["Progress_AligningLyrics"]);
            var totalBias = options.OnsetBiasSeconds + selection.OnsetBiasSeconds;
            var alignment = AlignSheet(sheet, model.Vocabulary, emissions, options, selection, totalBias, ct, envelope);
            ct.ThrowIfCancellationRequested();

            if (!alignment.Succeeded) return LyricSyncResult.Failed(alignment.Failure!, warnings);

            if (stemPath != null) alignment = SnapToStemOnsets(alignment, sheet, audio, options.OnsetSnap);
            ct.ThrowIfCancellationRequested();

            var interpolated = alignment.Syllables.Count(s => s.IsInterpolated);
            var lowConfidence = alignment.Syllables.Count(
                s => !s.IsInterpolated && (s.Confidence < LowConfidenceThreshold || s.ReviewReasons.HasFlag(LyricReviewReason.LowScore)));

            _logger?.Info(AppLogCategory.Audio,
                $"Lyric auto-sync: {sheet.Syllables.Count} syllables from the " +
                $"{sourceLabel} ('{Path.GetFileName(listenPath)}') using model '{selection.Describe()}' " +
                $"(bias {selection.OnsetBiasSeconds * 1000:+0;-0;0} ms), {interpolated} interpolated, " +
                $"{lowConfidence} low-confidence.");

            return new LyricSyncResult(
                true, null, sheet, alignment.Syllables,
                Flatten(sheet, alignment.Syllables),
                interpolated, lowConfidence, sourceLabel, listenPath,
                selection.Describe(), selection.IsEnsemble, totalBias,
                emissions.FrameCount * emissions.SecondsPerFrame, warnings, envelope);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, "Lyric auto-sync failed.", ex);
            return LyricSyncResult.Failed(ex.Message, warnings);
        }
    }

    private LyricModelSelection UsePrimaryAlone(LyricModel primary, LyricModel secondary, Exception failure, List<string> warnings)
    {
        _logger?.Warning(AppLogCategory.Audio,
            $"Lyric auto-sync: the optional '{secondary.Id}' model could not be used " +
            $"({failure.Message}); aligning with '{primary.Id}' alone.");
        warnings.Add(
            $"The optional '{secondary.Id}' model could not be used ({failure.Message}); " +
            $"aligned with '{primary.Id}' alone.");
        return new LyricModelSelection(primary, null, 1.0, primary.OnsetBiasSeconds,
            primary.Id == LyricModelCatalog.MultilingualId ? LyricModelCatalog.MultilingualVowelInitialBias : 0.0);
    }

    /// <summary>Checks every stem against its owning mix before handing samples to the model.
    /// The mix decode is reused when a stem is rejected.</summary>
    private float[] LoadCheckedAudio(string mixPath, ref string? stemPath, IList<string> warnings)
    {
        var mix = _loadAudio(mixPath, warnings);
        if (stemPath == null) return mix;
        var stem = _loadAudio(stemPath, warnings);
        var rate = LyricAcousticModel.RequiredSampleRate;
        var stemSeconds = (double)stem.Length / rate;
        var mixSeconds = (double)mix.Length / rate;
        if (StemTimeBase.Matches(stemSeconds, mixSeconds)) return stem;

        var warning = StemTimeBase.DescribeMismatch(stemPath, stemSeconds, mixPath, mixSeconds);
        warnings.Add(warning);
        _logger?.Warning(AppLogCategory.Audio, warning);
        stemPath = null;
        return mix;
    }

    /// <summary>
    ///     Moves each onset to the vocal stem's own onset near it (<see cref="LyricOnsetSnap" />):
    ///     the stem shows where each syllable's first sound begins, CTC only roughly.
    /// </summary>
    private static LyricAlignmentResult SnapToStemOnsets(
        LyricAlignmentResult alignment, LyricSheet sheet, float[] stem, LyricOnsetSnapOptions? snap)
    {
        if (snap == null || !LyricOnsetSnap.AppliesTo(sheet)) return alignment;

        var onsets = alignment.Syllables.Select(s => s.StartSeconds).ToList();
        var features = VocalOnsetFeatures.FromSamples(stem, LyricAcousticModel.RequiredSampleRate, onsets, snap.ReachSeconds);
        return alignment with { Syllables = LyricOnsetSnap.Apply(alignment.Syllables, sheet, features, snap) };
    }

    /// <summary>
    ///     The trellis over the (possibly blended) emissions, with the model's own
    ///     onset corrections added to the caller's.
    /// </summary>
    private static LyricAlignmentResult AlignSheet(
        LyricSheet sheet, LyricVocabulary vocabulary, CtcEmissions emissions,
        LyricSyncOptions options, LyricModelSelection selection, double totalBias, CancellationToken ct, VocalEnvelope? envelope)
        => LyricAligner.Align(sheet, vocabulary, emissions,
            new LyricAlignmentOptions
            {
                // The model's own correction, plus whatever the caller asked
                // for. The per-model part is a property of where that CTC head
                // fires inside a syllable, so it is not the caller's business.
                OnsetBiasSeconds = totalBias,
                VowelInitialOnsetBiasSeconds = selection.VowelInitialOnsetBiasSeconds,
                StarLogProb = options.StarLogProb
            }, ct, envelope);

    /// <summary>
    ///     Opens one head so that a failed GPU run is answered on the CPU
    ///     (<see cref="LyricProviderFallbackSession" />). Every head goes through
    ///     here, the optional second one included: each is remembered on its own.
    /// </summary>
    private ILyricAcousticSession OpenSession(
        LyricModel model, OnnxExecutionProvider provider, List<string> warnings, IProgress<string>? progress) =>
        new LyricProviderFallbackSession(
            model, provider, _openSession, _providerMemory,
            m => _logger?.Info(AppLogCategory.Audio, m),
            (m, ex) =>
            {
                _logger?.Warning(AppLogCategory.Audio, m, ex);
                var notice = _localization["Progress_LyricGpuFailedCpu"];
                warnings.Add(notice);
                progress?.Report(notice);
            });

    /// <summary>
    ///     Zips the sheet's structure back onto the aligner's times. The two are
    ///     index-parallel by construction (<see cref="LyricAligner.Align" /> writes
    ///     one entry per syllable, interpolating the ones it could not measure), so
    ///     this is a projection and never a join.
    /// </summary>
    private static List<LyricSyncSyllable> Flatten(
        LyricSheet sheet, IReadOnlyList<AlignedSyllable> aligned)
    {
        var syllables = new List<LyricSyncSyllable>(sheet.Syllables.Count);
        for (var i = 0; i < sheet.Syllables.Count && i < aligned.Count; i++)
        {
            var s = sheet.Syllables[i];
            var a = aligned[i];
            syllables.Add(new LyricSyncSyllable(
                i, s.Text, s.ContinuesWord, s.EndsLine, s.LineIndex,
                a.StartSeconds, a.EndSeconds, a.Confidence, a.IsInterpolated)
            {
                ReviewReasons = a.ReviewReasons
            });
        }

        return syllables;
    }

    /// <summary>
    ///     The pooled path recovers words only one head heard; on romaji the English head's
    ///     letters are guesses at Japanese syllables, so it only adds noise there.
    /// </summary>
    internal static CtcEmissions WithoutPooledForRomaji(CtcEmissions emissions, LyricSheet sheet) =>
        LyricScriptDetector.IsLikelyRomaji(sheet) ? emissions with { Pooled = null } : emissions;

    /// <summary>
    ///     Folds the OPTIONAL second head into the primary's emissions, and
    ///     answers a failure with the primary's own.
    ///     <para>
    ///     The second model is a separate, optional download and
    ///     <see cref="LyricModel.IsInstalled" /> is a bare presence check — so
    ///     <c>SelectForSheet</c> returns a <c>Secondary</c> the moment the two
    ///     files merely EXIST. A truncated <c>model.onnx</c>, an unsupported
    ///     opset, or a CUDA OOM while both the 340 MB and the 303 MB session are
    ///     alive must not propagate out of <c>SyncCore</c>'s single try and
    ///     return <c>Failed</c>: the user has a perfectly good multilingual
    ///     model installed and would be told auto-sync failed. The documented contract
    ///     is the opposite — one model missing degrades to the other alone — and
    ///     this is where that is enforced.
    ///     </para>
    ///     <para>
    ///     Cancellation is NOT a failure of the secondary and must keep
    ///     propagating; and <c>Blend</c> either returns a whole new matrix or
    ///     throws, so a refusal cannot leave a half-blended one.
    ///     </para>
    /// </summary>
    internal static CtcEmissions BlendOptionalSecondary(
        CtcEmissions primary, Func<CtcEmissions, CtcEmissions> blend, Action<Exception>? onDegraded)
    {
        try
        {
            return blend(primary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onDegraded?.Invoke(ex);
            return primary;
        }
    }

    /// <summary>
    ///     Resolves the vocal stem, never letting the lookup fail the run — a
    ///     locator that throws must degrade to "align against the mix", which is
    ///     exactly what this feature did before stems were consulted at all.
    /// </summary>
    private string? SafeLocateVocalStem(string audioPath)
    {
        try
        {
            var stem = _vocalStem(audioPath);
            if (string.IsNullOrWhiteSpace(stem) || !File.Exists(stem)) return null;

            _logger?.Info(AppLogCategory.Audio,
                $"Lyric auto-sync found separated vocal stem '{stem}'; checking its duration against the mix.");
            return stem;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Lyric auto-sync could not look for a vocal stem ({ex.GetType().Name}); using the mix.");
            return null;
        }
    }

    /// <summary>
    ///     Folds aligned syllables onto the beat grid, one phrase per line.
    ///     <para>
    ///         The initial end comes from where CTC stopped hearing the syllable,
    ///         bounded by the next onset. The aligner's spans are already
    ///         non-overlapping in frames, and this keeps them non-overlapping
    ///         after the onset bias has moved them.
    ///     </para>
    ///     <para>
    ///         Inside a phrase, an enabled span ends at the next syllable's start
    ///         minus the note-style minimum gap (<see cref="ChartSpacingEnforcer.GetMinDistance" />).
    ///         This closes gaps where CTC stopped hearing a syllable early and trims
    ///         overlaps while keeping the aligned starts. Within a word it always spans.
    ///         With word spanning off, word endings keep their measured end up to
    ///         the same spacing limit. Reaching <see cref="LyricAutoSyncOptions.MaxSyllableSeconds" />
    ///         in measurement needs review, and a vocal stem can support a longer hold.
    ///     </para>
    ///     <para>
    ///         With a vocal <paramref name="envelope" /> the measured end follows the voice
    ///         (<see cref="VocalEndRefiner" />). A phrase-final held vowel extends
    ///         until the voice fades, bounded by the next onset or the envelope end.
    ///         <see cref="LyricAutoSyncOptions.SpanPhraseEndToNextPhrase" /> allows
    ///         spanning across a phrase boundary through these measurement rules.
    ///     </para>
    /// </summary>
    internal static List<LyricPhrase> BuildPhrases(
        LyricSheet sheet, IReadOnlyList<AlignedSyllable> aligned,
        IList<BeatMarker> beats, LyricAutoSyncOptions options, VocalEnvelope? envelope = null)
    {
        var phrases = new List<LyricPhrase>();
        LyricPhrase? phrase = null;
        var spacingConfig = options.SpacingConfig ?? DefaultSpacingConfig;
        var endResult = LyricSyllableEnds.ComputeWithReview(
            [.. sheet.Syllables.Select((syllable, i) => new LyricEndSyllable(
                aligned[i].StartSeconds, aligned[i].EndSeconds, syllable.LineIndex, ShouldSpanToNext(syllable, options)))],
            (start, nextStart) => SpanTargetSeconds(start, nextStart, beats, spacingConfig),
            options.MaxSyllableSeconds, envelope, options.VocalEnd);
        var review = LyricAlignmentReview.Evaluate(sheet, aligned, envelope);

        for (var i = 0; i < sheet.Syllables.Count; i++)
        {
            var syllable = sheet.Syllables[i];
            var placement = aligned[i];

            if (phrase == null)
            {
                phrase = new LyricPhrase { Position = Position(placement.StartSeconds, beats) };
                phrases.Add(phrase);
            }

            var end = endResult.Ends[i];

            // The hyphen is INPUT SYNTAX, never stored text. In this model the
            // continuation lives in IsWordPart, and the writers append the '-'
            // themselves at export (RsXmlVocalsWriter) or emit a WORD_PART flag
            // (RscpWriter) — which is also what the Ctrl+Space tap path does.
            // Storing it here put a visible '-' on every syllable in the editor.
            var lyric = new LyricEvent
            {
                Position = Position(placement.StartSeconds, beats),
                EndPosition = Position(end, beats),
                Text = syllable.Text,
                AlignmentConfidence = placement.Confidence,
                ReviewReasons = review[i] | (endResult.CapHits[i] ? LyricReviewReason.CapHit : LyricReviewReason.None)
            };
            LyricLineRule.Apply(lyric, syllable.ContinuesWord, syllable.EndsLine);
            phrase.Lyrics.Add(lyric);

            if (syllable.EndsLine)
            {
                phrase.EndPosition = Position(end, beats);
                phrase = null;
            }
        }

        // A sheet whose last line carried no explicit end still needs closing.
        if (phrase is { Lyrics.Count: > 0 })
            phrase.EndPosition = phrase.Lyrics[^1].EndPosition;

        LyricPhraseSpacing.CloseGaps(phrases, beats, spacingConfig, options.SpanWordEndToNextWord);
        return phrases;
    }

    /// <summary>Product-default spacing when the caller passes no config — one
    /// shared instance so the pure fold allocates nothing per call.</summary>
    private static readonly AppConfig DefaultSpacingConfig = new();

    /// <summary>
    ///     Whether the gap after <paramref name="syllable" /> may be spanned.
    ///     Within one word, always — the word is sung through. Across a word or
    ///     phrase boundary the matching option decides.
    /// </summary>
    private static bool ShouldSpanToNext(LyricSyllable syllable, LyricAutoSyncOptions options)
    {
        if (syllable.ContinuesWord) return true;
        if (syllable.EndsLine) return options.SpanPhraseEndToNextPhrase;
        return options.SpanWordEndToNextWord;
    }

    /// <summary>
    ///     The spanned end: the next syllable's start minus the minimum gap,
    ///     evaluated on the beat grid the way note spacing is
    ///     (<see cref="ChartSpacingEnforcer.EnforceSoundSpacing" /> anchors the
    ///     min distance at the earlier sound's position), then folded back to
    ///     seconds. Never earlier than the syllable's own start.
    /// </summary>
    private static double SpanTargetSeconds(
        double startSeconds, double nextStartSeconds, IList<BeatMarker> beats, AppConfig config)
    {
        var startPos = Position(startSeconds, beats);
        var nextPos = Position(nextStartSeconds, beats);
        var targetPos = FractionalPosition.Max(startPos, LyricPhraseSpacing.EndLimit(startPos, nextPos, beats, config));
        return targetPos.GetPositionMs(beats) / 1000.0;
    }

    private static FractionalPosition Position(double seconds, IList<BeatMarker> beats) =>
        TimelineMath.PositionFromMs(Math.Max(0.0, seconds) * 1000.0, beats);
}
