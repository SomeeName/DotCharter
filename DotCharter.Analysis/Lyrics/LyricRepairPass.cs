using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     What one local repair pass reads and writes: <c>Aligned</c> holds the measured spans,
///     <c>Result</c> and <c>Reasons</c> the proposals and flags, updated in place line by line.
/// </summary>
internal sealed record LyricRepairPass(
    LyricSheet Sheet, LyricVocabulary Vocabulary, CtcEmissions Emissions, LyricTokenization Tokenization,
    IReadOnlyList<AlignedSyllable> Aligned, AlignedSyllable[] Result, LyricReviewReason[] Reasons,
    VocalEnvelope Envelope, LyricAlignmentOptions Options, CancellationToken CancellationToken)
{
    private readonly HashSet<int> _vowels = VowelTokens(Vocabulary);

    /// <summary>
    ///     A line opening proposed behind its voice: the place its line's own alignment gave it
    ///     instead, where the gate looks for its letters before it takes the displacement.
    /// </summary>
    internal Dictionary<int, AlignedSyllable> OwnPlaces { get; } = [];

    /// <summary>Two onsets this close are one onset: one emission frame plus the model's vowel correction.</summary>
    internal double SameOnsetSeconds => Emissions.SecondsPerFrame + Math.Abs(Options.VowelInitialOnsetBiasSeconds) + 1e-9;

    /// <summary>What the model's onset corrections add to the time of a syllable's first letter frame.</summary>
    internal double OnsetBias(int index) =>
        SyllableOnsetClassifier.Classify(Sheet.Syllables[index].Text) == SyllableOnsetClass.Vowel
            ? Options.OnsetBiasSeconds + Options.VowelInitialOnsetBiasSeconds
            : Options.OnsetBiasSeconds;

    /// <summary>Whether a model column is a vowel letter.</summary>
    internal bool IsVowel(int token) => _vowels.Contains(token);

    internal static HashSet<int> VowelTokens(LyricVocabulary vocabulary)
    {
        var vowels = new HashSet<int>();
        foreach (var c in vocabulary.Characters)
        {
            if (char.IsLetter(c) && SyllableOnsetClassifier.Classify(c.ToString()) == SyllableOnsetClass.Vowel &&
                vocabulary.TryGetId(c, out var id))
            {
                vowels.Add(id);
            }
        }

        return vowels;
    }
}
