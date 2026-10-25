using DotCharter.Analysis.Lyrics;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>One installed acoustic model, and the alignment settings it was measured with.</summary>
/// <param name="Id">Folder name under the models root; also what the log reports.</param>
/// <param name="Directory">Holds <c>model.onnx</c> and <c>vocab.json</c>.</param>
/// <param name="OnsetBiasSeconds">
///     A per-MODEL constant, not a taste setting. Different CTC heads fire at
///     different points inside a syllable, so the correction belongs to the model
///     that needs it — see <see cref="LyricModelCatalog" /> for the measurements.
/// </param>
public sealed record LyricModel(string Id, string Directory, double OnsetBiasSeconds)
{
    public string ModelPath => Path.Combine(Directory, "model.onnx");
    public string VocabularyPath => Path.Combine(Directory, "vocab.json");
    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(VocabularyPath);
}

/// <summary>What auto-sync should run for one sheet.</summary>
/// <param name="Primary">Whose symbol space the alignment uses. Always present.</param>
/// <param name="Secondary">The second head to blend in, or null to run <paramref name="Primary" /> alone.</param>
/// <param name="PrimaryWeight">Primary's share of the blend; 1.0 when there is no secondary.</param>
/// <param name="OnsetBiasSeconds">
///     The bias for THIS combination — deliberately not a property of either
///     model, because the ensemble's optimum differs from both solo values and for
///     English even changes sign.
/// </param>
/// <param name="VowelInitialOnsetBiasSeconds">
///     Extra shift for syllables that open on a vowel, which CTC places late
///     (<see cref="LyricAlignmentOptions.VowelInitialOnsetBiasSeconds" />). A
///     property of the combination for the same reason as the bias above, and zero
///     wherever it has not been measured.
/// </param>
public sealed record LyricModelSelection(
    LyricModel Primary, LyricModel? Secondary, double PrimaryWeight, double OnsetBiasSeconds,
    double VowelInitialOnsetBiasSeconds = 0.0)
{
    public bool IsEnsemble => Secondary != null;

    /// <summary>Log-friendly description of what ran.</summary>
    public string Describe() => IsEnsemble
        ? $"{Primary.Id}+{Secondary!.Id} (w {PrimaryWeight:0.00})"
        : Primary.Id;
}

/// <summary>
///     The acoustic models lyric auto-sync can use, and which one a given sheet
///     should go to.
///     <para>
///         <b>Two models, because English and romaji are not the same problem.</b>
///         MMS-FA aligns romanized text in 1,100+ languages from a 31-symbol
///         alphabet — which is exactly right for Japanese charted as romaji, where
///         the spelling is nearly phonemic. English orthography is the hardest case
///         there is for a grapheme model, and an English-only head trained on 960 h
///         of English does measurably better at it.
///     </para>
///     <para>
///         <b>Measured over 40 official English vocal arrangements
///         (14,352 syllables), listening to a separated vocal stem:</b>
///     </para>
///     <code>
///                                   &lt;=50ms  &lt;=100ms  &lt;=200ms  |median|    p90
///       MMS-FA                       46.0%    74.5%    82.2%     54 ms  1558 ms
///       wav2vec2-large-960h-lv60     57.1%    75.2%    84.5%     40 ms   601 ms
///       ... the same, bias -10 ms    57.4%    76.7%      -        40 ms   602 ms
///       wav2vec2-base-960h           51.1%    70.5%    81.3%     48 ms   556 ms
///     </code>
///     <para>
///         Paired per song at 100 ms the large English head wins 26 and loses 14.
///         <b>The variance is high and worth knowing about:</b> best +37.4 pp, worst
///         −43.0 pp — it is better on most songs and much worse on a few, so this is
///         a favourable trade rather than a strict improvement. The base model is
///         NOT used: it loses the 100 ms bucket outright.
///     </para>
///     <para>
///         <b>The −10 ms bias on the English model is not the same knob that was
///         rejected for MMS-FA, and the difference is the point.</b> Measured
///         against the vocal stem's own onsets, MMS-FA already fires AT the
///         acoustic onset (median −24 ms against a reference that itself sits
///         ~25 ms late), so biasing it would have moved it away from the voice. The
///         large English head fires ~11 ms LATE of that reference, i.e. ~36 ms after
///         the acoustic onset, so −10 ms moves it TOWARDS the voice and improves
///         both accuracy buckets at once.
///     </para>
/// </summary>
public static class LyricModelCatalog
{
    /// <summary>The multilingual default: romanized text in 1,100+ languages.</summary>
    public const string MultilingualId = "lyric-align";

