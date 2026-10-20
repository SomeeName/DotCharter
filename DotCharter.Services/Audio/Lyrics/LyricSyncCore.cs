using DotCharter.Analysis.Lyrics;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Everything one alignment run needs that is NOT the chart: no beat grid, no
///     <see cref="AppConfig" />, no undoable action.
///     <para>
///         <b>This is the standalone half of <see cref="LyricAutoSyncOptions" />.</b>
///         The app builds those from its settings and then projects them down to
///         these (<see cref="LyricAutoSyncOptions.ToSyncOptions" />); a caller with
///         no project — the <c>dotcharter-lyricsync</c> CLI — builds these
///         directly. Keeping the projection one-way is what stops the second
///         consumer becoming a fork of the first.
///     </para>
/// </summary>
public sealed record LyricSyncOptions
{
    /// <summary>Product defaults: no extra bias, free wildcard, auto everything.</summary>
    public static LyricSyncOptions Default { get; } = new();

    /// <summary>
    ///     Shifts every onset; negative is earlier. Added to the model's OWN
    ///     measured correction, which is not the caller's business — see
    ///     <see cref="LyricModelSelection.OnsetBiasSeconds" />.
    /// </summary>
    public double OnsetBiasSeconds { get; init; }

    /// <summary>
    ///     See <see cref="LyricAlignmentOptions.StarLogProb" /> — 0.0 measured
    ///     better than any penalty over both corpora.
    /// </summary>
    public double StarLogProb { get; init; }

    /// <summary>Execution provider ladder for the ONNX sessions.</summary>
    public OnnxExecutionProvider Provider { get; init; } = OnnxExecutionProvider.Auto;

    /// <summary>
    ///     An ALREADY-SEPARATED vocal stem to listen to instead of the mix.
    ///     <para>
    ///         <b>Null asks <see cref="VocalStemLocator" />; it never starts a
    ///         separation either way.</b> The stem wins decisively when one exists
    ///         (English corpus, within 100 ms: 66.2% on the mix, 75.5% on the
    ///         stem) but separating costs minutes and a GPU, and the caller asked
    ///         for lyrics to be timed. A path given here that does not exist is a
    ///         hard failure, not a silent fall-back to the mix — the caller said
    ///         which file to listen to.
    ///     </para>
    /// </summary>
    public string? VocalStemPath { get; init; }

    /// <summary>
    ///     Overrides the language router. Null detects with
    ///     <see cref="LyricScriptDetector" /> (the shipping behaviour), true forces
    ///     the English head when it is installed, false forces the multilingual one.
    /// </summary>
    public bool? ForceEnglishModel { get; init; }

    /// <summary>
    ///     Blend a second head in when both are installed. ON by default: it is
    ///     where the accuracy is (English p90 602 → 292 ms) and it costs a second
    ///     encode pass. Off runs the router's primary alone, with that model's own
    ///     SOLO onset bias.
    /// </summary>
    public bool AllowEnsemble { get; init; } = true;
}

/// <summary>One syllable of the sheet, timed in SECONDS of the audio file.</summary>
/// <param name="Index">Index into <see cref="LyricSyncResult.Sheet" />'s syllables.</param>
/// <param name="Text">The syllable as typed, with no <c>-</c>/<c>+</c> suffix.</param>
/// <param name="ContinuesWord">The next syllable finishes the same word (Rocksmith's trailing <c>-</c>).</param>
/// <param name="EndsLine">Last syllable of its phrase (Rocksmith's trailing <c>+</c>).</param>
/// <param name="LineIndex">Zero-based phrase this syllable came from.</param>
/// <param name="StartSeconds">Onset, model bias already applied.</param>
/// <param name="EndSeconds">
///     Where CTC stopped hearing it. <b>A floor, not the sung end</b> — a consumer
///     that wants pills without dead gaps spans it toward the next onset, which is
///     what <see cref="LyricAutoSyncService.BuildPhrases" /> does on the beat grid.
/// </param>
/// <param name="Confidence">
///     Mean per-frame probability of this syllable's letters, 0..1. Low absolute
///     values are normal — see <see cref="AlignedSyllable.Confidence" />.
/// </param>
/// <param name="IsInterpolated">Placed from its neighbours because it had no alignable characters.</param>
public readonly record struct LyricSyncSyllable(
    int Index,
    string Text,
    bool ContinuesWord,
    bool EndsLine,
    int LineIndex,
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    bool IsInterpolated);

