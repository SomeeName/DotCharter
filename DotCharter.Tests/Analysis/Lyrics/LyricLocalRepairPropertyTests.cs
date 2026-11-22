using System.Globalization;
using System.Text;
using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Generated lines through the local repair, seeded: whatever the rules propose, a heard start
///     never moves and every start that does move is justified by what the GENERATOR put there,
///     its own letters at the new place scoring at least the floor and more than at the old one (a
///     line opening's counted only from where the previous line ends), a
///     displaced opening not heard where it stood, or a line opening not heard where it stood,
///     left on the previous line's voice or in silence before an unbroken gap, moved to its line's
///     voice (with the next syllable pushed one onset on when it lacks room); no syllable ends past
///     the next one's start; and a second run changes no start, end, score or flag. The checks read
///     the generated frames and levels, never the product's predicates: a letter group scores its
///     strongest letter and a syllable its weakest group, and each line's voice is the frames at
///     least 0.15 of the line's 90th-percentile level. A failure names the seed and the case, with
///     the whole generated input, so it can be replayed.
/// </summary>
public sealed class LyricLocalRepairPropertyTests
{
    private const double Frame = 0.02;
    private const double Hop = 0.01;
    private const int Frames = 500;
    private const double Strong = 0.94;
    private const double Background = 0.01;
    private const float Voice = 0.5f;
    private const int CasesPerSeed = 320;

    /// <summary>Cases after the others, of each shape besides <see cref="Shape.Any" />.</summary>
    private const int ShapedCases = 60;
    private const string Alphabet = "abclengstr";
    private static readonly string[] Texts = ["a", "b", "c", "la", "ba", "eng", "see", "str"];

    /// <summary>Letter strengths besides <see cref="Strong" />, on both sides of the floor.</summary>
    private static readonly double[] Weaker = [0.3, 0.06, 0.045, 0.03];

    /// <summary>A second place for the same letters: slightly stronger, as strong, slightly weaker.</summary>
    private static readonly double[] EchoSteps = [0.004, 0, -0.004];

    /// <summary>Levels on both sides of the threshold a line's full voice sets (0.15 x 0.5 = 0.075).</summary>
    private static readonly float[] Levels = [Voice, 0.08f, 0.07f];

    [Theory]
    [InlineData(11, 0)]
    [InlineData(23, 0)]
    [InlineData(37, -0.04)]
    [InlineData(41, -0.04)]
    public void EveryMovedStartIsJustifiedAndARepairedLineRepairsToItself(int seed, double vowelBias)
    {
        var random = new Random(seed);
        var options = LyricAlignmentOptions.Default with { OnsetBiasSeconds = vowelBias / 4, VowelInitialOnsetBiasSeconds = vowelBias };
        var lines = 0;
        var moved = 0;
        var stranded = 0;
        var failures = new List<(string Kind, string Case)>();
        for (var n = 0; n < CasesPerSeed + 3 * ShapedCases; n++)
        {
            var shape = n < CasesPerSeed ? Shape.Any : (Shape)(1 + (n - CasesPerSeed) / ShapedCases);
            var generated = GeneratedLines.Generate(random, options, shape);
            lines += generated.LineCount;
            var once = generated.Run(generated.Aligned, TestContext.Current.CancellationToken);
            var twice = generated.Run(once, TestContext.Current.CancellationToken);
            var problem = generated.Violation(once) ?? Idempotence(once, twice);
            if (problem != null) failures.Add((Kind(problem), $"case {n}: {problem}\n{generated.Describe(once)}"));
            moved += once.Where((s, i) => s.StartSeconds != generated.Aligned[i].StartSeconds).Count();
            stranded += generated.StrandedMoves(once);
        }

        if (failures.Count > 0)
        {
            Assert.Fail($"seed {seed}: {failures.Count} of {CasesPerSeed + 3 * ShapedCases} cases fail\n" + string.Join('\n',
                failures.GroupBy(f => f.Kind).Select(g => $"{g.Count()} x {g.Key}, first {g.First().Case}")));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}: {CasesPerSeed + 3 * ShapedCases} cases, {lines} lines, {moved} moved starts, {stranded} stranded openings moved");
        Assert.True(lines >= 500, $"seed {seed}: only {lines} lines generated");
        Assert.True(moved >= 50, $"seed {seed}: only {moved} starts moved, the property would be vacuous");
        Assert.True(stranded >= 5, $"seed {seed}: only {stranded} stranded openings moved, G3 would go unchecked");
    }