    /// <summary>The English-only head.</summary>
    public const string EnglishId = "lyric-align-en";

    /// <summary>
    ///     Where a model may be installed, in priority order.
    ///     <para>
    ///         <b>Next to the executable first, then per-user.</b> A model shipped
    ///         with a release lands beside the exe, so it is found with no setup and
    ///         a portable copy of the app carries its own. The per-user location is
    ///         checked second so a hand-installed model — which is how the shipping
    ///         model arrived before this existed — keeps working untouched.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> ModelRoots =>
    [
        // The install's top folder, where a v0.62 install keeps them:
        // models are downloaded state, not binaries, so they never move into app/.
        Path.Combine(InstallLocation.InstallRoot, "models"),
        Path.Combine(UserDataFolders.RoamingAppData, "DotCharter", "models"),
    ];

    /// <summary>
    ///     Resolves a model id to the first root that actually has it, or to the
    ///     preferred root when it is installed nowhere (so the caller has a path to
    ///     name in a "not installed" message, and a place to download it to).
    /// </summary>
    public static LyricModel Resolve(string id, double onsetBiasSeconds, IReadOnlyList<string>? roots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        roots ??= ModelRoots;

        foreach (var root in roots)
        {
            var candidate = new LyricModel(id, Path.Combine(root, id), onsetBiasSeconds);
            if (candidate.IsInstalled) return candidate;
        }

        return new LyricModel(id, Path.Combine(roots[0], id), onsetBiasSeconds);
    }

    /// <summary>
    ///     Share the PRIMARY model takes in the ensemble. Measured on
    ///     both corpora; per-language because the optima differ and the router
    ///     already knows which case it is in.
    /// </summary>
    public const double RomajiBlendWeight = 0.75;

    /// <inheritdoc cref="RomajiBlendWeight" />
    public const double EnglishBlendWeight = 0.60;

    /// <summary>
    ///     The downloads, as published release assets.
    ///     <para>
    ///         <b>Versioned in the asset NAME, not by which release carries them.</b>
    ///         The weights change far more rarely than the app, so the fetcher scans
    ///         releases for the name rather than pinning a tag — that keeps
    ///         re-uploading 400 MB of unchanged weights optional on every release.
    ///     </para>
    ///     <para>
    ///         The hashes are the whole integrity story: a truncated or tampered
    ///         archive is discarded before anything is extracted.
    ///     </para>
    /// </summary>
    public static LyricModelDownload MultilingualDownload { get; } = new(
        MultilingualId, "lyric-align-v1.zip",
        "8c0ededd24910ca79358b7f0a6126c4e7dca103237c02b5a42f5765b27020b07", 226_670_922);

    public static LyricModelDownload EnglishDownload { get; } = new(
        EnglishId, "lyric-align-en-v1.zip",
        "d6029fa8a08b4fcfb5631454e8b54d56b04af2361d449eec4e363f774b475438", 189_000_184);

    /// <summary>
    ///     The multilingual model. Bias 0: alone it already fires at the acoustic
    ///     onset, and in the ensemble the sweep put the optimum at 0 too.
    /// </summary>
    public static LyricModel Multilingual(IReadOnlyList<string>? roots = null) =>
        Resolve(MultilingualId, 0.0, roots);

    /// <summary>The English model running ALONE, carrying its measured −10 ms correction.</summary>
    public static LyricModel English(IReadOnlyList<string>? roots = null) =>
        Resolve(EnglishId, -0.010, roots);