/// <summary>What one alignment produced, in seconds — no chart, no beat grid.</summary>
/// <param name="Ran">False when the run could not happen at all; <paramref name="Error" /> says why.</param>
/// <param name="Error">Null on success. One of <c>no-audio</c>, <c>no-stem</c>, <c>no-model</c>,
///     <c>no-lyrics</c>, or the aligner's own failure text.</param>
/// <param name="Sheet">The parsed sheet, so a consumer can re-derive word/phrase structure.</param>
/// <param name="Aligned">
///     The raw aligner output, index-parallel to <paramref name="Sheet" />'s syllables. Kept
///     alongside <paramref name="Syllables" /> because <see cref="LyricAutoSyncService.BuildPhrases" />
///     consumes exactly this shape — the app's fold must not go through a lossy projection.
/// </param>
/// <param name="Syllables">The flattened, seconds-based table a non-chart consumer wants.</param>
/// <param name="InterpolatedCount">Syllables placed from neighbours rather than measured.</param>
/// <param name="LowConfidenceCount">Syllables below <see cref="LyricAutoSyncService.LowConfidenceThreshold" />.</param>
/// <param name="AudioSource">"vocal stem" or "mix".</param>
/// <param name="AudioPath">The file actually listened to.</param>
/// <param name="Model">What ran, e.g. <c>lyric-align-en+lyric-align (w 0.60)</c>.</param>
/// <param name="IsEnsemble">True when two heads were blended.</param>
/// <param name="OnsetBiasSeconds">The total bias applied — the combination's own plus the caller's.</param>
/// <param name="AudioSeconds">Duration the emissions covered.</param>
/// <param name="Warnings">Decoder notes, a stem whose name does not say "vocals", a degraded ensemble.</param>
public sealed record LyricSyncResult(
    bool Ran,
    string? Error,
    LyricSheet Sheet,
    IReadOnlyList<AlignedSyllable> Aligned,
    IReadOnlyList<LyricSyncSyllable> Syllables,
    int InterpolatedCount,
    int LowConfidenceCount,
    string AudioSource,
    string AudioPath,
    string Model,
    bool IsEnsemble,
    double OnsetBiasSeconds,
    double AudioSeconds,
    IReadOnlyList<string> Warnings)
{
    /// <summary>A run that could not start. <paramref name="warnings" /> is kept — the decoder's
    /// notes are often the explanation.</summary>
    public static LyricSyncResult Failed(string error, IReadOnlyList<string>? warnings = null) =>
        new(false, error, LyricSheet.Empty, [], [], 0, 0, "", "", "", false, 0.0, 0.0,
            warnings ?? []);
}

/// <summary>
///     The chart-free entry point to lyric alignment: text + audio in, syllable
///     SECONDS out.
///     <para>
///         <b>This is a seam, not a second implementation.</b>
///         <see cref="LyricAutoSyncService" /> implements it and its own
///         <c>SyncAsync</c> calls <see cref="AlignAsync" /> before folding the
///         result onto the chart's beat grid, so the app and the standalone
///         <c>dotcharter-lyricsync</c> CLI run the same decode, the same model
///         router, the same ensemble and the same aligner. Anything that regresses
///         one regresses both — which is the point.
///         Pinned by <c>LyricSyncSeamTests</c>.
///     </para>
///     <para>
///         Everything above the seam is the chart's business: the beat grid, the
///         note-spacing span rules, <c>SetVocalPhrasesAction</c>. Nothing below it
///         knows a chart exists.
///     </para>
/// </summary>
public interface ILyricSyncCore
{
    /// <summary>
    ///     Prefix <see cref="LyricSyncResult.Error" /> carries when the AUDIO could
    ///     not be decoded — the one failure a caller can act on (install ffmpeg, or
    ///     hand over a <c>.wav</c>) without parsing an English message.
    /// </summary>
    const string DecodeFailedPrefix = "decode-failed: ";

    /// <summary>Where the multilingual model must be installed for this instance.</summary>
    string ModelDirectory { get; }

    /// <summary>
    ///     Whether the REQUIRED (multilingual) model is present. The English head is
    ///     an optional upgrade and its absence must never read as "not installed".
    /// </summary>
    bool IsModelInstalled { get; }

    /// <summary>
    ///     Aligns <paramref name="lyricsText" /> (the paste format: newline = phrase,
    ///     space = word, <c>-</c> = syllable) against <paramref name="audioPath" />.
    ///     Background-threaded; cancellable; never throws except for cancellation.
    /// </summary>
    /// <param name="lyricsText">The sheet, in <see cref="LyricSheetParser" />'s format.</param>
    /// <param name="audioPath">The song. Any format the app can decode.</param>
    /// <param name="options">Null uses <see cref="LyricSyncOptions.Default" />.</param>
    /// <param name="status">Coarse progress, for a status line.</param>
    /// <param name="cancellationToken">Cancels the decode, either encode pass, or the trellis.</param>
    Task<LyricSyncResult> AlignAsync(
        string lyricsText,
        string audioPath,
        LyricSyncOptions? options = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}