    /// <summary>The broken rule without its numbers, so failures group by rule.</summary>
    private static string Kind(string problem) =>
        string.Concat(problem.Where(c => !char.IsDigit(c) && c is not ('.' or ',' or '-'))).Split(" at ")[0];

    private static string? Idempotence(IReadOnlyList<AlignedSyllable> once, IReadOnlyList<AlignedSyllable> twice)
    {
        for (var i = 0; i < once.Count; i++)
        {
            var (a, b) = (once[i], twice[i]);
            if (a.StartSeconds != b.StartSeconds || a.EndSeconds != b.EndSeconds || a.Confidence != b.Confidence ||
                a.ReviewReasons != b.ReviewReasons)
            {
                var timing = a.StartSeconds != b.StartSeconds || a.EndSeconds != b.EndSeconds || a.Confidence != b.Confidence;
                return string.Create(CultureInfo.InvariantCulture,
                    $"not idempotent ({(timing ? "timing" : "flags only")}) at {i}: {a.StartSeconds:0.###}-{a.EndSeconds:0.###} {a.Confidence:0.###} {a.ReviewReasons} then " +
                    $"{b.StartSeconds:0.###}-{b.EndSeconds:0.###} {b.Confidence:0.###} {b.ReviewReasons}");
            }
        }

        return null;
    }

    /// <summary>
    ///     What a generated line looks like. <c>Any</c>: drawn at random. <c>Early</c>: its opening
    ///     stranded in silence before two syllables sung early (<c>EarlyVoice</c>).
    ///     <c>OwnLetters</c>: a displaced opening whose own letters (0.3) sit inside its voice, later
    ///     than the voice begins and earlier than where it was measured. <c>Boundary</c>: a later
    ///     line's opening without letters, in silence after the previous line's voice runs on past
    ///     that line's end, where the previous line's last syllable sounds its first letter, the
    ///     opening's own, once more; the rest of the line sung later.
    /// </summary>
    private enum Shape
    {
        Any,
        Early,
        OwnLetters,
        Boundary
    }

    private static bool IsVowel(char letter) => "aeiou".Contains(letter);

    private static int Column(char letter) => Alphabet.IndexOf(letter) + 1;

    /// <summary>One generated sheet: its syllables, the frames carrying their letters and the voice levels.</summary>
    private sealed class GeneratedLines
    {
        private readonly LyricAlignmentOptions _options;
        private readonly List<string> _texts = [];
        private readonly List<int> _lineOf = [];
        private readonly List<string> _letters = [];
        private readonly int[] _symbol = new int[Frames];
        private readonly double[] _strength = new double[Frames];
        private readonly List<(double From, double To, float Level)> _regions = [];
        private readonly float[] _levels = new float[Frames * 2];

        private readonly Shape _shape;

        private GeneratedLines(LyricAlignmentOptions options, Shape shape) => (_options, _shape) = (options, shape);

        internal AlignedSyllable[] Aligned { get; private set; } = [];

        internal int LineCount { get; private set; }

        private double Tolerance => Frame + Math.Abs(_options.VowelInitialOnsetBiasSeconds);

        internal static GeneratedLines Generate(Random random, LyricAlignmentOptions options, Shape shape)
        {
            var g = new GeneratedLines(options, shape);
            Array.Fill(g._symbol, -1);
            var aligned = new List<AlignedSyllable>();
            var t = 0.6 + random.NextDouble() * 0.6;
            g.LineCount = shape == Shape.Boundary ? 2 + random.Next(2) : 1 + random.Next(3);
            for (var line = 0; line < g.LineCount && t < 8.0; line++) t = g.AddLine(random, line, t, aligned);
            g.LineCount = g._lineOf.Count == 0 ? 0 : g._lineOf[^1] + 1;
            g.Aligned = [.. aligned];
            for (var i = 0; i < g._levels.Length; i++)
                g._levels[i] = g._regions.Where(v => i * Hop >= v.From - 1e-9 && i * Hop < v.To - 1e-9).Select(v => v.Level).DefaultIfEmpty(0f).Max();
            return g;
        }