    /// <summary>
    ///     Onset bias for the ENSEMBLE. Both are different from the solo values and
    ///     the English one even changes SIGN: blending pulls the English median to
    ///     −14 ms, so the correction that moves lyrics earlier for other models has to
    ///     move them later. Keeping one bias per model would therefore have been
    ///     wrong in both directions at once.
    /// </summary>
    private const double EnsembleEnglishBias = 0.010;

    private const double EnsembleRomajiBias = 0.0;

    /// <summary>
    ///     Vowel-initial syllables are the late ones on the multilingual head.
    ///     Over the 2,036 hand-synced romaji syllables on a vocal stem they sit a
    ///     median +62 ms from the reference (61% later than 50 ms) against −11 ms
    ///     for the chart as a whole; shifting them by −50 ms moved their median to
    ///     +12 ms and the chart's within-50 ms from 77.3% to 82.1%. Measured for the
    ///     multilingual head only — the English head's vowel-initial error is not.
    /// </summary>
    public const double MultilingualVowelInitialBias = -0.050;

    /// <summary>
    ///     Picks the model for a sheet: the English head only when the text is
    ///     clearly English AND that model is installed, otherwise the multilingual
    ///     one.
    ///     <para>
    ///         <b>Falling back on "not installed" is deliberate.</b> The English
    ///         model is an optional download, so an install that does not have it
    ///         must still align English — a little less well — rather than refuse.
    ///     </para>
    /// </summary>
    public static LyricModel ForSheet(LyricSheet sheet, IReadOnlyList<string>? roots = null)
        => SelectForSheet(sheet, roots).Primary;

    /// <summary>
    ///     What to actually run for a sheet: one head, or BOTH blended.
    ///     <para>
    ///         Ensembling is the default whenever both models are present, because
    ///         it is where the accuracy is — the errors come in coherent runs and
    ///         two heads lose the thread in different places, so the blend halves
    ///         the tail (English p90 602 → 292 ms). It costs a second encode pass.
    ///     </para>
    ///     <para>
    ///         Every degraded case still aligns: one model missing simply runs the
    ///         other alone, with that model's own solo bias.
    ///     </para>
    /// </summary>
    /// <param name="sheet">The parsed lyrics, which is what the router reads.</param>
    /// <param name="roots">Model roots in priority order; null uses <see cref="ModelRoots" />.</param>
    /// <param name="forceEnglish">
    ///     Overrides the router. Null detects (the shipping behaviour, and what
    ///     every in-app caller passes); true asks for the English head, false for
    ///     the multilingual one. An override never conjures a model that is not
    ///     installed — it only replaces the GUESS, so the fall-backs below still apply.
    /// </param>
    /// <param name="allowEnsemble">
    ///     False runs the router's primary ALONE, with that model's own SOLO bias.
    ///     The ensemble bias belongs to the combination and would be wrong — for
    ///     English it even has the opposite sign — applied to a single head.
    /// </param>
    public static LyricModelSelection SelectForSheet(
        LyricSheet sheet, IReadOnlyList<string>? roots = null,
        bool? forceEnglish = null, bool allowEnsemble = true)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var multilingual = Multilingual(roots);
        var english = English(roots);
        var wantsEnglish = forceEnglish ?? LyricScriptDetector.IsLikelyEnglish(sheet);

        if (wantsEnglish && english.IsInstalled)
        {
            return multilingual.IsInstalled && allowEnsemble
                ? new LyricModelSelection(english, multilingual, EnglishBlendWeight, EnsembleEnglishBias)
                : new LyricModelSelection(english, null, 1.0, english.OnsetBiasSeconds);
        }

        if (allowEnsemble && multilingual.IsInstalled && english.IsInstalled)
            return new LyricModelSelection(multilingual, english, RomajiBlendWeight, EnsembleRomajiBias, MultilingualVowelInitialBias);

        return new LyricModelSelection(multilingual, null, 1.0, multilingual.OnsetBiasSeconds, MultilingualVowelInitialBias);
    }
}
