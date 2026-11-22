namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     How well a syllable's own letters are heard at a start: the evidence the repair gate
///     accepts for a new place. The letters split into groups, each run of vowels and each run
///     of consonants; a group scores its strongest letter and the syllable its WEAKEST group.
///     A run is one sound to a letter model (it often emits one letter of "ng" or "ee"), so a
///     group needs only one of its letters; a strong vowel cannot carry a consonant run the
///     model never emitted, which a geometric mean over the letters lets through (0.9 and
///     0.003 give 0.052). The first group must sound at the start itself, one onset either side; the
///     others anywhere from there to the end of the span the syllable is given. Letters heard
///     before <c>notBefore</c> (the previous line's settled end, for a line's opening) belong to
///     the syllable sung there and count for no other.
/// </summary>
internal static class LyricLetterEvidence
{
    internal static double At(LyricRepairPass pass, int index, double start, double span, double notBefore = double.NegativeInfinity)
    {
        var tolerance = pass.SameOnsetSeconds;
        var firstFrameTime = start - pass.OnsetBias(index);
        var from = Math.Max(firstFrameTime - tolerance, notBefore - pass.OnsetBias(index));
        var groups = Groups(pass, index);
        var weakest = 1.0;
        for (var g = 0; g < groups.Count; g++)
        {
            var reach = g == 0 ? tolerance : Math.Max(span, tolerance);
            weakest = Math.Min(weakest, Strongest(pass.Emissions, groups[g], from, firstFrameTime + reach));
        }

        return groups.Count == 0 ? 0 : weakest;
    }

    private static double Strongest(CtcEmissions emissions, List<int> tokens, double from, double to)
    {
        var spf = emissions.SecondsPerFrame;
        var first = Math.Max(0, (int)Math.Ceiling(from / spf - 1e-9));
        var last = Math.Min(emissions.FrameCount - 1, (int)Math.Floor(to / spf + 1e-9));
        var best = 0.0;
        for (var f = first; f <= last; f++)
            foreach (var token in tokens) best = Math.Max(best, Math.Exp(emissions.LogProbs[f * emissions.Width + token]));

        return best;
    }

    private static List<List<int>> Groups(LyricRepairPass pass, int index)
    {
        var map = pass.Tokenization.Syllables[index];
        var groups = new List<List<int>>();
        bool? vowel = null;
        for (var t = map.TokenStart; t < map.TokenStart + map.TokenCount; t++)
        {
            var token = pass.Tokenization.Tokens[t];
            if (pass.IsVowel(token) != vowel) groups.Add([]);
            groups[^1].Add(token);
            vowel = pass.IsVowel(token);
        }

        return groups;
    }
}