        internal IReadOnlyList<AlignedSyllable> Run(IReadOnlyList<AlignedSyllable> input, CancellationToken ct)
        {
            var sheet = LyricSheetParser.Parse(string.Join('\n', Enumerable.Range(0, LineCount)
                .Select(l => string.Join('-', _texts.Where((_, i) => _lineOf[i] == l)))));
            var vocabulary = LyricVocabulary.FromTokens(["<pad>", .. Alphabet.Select(c => c.ToString())]);
            var width = Alphabet.Length + 1;
            var probabilities = new float[Frames * width];
            for (var f = 0; f < Frames; f++)
            {
                for (var column = 0; column < width; column++)
                {
                    var p = _symbol[f] < 0 ? column == 0 ? Strong : Background
                        : column == _symbol[f] ? _strength[f]
                        : column == 0 ? _strength[f] >= 0.5 ? Background : Strong : Background;
                    probabilities[f * width + column] = (float)Math.Log(p);
                }
            }
            return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, Frames, width, Frame),
                input, new VocalEnvelope(_levels, Hop), _options, ct);
        }

        /// <summary>The first broken invariant, or null.</summary>
        internal string? Violation(IReadOnlyList<AlignedSyllable> result)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var (old, now) = (Aligned[i], result[i]);
                var moved = now.StartSeconds != old.StartSeconds;
                if (now.EndSeconds <= now.StartSeconds) return $"{i}: length {now.EndSeconds - now.StartSeconds}";
                if (moved != now.ReviewReasons.HasFlag(LyricReviewReason.Repaired)) return $"{i}: Repaired is {!moved} but the start moved {moved}";
                if (!moved && (now.Confidence != old.Confidence || (now.EndSeconds != old.EndSeconds && !IsCut(i, old, now, result))))
                    return $"{i}: kept start, changed span or score";
                if (!moved) continue;
                if (old.Confidence >= LyricAlignmentReview.LowConfidenceThreshold) return $"{i}: a heard start (G0) moved";
                if (!HasLetterEvidence(i, old, now, result) && !IsDisplaced(i, old, now, result) && !IsStranded(i, old, now, result) && !IsPushed(i, old, now, result))
                    return $"{i}: moved without letters (G1), displacement (G2) or a stranded opening (G3)";
                if (OutOfBounds(i, now, result) is { } bounds) return $"{i}: {bounds}";
            }
            for (var i = 0; i + 1 < result.Count; i++)
            {
                if (_lineOf[i] != _lineOf[i + 1]) continue;

                var anyMoved = result[i].StartSeconds != Aligned[i].StartSeconds || result[i + 1].StartSeconds != Aligned[i + 1].StartSeconds;
                if (result[i + 1].StartSeconds < result[i].StartSeconds || (anyMoved && result[i + 1].StartSeconds <= result[i].StartSeconds))
                    return $"{i}: order broken";
                if (result[i].EndSeconds > result[i + 1].StartSeconds + 1e-9)
                    return string.Create(CultureInfo.InvariantCulture, $"{i}: ends past the next start ({result[i].EndSeconds:0.###} > {result[i + 1].StartSeconds:0.###})");
            }

            return null;
        }

        /// <summary>How many line openings moved as G3 allows.</summary>
        internal int StrandedMoves(IReadOnlyList<AlignedSyllable> result) =>
            Enumerable.Range(0, result.Count).Count(i => result[i].StartSeconds != Aligned[i].StartSeconds && IsStranded(i, Aligned[i], result[i], result));

        internal string Describe(IReadOnlyList<AlignedSyllable> result)
        {
            var text = new StringBuilder();
            var options = string.Create(CultureInfo.InvariantCulture, $"bias {_options.OnsetBiasSeconds} vowel {_options.VowelInitialOnsetBiasSeconds}");
            text.AppendLine(options);
            for (var i = 0; i < Aligned.Length; i++)
            {
                var (a, r) = (Aligned[i], result[i]);
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [{i}] line {_lineOf[i]} '{_texts[i]}' old {a.StartSeconds:0.###}-{a.EndSeconds:0.###} conf {a.Confidence} " +
                    $"letters {_letters[i]} -> {r.StartSeconds:0.###}-{r.EndSeconds:0.###} conf {r.Confidence:0.###} {r.ReviewReasons}"));
            }
            text.Append("  voiced ").AppendJoin(", ", _regions.OrderBy(v => v.From)
                .Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.From:0.##}-{v.To:0.##}@{v.Level:0.##}")));
            return text.ToString();
        }

        private double LetterTime(int frame, int syllable) => frame * Frame + Bias(_texts[syllable]);

        private double Bias(string text) =>
            IsVowel(text[0]) ? _options.OnsetBiasSeconds + _options.VowelInitialOnsetBiasSeconds : _options.OnsetBiasSeconds;

        /// <summary>
        ///     The syllable's letters in the generated frames: each run of vowels and each run of
        ///     consonants scores its strongest letter (one letter of a run is enough), the syllable its
        ///     weakest run. The first run within one onset of <paramref name="start" />, the others from
        ///     there to the end of <paramref name="span" />; <paramref name="slack" /> frames widen (or,
        ///     negative, narrow) the windows. A frame without the letter carries the background; frames
        ///     before <paramref name="notBefore" /> are not looked at.
        /// </summary>
        private double Score(int syllable, double start, double span, int slack, double notBefore = double.NegativeInfinity)
        {
            var text = _texts[syllable];
            var weakest = 1.0;
            for (var j = 0; j < text.Length;)
            {
                var from = Math.Max(start - Tolerance - slack * Frame, notBefore);
                var to = start + (j == 0 ? Tolerance : Math.Max(span, Tolerance)) + slack * Frame;
                var vowel = IsVowel(text[j]);
                var run = new List<int>();
                while (j < text.Length && IsVowel(text[j]) == vowel) run.Add(Column(text[j++]));
                var best = 0.0;
                for (var f = 0; f < Frames; f++)
                {
                    var time = LetterTime(f, syllable);
                    if (time >= from - 1e-9 && time <= to + 1e-9) best = Math.Max(best, run.Contains(_symbol[f]) ? _strength[f] : Background);
                }
                weakest = Math.Min(weakest, best);
            }

            return weakest;
        }

        private bool Heard(int syllable, double start, double span, int slack) =>
            Score(syllable, start, span, slack) >= LyricAlignmentReview.LowConfidenceThreshold;

        /// <summary>G1; a line opening's letters count only from where the previous line ends.</summary>
        private bool HasLetterEvidence(int i, AlignedSyllable old, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result)
        {
            var span = now.EndSeconds - now.StartSeconds;
            var notBefore = i > 0 && _lineOf[i - 1] != _lineOf[i]
                ? Enumerable.Range(0, i).Where(j => _lineOf[j] == _lineOf[i - 1]).Max(j => result[j].EndSeconds)
                : double.NegativeInfinity;
            var there = Score(i, now.StartSeconds, span, 1, notBefore);
            return there >= LyricAlignmentReview.LowConfidenceThreshold && there > Score(i, old.StartSeconds, span, -1);
        }

        /// <summary>A kept syllable's end cut back to the next start of its line, leaving it at least one onset.</summary>
        private bool IsCut(int i, AlignedSyllable old, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result) =>
            i + 1 < result.Count && _lineOf[i + 1] == _lineOf[i] && Math.Abs(now.EndSeconds - result[i + 1].StartSeconds) <= 1e-9 &&
            now.EndSeconds < old.EndSeconds && now.EndSeconds - now.StartSeconds >= Tolerance - 1e-6;

        private bool IsDisplaced(int i, AlignedSyllable old, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result)
        {
            if (i + 1 >= result.Count || _lineOf[i + 1] != _lineOf[i] || _texts[i][0] == _texts[i + 1][0]) return false;

            var threshold = Threshold(i, result);
            var unbroken = Runs(threshold, 2).FirstOrDefault(v => old.StartSeconds >= v.From - Hop && old.StartSeconds < v.To + Hop, (From: -1.0, To: -1.0));
            var next = result[i + 1];
            return unbroken.From >= 0 && Math.Abs(now.StartSeconds - unbroken.From) <= Hop + 1e-6 &&
                   !Heard(i, old.StartSeconds, now.EndSeconds - now.StartSeconds, -1) &&
                   Math.Abs(next.StartSeconds - old.StartSeconds) <= Tolerance + Frame &&
                   Heard(i + 1, old.StartSeconds, next.EndSeconds - next.StartSeconds, 1);
        }

        /// <summary>
        ///     G3 from the generated voice: an unheard line opening on unvoiced frames, or on voice that
        ///     began no later than one onset after the previous syllable, now where its line's voice
        ///     begins, with no voice at all between its old place and there for at least a gap.
        /// </summary>
        private bool IsStranded(int i, AlignedSyllable old, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result)
        {
            if ((i > 0 && _lineOf[i - 1] == _lineOf[i]) || i + 1 >= result.Count || _lineOf[i + 1] != _lineOf[i]) return false;
            if (Heard(i, old.StartSeconds, now.EndSeconds - now.StartSeconds, -1)) return false;

            var threshold = Threshold(i, result);
            var silenceFrom = old.StartSeconds;
            if (Chain(old.StartSeconds, threshold) is { } here)
            {
                if (i == 0 || here.From > result[i - 1].StartSeconds + Tolerance + Hop) return false;
                silenceFrom = here.To;
            }
            var next = result[i + 1];
            return Chain(next.StartSeconds, threshold) is { } voice && Math.Abs(now.StartSeconds - voice.From) <= Hop + 1e-6 &&
                   Math.Abs(now.EndSeconds - next.StartSeconds) <= 1e-6 &&
                   voice.From - silenceFrom >= LyricAlignmentReview.MaxBlankGapSeconds - 2 * Hop &&
                   !Runs(threshold, Rest).Any(v => v.To > silenceFrom + Hop && v.From < voice.From - Hop);
        }

        /// <summary>G3's push: the syllable after a moved line opening, unheard by score, now one onset after it.</summary>
        private bool IsPushed(int i, AlignedSyllable old, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result) =>
            i > 0 && _lineOf[i - 1] == _lineOf[i] && result[i - 1].StartSeconds != Aligned[i - 1].StartSeconds &&
            IsStranded(i - 1, Aligned[i - 1], result[i - 1], result) && old.StartSeconds < now.StartSeconds &&
            Math.Abs(now.StartSeconds - result[i - 1].StartSeconds - Tolerance) <= 1e-6;

        private static int Rest => (int)Math.Ceiling(LyricVocalRegions.MinRestSeconds / Hop - 1e-9);

        /// <summary>The voice holding a time (an onset may lead it), whole runs split only by a rest.</summary>
        private (double From, double To)? Chain(double seconds, double threshold)
        {
            foreach (var v in Runs(threshold, Rest))
                if (seconds >= v.From - LyricVocalRegions.OnsetLeadSeconds - Hop && seconds < v.To + Hop) return v;

            return null;
        }

        /// <summary>Line <paramref name="i" />'s voice threshold: 0.15 of the 90th-percentile level between its neighbours.</summary>
        private double Threshold(int i, IReadOnlyList<AlignedSyllable> result)
        {
            var first = i;
            while (first > 0 && _lineOf[first - 1] == _lineOf[i]) first--;
            var next = Enumerable.Range(i, result.Count - i).FirstOrDefault(j => _lineOf[j] != _lineOf[i], -1);
            var before = first > 0 ? result[first - 1].EndSeconds : 0;
            var after = next >= 0 ? Aligned[next].StartSeconds : Frames * Frame;
            var (from, to) = (FrameOf(before), FrameOf(after));
            var window = _levels[from..(to + 1)].Order().ToArray();
            return Math.Max(0.002, window[(int)Math.Clamp(0.9 * (window.Length - 1), 0, window.Length - 1)] * 0.15);
        }

        private int FrameOf(double seconds) => Math.Clamp((int)Math.Round(seconds / Hop), 0, _levels.Length - 1);

        /// <summary>Stretches at or over the threshold, split where it stays under for <paramref name="quiet" /> frames.</summary>
        private List<(double From, double To)> Runs(double threshold, int quiet)
        {
            var runs = new List<(double From, double To)>();
            var (start, under) = (-1, 0);
            for (var f = 0; f <= _levels.Length; f++)
            {
                if (f < _levels.Length && _levels[f] >= threshold)
                {
                    if (start < 0) start = f;
                    under = 0;
                }
                else if (start >= 0 && (++under >= quiet || f == _levels.Length))
                {
                    runs.Add((start * Hop, (f - under + 1) * Hop));
                    start = -1;
                }
            }

            return runs;
        }

        private string? OutOfBounds(int i, AlignedSyllable now, IReadOnlyList<AlignedSyllable> result)
        {
            var previous = Enumerable.Range(0, result.Count).Where(j => _lineOf[j] == _lineOf[i] - 1).Select(j => result[j].EndSeconds).DefaultIfEmpty(0).Max();
            var nextLine = Enumerable.Range(0, result.Count).FirstOrDefault(j => _lineOf[j] == _lineOf[i] + 1, -1);
            var upper = nextLine >= 0 ? result[nextLine].StartSeconds : Frames * Frame;
            if (now.StartSeconds < previous - 1e-9) return $"starts before the previous line ends ({previous:0.###})";
            return now.EndSeconds > upper + 1e-9 ? $"ends after the next line starts ({upper:0.###})" : null;
        }

        /// <summary>Adds one line of 1 to 8 syllables at <paramref name="t" />; returns where the next line may begin.</summary>
        private double AddLine(Random random, int line, double t, List<AlignedSyllable> aligned)
        {
            var count = _shape == Shape.Early ? 6 + random.Next(3) : 1 + random.Next(8);
            var first = _texts.Count;
            var places = new List<double>();
            for (var k = 0; k < count && t < 9.0; k++)
            {
                places.Add(t);
                _texts.Add(Texts[random.Next(Texts.Length)]);
                _lineOf.Add(line);
                t += 0.14 + random.NextDouble() * 0.31 + (random.NextDouble() < 0.15 ? 0.3 + random.NextDouble() * 0.5 : 0);
            }
            if (places.Count == 0) return t;

            var boundary = _shape == Shape.Boundary && first > 0 && places.Count > 1;
            if (boundary)
            {
                _texts[first] = _texts[first - 1][..1];
                for (var k = 1; k < places.Count; k++) places[k] += 0.8;
            }

            var displaced = _shape == Shape.OwnLetters ? places.Count > 1 : _shape == Shape.Any && places.Count > 1 && random.NextDouble() < 0.25;
            var stranded = Strand(random, places, first, displaced || boundary, _shape == Shape.Early);
            if (stranded is { } shift) t += shift.Delta;
            var early = _shape == Shape.Early && stranded is { Kind: 0, Syllable: 0 } e && places.Count >= 6
                ? EarlyVoice(places, first, places[0] + e.Gap)
                : ((double From, double To, double Shift)?)null;
            if (early is { } moved) t += moved.Shift;
            if (displaced && random.NextDouble() < 0.5) _texts[first + 1] = _texts[first][0] == 'a' ? "a" : _texts[first][0] + "a";
            var starts = new double[places.Count];
            for (var k = 0; k < places.Count; k++)
            {
                var frame = (int)Math.Round(places[k] / Frame);
                var mode = ((displaced || boundary) && k == 0) || stranded?.Syllable == k ? 1 : early != null && k is 1 or 2 ? 7 : random.Next(7);
                PlaceLetters(random, first + k, frame, mode);
                var jitter = early != null || random.NextDouble() < 0.5 || stranded?.Syllable == k ? 0 : (random.NextDouble() - 0.5) * 0.8;
                starts[k] = LetterTime(frame, first + k) + jitter;
            }
            if (early is { } voice) (starts[1], starts[2]) = (voice.To + 0.1, voice.To + 0.2);
            if (displaced && _shape == Shape.OwnLetters)
            {
                var own = (int)Math.Round(places[0] / Frame);
                Put(_texts[first], own, 0.3, null);
                _letters[first] = $"late@{own}x0.3";
            }
            if (displaced) (starts[0], starts[1]) = (LetterTime((int)Math.Round(places[1] / Frame), first + 1), starts[1] + 0.1 + random.NextDouble() * 0.2);
            for (var k = 0; k < starts.Length; k++)
            {
                starts[k] = Math.Round(Math.Max(starts[k], k == 0 ? (aligned.Count > 0 ? aligned[^1].EndSeconds + 0.05 : 0.1) : starts[k - 1] + 0.03), 3);
            }
            double[] confidences = [0.9, 0.3, 0.06, 0.049, 0.03, 0.01];
            for (var k = 0; k < starts.Length; k++)
            {
                var limit = k + 1 < starts.Length ? starts[k + 1] : starts[k] + 1.4;
                var end = Math.Min(Math.Min(limit, 9.99), starts[k] + 0.02 + random.NextDouble() * (random.NextDouble() < 0.2 ? 1.2 : 0.3));
                var confidence = ((displaced || boundary) && k == 0) || (early != null && k is 1 or 2) || (stranded?.Syllable == k && random.NextDouble() < 0.75) ? 0.01 : confidences[random.Next(confidences.Length)];
                aligned.Add(new AlignedSyllable(first + k, starts[k], Math.Round(Math.Max(end, starts[k] + 0.02), 3), confidence, false));
            }
            if (stranded is { } s) AddStrandedVoice(s, places, first > 0 ? aligned[first - 1].StartSeconds : null, aligned[^1].EndSeconds, early, random);
            else if (!boundary) AddVoice(random, places, displaced, aligned[^1].EndSeconds);
            if (boundary)
            {
                _regions.Add((aligned[first + 1].StartSeconds - 0.1, aligned[^1].EndSeconds + 0.2, Voice));
                var previousEnd = aligned[first - 1].EndSeconds;
                var shared = (int)Math.Round(previousEnd / Frame) - 2;
                Put(_texts[first], shared, Strong, null);
                _letters[first] = $"missing, previous line's @{shared}";
                _regions.Add((previousEnd - 0.2, aligned[first].StartSeconds - 0.1, Voice));
            }
            return Math.Max(t, aligned[^1].EndSeconds) + 0.2 + random.NextDouble() * 1.2;
        }

        /// <summary>
        ///     Leaves one syllable (the line's first, or one inside it) unheard, in four lines of ten,
        ///     before a silence on both sides of a gap: the places after it move so that its next one
        ///     is sung that long after its voice (or, in silence, its start).
        /// </summary>
        private static (int Syllable, int Kind, double Gap, double Delta)? Strand(Random random, List<double> places, int first, bool displaced, bool early)
        {
            if (displaced || places.Count < 2 || (!early && random.NextDouble() >= 0.4)) return null;

            var kind = early ? 0 : random.Next(4);
            if (kind == 1 && first == 0) kind = 0;
            var syllable = kind == 3 && places.Count > 2 ? 1 + random.Next(places.Count - 2) : 0;
            var gap = !early && random.NextDouble() < 0.5 ? 0.6 + random.NextDouble() * 0.3 : 1.1 + random.NextDouble() * 0.5;
            var voiceEnd = places[syllable] + (kind == 0 ? 0 : 0.2);
            var delta = voiceEnd + gap + random.NextDouble() * 0.08 - places[syllable + 1];
            for (var k = syllable + 1; k < places.Count; k++) places[k] += delta;
            return (syllable, kind, gap, delta);
        }

        /// <summary>
        ///     After an opening stranded in silence, its next two syllables sung on a short voice of their
        ///     own from <paramref name="from" />, one consonant and right after it one vowel (under a vowel
        ///     correction the second then starts inside the first), but measured unheard on unvoiced frames
        ///     just after that voice; the rest of the line follows on its own voice. Returns the short voice
        ///     and how far the rest moved.
        /// </summary>
        private (double From, double To, double Shift) EarlyVoice(List<double> places, int first, double from)
        {
            (_texts[first + 1], _texts[first + 2]) = ("b", "a");
            // Room before them on that voice for the opening's own letters, which the forced path places there too.
            places[1] = Math.Max(places[1], from + 0.2);
            places[2] = places[1] + 3 * Frame;
            var to = places[2] + 0.16;
            var shift = Math.Max(0, to + 0.7 - places[3]);
            for (var k = 3; k < places.Count; k++) places[k] += shift;
            return (from, to, shift);
        }

        /// <summary>
        ///     Kind 0: the stranded syllable in silence; 1: on the previous line's voice, which began
        ///     well before, just around or after the previous syllable's start, sometimes behind a
        ///     lead-in near the threshold; 2: on a pick-up of its own; 3: inside its line. Kinds 0 and
        ///     1 sometimes get a short voice of their own inside the silence.
        /// </summary>
        private void AddStrandedVoice(
            (int Syllable, int Kind, double Gap, double Delta) stranded, List<double> places, double? previousStart, double lineEnd,
            (double From, double To, double Shift)? early, Random random)
        {
            var at = places[stranded.Syllable];
            if (stranded.Kind == 1 && previousStart is { } p)
            {
                double[] offsets = [-0.3, Tolerance - 0.01, Tolerance + 0.01, 0.15];
                var began = Math.Min(p + offsets[random.Next(offsets.Length)], at - 0.05);
                if (random.NextDouble() < 0.4) _regions.Add((p - 0.3, began, Levels[1 + random.Next(2)]));
                _regions.Add((began, at + 0.2, Voice));
            }
            if (stranded.Kind == 2) _regions.Add((at - 0.03, at + 0.2, Voice));
            if (stranded.Kind == 3) _regions.Add((places[0] - 0.2, at + 0.2, Voice));
            var silenceFrom = stranded.Kind == 0 ? at : at + 0.2;
            var resume = silenceFrom + stranded.Gap;
            if (stranded.Kind <= 1 && early == null && random.NextDouble() < 0.35)
            {
                var from = silenceFrom + 0.2 + random.NextDouble() * 0.2;
                var to = Math.Min(from + 0.1 + random.NextDouble() * 0.15, resume - 0.2);
                if (to - from >= 0.05) _regions.Add((from, to, Levels[random.Next(Levels.Length)]));
            }
            var end = Math.Max(lineEnd, places[^1]) + 0.1 + random.NextDouble() * 0.4;
            if (early is { } voice)
            {
                _regions.Add((voice.From, voice.To, Voice));
                _regions.Add((places[3] - 0.1, end, Voice));
                return;
            }

            _regions.Add((resume, end, Voice));
        }

        /// <summary>
        ///     Mode 0 and 4: letters where sung; 1: missing; 2: one faint first letter; 3: letters
        ///     shifted; 5: only some letters of each run; 6: the letters again at a second place,
        ///     slightly stronger, as strong or slightly weaker; 7: letters where sung, strong. Strength
        ///     mostly strong, else on either side of the floor.
        /// </summary>
        private void PlaceLetters(Random random, int syllable, int frame, int mode)
        {
            var text = _texts[syllable];
            var at = mode == 3 ? frame + (frame % 7 - 3) * 4 : frame;
            var strength = mode == 7 || random.NextDouble() < 0.6 ? Strong : Weaker[random.Next(Weaker.Length)];
            var echo = at + (random.Next(2) == 0 ? -1 : 1) * (8 + random.Next(15));
            var echoStrength = strength + EchoSteps[random.Next(EchoSteps.Length)];
            _letters.Add(mode switch
            {
                1 => "missing",
                2 => $"faint@{at}",
                5 => string.Create(CultureInfo.InvariantCulture, $"some@{at}x{strength}"),
                6 => string.Create(CultureInfo.InvariantCulture, $"@{at}x{strength}+@{echo}x{echoStrength}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"@{at}x{strength}")
            });
            if (mode == 1) return;
            if (mode == 2)
            {
                Put(text[..1], at, 0.03, null);
                return;
            }

            Put(text, at, strength, mode == 5 ? random : null);
            if (mode == 6) Put(text, echo, echoStrength, null);
        }

        /// <summary>Each letter for three frames from <paramref name="at" />; with <paramref name="some" />, each letter only half the time.</summary>
        private void Put(string text, int at, double strength, Random? some)
        {
            for (var j = 0; j < text.Length; j++)
            {
                if (some != null && some.NextDouble() < 0.5) continue;

                for (var f = at + 3 * j; f < at + 3 * j + 3 && f >= 0 && f < Frames; f++)
                {
                    if (strength < 0.5 && _strength[f] >= Strong) continue;

                    _symbol[f] = Column(text[j]);
                    _strength[f] = strength;
                }
            }
        }

        /// <summary>0 to 4 regions: the line's voice with a rest inside, a breath before it, voice into the next line, or random blobs, some near the threshold.</summary>
        private void AddVoice(Random random, List<double> places, bool displaced, double lineEnd)
        {
            var kind = random.Next(4);
            if (kind == 0) return;
            if (kind == 3)
            {
                for (var n = random.Next(1, 5); n > 0; n--)
                {
                    var from = places[0] - 0.8 + random.NextDouble() * (lineEnd - places[0] + 1.3);
                    _regions.Add((from, from + 0.1 + random.NextDouble() * 1.1, Levels[random.Next(Levels.Length)]));
                }
                return;
            }

            var start = places[0] - (displaced ? 0.3 + random.NextDouble() * 0.4 : random.NextDouble() * 0.5);
            var end = places[^1] + 0.1 + random.NextDouble() * (kind == 2 ? 1.2 : 0.4);
            var rest = places.Count > 2 && random.NextDouble() < 0.4 ? 1 + random.Next(places.Count - 2) : -1;
            if (rest > 0 && places[rest + 1] - places[rest] > 0.35)
            {
                _regions.Add((start, places[rest] + 0.12, Voice));
                _regions.Add((places[rest + 1] - random.NextDouble() * 0.05, end, Voice));
            }
            else
            {
                _regions.Add((start, end, Voice));
            }
            if (random.NextDouble() < 0.3) _regions.Add((start - 0.9 + random.NextDouble() * 0.2, start - 0.5, Levels[random.Next(Levels.Length)]));
        }
    }
}
