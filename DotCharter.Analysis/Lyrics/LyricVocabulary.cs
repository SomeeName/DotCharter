namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     The acoustic model's output alphabet, plus the synthetic wildcard column
///     the aligner appends to it.
///     <para>
///         The model this was built for is <b>MMS-FA</b>
///         (<c>facebook/mms-300m-1130-forced-aligner</c>), whose vocabulary is
///         <b>31 tokens</b>: <c>&lt;blank&gt;</c>, <c>&lt;pad&gt;</c>,
///         <c>&lt;/s&gt;</c>, <c>&lt;unk&gt;</c>, then the 26 lower-case roman
///         letters and an apostrophe. That is the whole reason this feature is
///         one model rather than two: MMS-FA aligns <b>romanized</b> text in any
///         of its 1,100+ languages, and the owner already charts Japanese in
///         romaji. English lyrics are romanized by definition. So Japanese and
///         English share one alphabet, one model and one code path — no kana
///         conversion, no grapheme-to-phoneme, no pronunciation lexicon.
///     </para>
///     <para>
///         <b>The star column is synthetic and lives one past the model's own
///         width.</b> CTC alignment forces EVERY frame to be explained by the
///         token sequence, so an instrumental intro, a solo or an untranscribed
///         backing vocal has to be absorbed by whatever token happens to sit at
///         the seam — which drags that syllable's onset far off. The standard
///         fix (torchaudio's <c>&lt;star&gt;</c>) is a column whose log-prob is
///         <b>0.0, i.e. probability 1.0, in every frame</b>: it matches anything
///         at no cost, so unsung audio is consumed by a star instead of by a
///         real syllable. We never ask the model for it — the emission provider
///         appends the column — so <see cref="StarId" /> is deliberately equal
///         to <see cref="SymbolCount" /> rather than being a vocabulary entry.
///     </para>
/// </summary>
public sealed class LyricVocabulary
{
    private readonly Dictionary<char, int> _charToId;

    private LyricVocabulary(Dictionary<char, int> charToId, int blankId, int symbolCount)
    {
        _charToId = charToId;
        BlankId = blankId;
        SymbolCount = symbolCount;
    }

    /// <summary>The CTC blank. Index 0 for MMS-FA, but resolved by NAME.</summary>
    public int BlankId { get; }

    /// <summary>
    ///     The model's own output width — the number of columns an emission
    ///     frame actually carries before <see cref="StarId" /> is appended.
    /// </summary>
    public int SymbolCount { get; }

    /// <summary>
    ///     The wildcard token. Sits one past the model's width because the
    ///     emission provider appends it; see the type remarks.
    /// </summary>
    public int StarId => SymbolCount;

    /// <summary>Emission width once the star column is present.</summary>
    public int WidthWithStar => SymbolCount + 1;

    /// <summary>
    ///     The column this model emits BETWEEN words, or -1 when it has none.
    ///     <para>
    ///         wav2vec2 CTC heads trained on English transcribe
    ///         <c>HELLO|WORLD</c> — the pipe is a real output symbol and the model
    ///         fires it at every word boundary. A token sequence that omits it
    ///         forces those frames onto a blank the model is not predicting, which
    ///         makes each boundary an expensive stretch of the path rather than
    ///         the free one it should be.
    ///     </para>
    ///     <para>
    ///         MMS-FA has no such symbol: it aligns romanized text as an unbroken
    ///         letter stream. So this is -1 there, and every delimiter-aware code
    ///         path is a no-op for the shipping model by construction rather than
    ///         by a flag someone has to remember to set.
    ///     </para>
    /// </summary>
    public int WordDelimiterId { get; private init; } = -1;

    /// <summary>The alignable characters, for diagnostics and tests.</summary>
    public IReadOnlyCollection<char> Characters => _charToId.Keys;

    /// <summary>
    ///     Build from the model's <c>vocab.json</c> flattened to id order.
    ///     <para>
    ///         Single-character entries become alignable characters; everything
    ///         else (<c>&lt;blank&gt;</c>, <c>&lt;pad&gt;</c>, <c>&lt;/s&gt;</c>,
    ///         <c>&lt;unk&gt;</c>) is a control token that text can never map
    ///         onto. The blank is resolved by name — <c>&lt;blank&gt;</c> first,
    ///         then <c>&lt;pad&gt;</c>, which is what most wav2vec2 CTC heads
    ///         call it — and only falls back to index 0 when neither is present.
    ///         Guessing index 0 unconditionally would silently mis-align any
    ///         model that orders its vocabulary differently, and the failure
    ///         would look like poor accuracy rather than a wiring bug.
    ///     </para>
    /// </summary>
    public static LyricVocabulary FromTokens(IReadOnlyList<string> tokensInIdOrder)
    {
        ArgumentNullException.ThrowIfNull(tokensInIdOrder);
        if (tokensInIdOrder.Count == 0)
            throw new ArgumentException("The vocabulary is empty.", nameof(tokensInIdOrder));

        var charToId = new Dictionary<char, int>();
        var blankId = -1;
        var padId = -1;

        for (var id = 0; id < tokensInIdOrder.Count; id++)
        {
            var token = tokensInIdOrder[id];
            if (string.IsNullOrEmpty(token)) continue;

            if (string.Equals(token, "<blank>", StringComparison.Ordinal)) blankId = id;
            else if (string.Equals(token, "<pad>", StringComparison.Ordinal)) padId = id;

            // A one-character entry is alignable text. Control tokens are all
            // bracketed and therefore longer.
            if (token.Length == 1) charToId[char.ToLowerInvariant(token[0])] = id;
        }

        if (blankId < 0) blankId = padId;
        if (blankId < 0) blankId = 0;

        // The word delimiter is a MODEL symbol, not text: it must never be
        // reachable from a lyric sheet, or a stray '|' someone pasted would align
        // as if it were sung. So it is lifted out of the character map.
        var delimiterId = -1;
        if (charToId.Remove('|', out var pipe)) delimiterId = pipe;

        return new LyricVocabulary(charToId, blankId, tokensInIdOrder.Count) { WordDelimiterId = delimiterId };
    }

    /// <summary>Map one already-lower-cased character to its output column.</summary>
    public bool TryGetId(char c, out int id) => _charToId.TryGetValue(c, out id);
}
