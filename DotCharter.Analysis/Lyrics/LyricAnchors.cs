namespace DotCharter.Analysis.Lyrics;

/// <summary>A lyric word the model clearly heard: its first token starts on <paramref name="Frame" />.</summary>
/// <param name="TokenIndex">Index of the word's first token in <see cref="LyricTokenization.Tokens" />.</param>
/// <param name="Frame">Emission frame the free decode heard that first letter on.</param>
internal readonly record struct LyricAnchor(int TokenIndex, int Frame);

/// <summary>
///     Anchors for <see cref="AnchoredCtcAligner" />: the words the model heard on its own.
///     The emissions are free-decoded (argmax per frame, repeats collapsed, blanks dropped) and
///     the decoded letters are matched to the sheet's letters by a monotonic edit-distance
///     alignment. A word becomes an anchor only when every letter matched exactly and in one
///     run, each was heard with confidence, and the exact-match run around it occurs ONCE in
///     the sheet's letters, so a repeated chorus line anchors only where it differs from its
///     other repeats and never to the wrong one.
/// </summary>
internal static class LyricAnchors
{
    /// <summary>Shorter words match by chance too often to pin a span.</summary>
    internal const int MinWordLetters = 3;

    /// <summary>The exact-match run around an anchor word: long enough to be told apart from other lines.</summary>
    internal const int MinRunLetters = 8;

    /// <summary>Each decoded letter of an anchor word peaks at least this high.</summary>
    internal const double MinLetterProbability = 0.5;

    /// <summary>Refuse the edit-distance table past this many cells; the caller falls back to one path.</summary>
    private const long MaxCells = 80_000_000;

    private const byte Diagonal = 0;
    private const byte SkipLyric = 1;
    private const byte SkipDecoded = 2;

    internal static List<LyricAnchor> Find(
        CtcEmissions emissions, LyricSheet sheet, LyricTokenization tokenization, LyricVocabulary vocabulary, CancellationToken ct)
    {
        var letters = Letters(sheet, tokenization, out var words);
        var decoded = Decode(emissions, vocabulary, ct);
        if (letters.Count == 0 || decoded.Count == 0 || (long)letters.Count * decoded.Count > MaxCells)
            return [];

        var match = Match(letters, decoded, ct);
        var text = new string([.. letters.Select(l => (char)l.Token)]);
        var anchors = new List<LyricAnchor>();
        foreach (var (first, count) in words)
        {
            ct.ThrowIfCancellationRequested();
            if (count < MinWordLetters || !WordHeard(match, decoded, first, count)) continue;

            var from = first;
            while (from > 0 && match[from - 1] >= 0 && match[from - 1] == match[from] - 1) from--;
            var to = first + count;
            while (to < letters.Count && match[to] >= 0 && match[to] == match[to - 1] + 1) to++;
            if (to - from < MinRunLetters) continue;

            var run = text.Substring(from, to - from);
            if (text.IndexOf(run, StringComparison.Ordinal) != from || text.IndexOf(run, from + 1, StringComparison.Ordinal) >= 0) continue;

            anchors.Add(new LyricAnchor(letters[first].TokenIndex, decoded[match[first]].Frame));
        }

        return anchors;
    }

    private static bool WordHeard(int[] match, List<(int Token, int Frame, double Probability)> decoded, int first, int count)
    {
        for (var i = first; i < first + count; i++)
        {
            if (match[i] < 0 || (i > first && match[i] != match[i - 1] + 1)) return false;
            if (decoded[match[i]].Probability < MinLetterProbability) return false;
        }

        return true;
    }

    /// <summary>The sheet's letters in order (stars and word delimiters left out), and each word's letter range.</summary>
    private static List<(int Token, int TokenIndex)> Letters(LyricSheet sheet, LyricTokenization tokenization, out List<(int First, int Count)> words)
    {
        var letters = new List<(int Token, int TokenIndex)>();
        words = [];
        var wordStart = 0;
        foreach (var syllable in tokenization.Syllables)
        {
            for (var t = syllable.TokenStart; t < syllable.TokenStart + syllable.TokenCount; t++) letters.Add((tokenization.Tokens[t], t));
            var source = sheet.Syllables[syllable.SyllableIndex];
            if (source.ContinuesWord && !source.EndsLine) continue;
            if (letters.Count > wordStart) words.Add((wordStart, letters.Count - wordStart));
            wordStart = letters.Count;
        }

        if (letters.Count > wordStart) words.Add((wordStart, letters.Count - wordStart));
        return letters;
    }

    /// <summary>Greedy CTC decode: one entry per run of a letter, at its first frame, with its peak probability.</summary>
    internal static List<(int Token, int Frame, double Probability)> Decode(CtcEmissions emissions, LyricVocabulary vocabulary, CancellationToken ct)
    {
        var decoded = new List<(int Token, int Frame, double Probability)>();
        var previous = -1;
        for (var f = 0; f < emissions.FrameCount; f++)
        {
            if ((f & 1023) == 0) ct.ThrowIfCancellationRequested();
            var row = emissions.LogProbs.AsSpan(f * emissions.Width, emissions.Width);
            var best = 0;
            for (var k = 1; k < row.Length; k++)
                if (row[k] > row[best]) best = k;

            var letter = best != vocabulary.BlankId && best != vocabulary.WordDelimiterId;
            if (letter && best == previous)
                decoded[^1] = decoded[^1] with { Probability = Math.Max(decoded[^1].Probability, Math.Exp(row[best])) };
            else if (letter) decoded.Add((best, f, Math.Exp(row[best])));

            previous = best;
        }

        return decoded;
    }

    /// <summary>Unit-cost edit distance; for each lyric letter the decoded letter it matched EXACTLY, else -1.</summary>
    private static int[] Match(List<(int Token, int TokenIndex)> letters, List<(int Token, int Frame, double Probability)> decoded, CancellationToken ct)
    {
        var n = letters.Count;
        var m = decoded.Count;
        var moves = new byte[(long)(n + 1) * (m + 1)];
        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            previous[j] = j;
            moves[j] = SkipDecoded;
        }

        for (var i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            current[0] = i;
            moves[(long)i * (m + 1)] = SkipLyric;
            for (var j = 1; j <= m; j++)
            {
                var diagonal = previous[j - 1] + (letters[i - 1].Token == decoded[j - 1].Token ? 0 : 1);
                var skipLyric = previous[j] + 1;
                var skipDecoded = current[j - 1] + 1;
                var (cost, move) = diagonal <= skipLyric && diagonal <= skipDecoded ? (diagonal, Diagonal)
                    : skipLyric <= skipDecoded ? (skipLyric, SkipLyric) : (skipDecoded, SkipDecoded);
                current[j] = cost;
                moves[(long)i * (m + 1) + j] = move;
            }

            (previous, current) = (current, previous);
        }

        var match = new int[n];
        Array.Fill(match, -1);
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var move = i == 0 ? SkipDecoded : j == 0 ? SkipLyric : moves[(long)i * (m + 1) + j];
            if (move == Diagonal)
            {
                if (letters[i - 1].Token == decoded[j - 1].Token) match[i - 1] = j - 1;
                i--;
                j--;
            }
            else if (move == SkipLyric)
            {
                i--;
            }
            else
            {
                j--;
            }
        }

        return match;
    }
}
