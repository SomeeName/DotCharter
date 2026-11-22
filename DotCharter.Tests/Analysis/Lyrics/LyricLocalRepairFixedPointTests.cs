using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     One run of the local repair is its own fixed point: fed its result, a second run moves no
///     start and changes no end, score or flag. Frozen inputs in the property test's frame
///     semantics (20 ms letter frames, each letter for three frames; 10 ms voice hops), onset bias
///     -0.01 s and vowel correction -0.04 s unless a test says otherwise (#627).
/// </summary>
public sealed class LyricLocalRepairFixedPointTests
{
    private const string Alphabet = "abclengstr";
    private const int Frames = 500;
    private const double Frame = 0.02;
    private const double Hop = 0.01;

    /// <summary>
    ///     Line 1's unheard opening `a` stands at 3.789 s, on the next syllable's letters, while its
    ///     own letters (0.3) sit at frame 173, inside the span the displacement would give it from
    ///     its voice's start (3.24 s): it goes to its letters, not to the voice start.
    /// </summary>
    [Fact]
    public void ADisplacedOpeningWhoseOwnLettersLieInsideItsSpanGoesToItsLetters()
    {
        var input = new Input("see-str\na-b-c-eng-b",
            [(0.65, 0.949, 0.01), (1.49, 1.612, 0.01), (3.789, 3.819, 0.01), (3.819, 3.985, 0.06), (4.03, 4.141, 0.3), (4.156, 4.483, 0.049), (4.53, 5.622, 0.049)],
            [(2, 173, 0.3, 1), (3, 191, 0.94, 1), (3, 205, 0.944, 1), (4, 214, 0.94, 1), (5, 214, 0.94, 3), (5, 222, 0.936, 3), (6, 227, 0.94, 1)],
            [(1.43, 2.03, 0.5f), (2.83, 2.96, 0.08f), (3.24, 4.41, 0.5f), (5.28, 6.31, 0.08f)]);

        var (once, twice) = input.RunTwice();

        AssertSame(once, twice);
        Assert.InRange(once[2].StartSeconds, 173 * Frame - 0.05 - 0.06, 173 * Frame - 0.05 + 0.06);
        Assert.True(once[2].Confidence >= LyricAlignmentReview.LowConfidenceThreshold);
        Assert.True(once[2].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    /// <summary>
    ///     Line 1's opening `b` has no letters of its own; its line's search reaches back onto the
    ///     tail of line 0's voice, where line 0's own `b` (frame 197) is heard just before line 0's
    ///     last syllable ends (4.007 s). Those letters are line 0's: the opening keeps 5.23 s, and
    ///     line 0 is repaired against the same next start on every run.
    /// </summary>
    [Fact]
    public void ALineOpeningIsNoOwnerOfThePreviousLinesLetters()
    {
        var input = new Input("see-b-a-ba-a-b-eng\nb-b-a-b-see-la",
            [
                (0.65, 0.814, 0.03), (2.278, 2.378, 0.01), (2.378, 2.424, 0.01), (3.367, 3.397, 0.049), (3.397, 3.704, 0.3), (3.704, 3.928, 0.03),
                (3.972, 4.007, 0.3), (5.23, 5.384, 0.01), (6.967, 7.067, 0.01), (7.067, 7.16, 0.01), (7.55, 8.13, 0.3), (8.13, 8.222, 0.9), (8.53, 8.716, 0.06)
            ],
            [
                (1, 98, 0.045, 1), (2, 101, 0.94, 1), (3, 164, 0.3, 2), (4, 173, 0.03, 1), (5, 197, 0.94, 1), (6, 212, 0.03, 1), (8, 332, 0.94, 1),
                (9, 335, 0.94, 1), (10, 378, 0.94, 1), (10, 388, 0.936, 1), (11, 407, 0.94, 3), (11, 387, 0.936, 3), (12, 427, 0.3, 2)
            ],
            [(1.94, 2.18, 0.5f), (3.19, 4.71, 0.5f), (6.63, 6.87, 0.5f), (7.47, 8.93, 0.5f)]);

        var (once, twice) = input.RunTwice();

        AssertSame(once, twice);
        Assert.Equal(5.23, once[7].StartSeconds, 1e-9);
        Assert.Equal(0.01, once[7].Confidence, 1e-9);
    }

    /// <summary>
    ///     Line 1's opening `c` has no letters and stands in silence before a long gap, so G3 would
    ///     place it where its line's voice begins (4.75 s, to 4.848 s). A `c` heard at 0.3 inside that
    ///     span (frame 240) would prove a place of its own on the next run: G3 is refused, the
    ///     opening keeps 3.58 s and is flagged. No onset bias.
    /// </summary>
    [Fact]
    public void AStrandedOpeningWithItsLettersHeardInsideTheSpanItWouldGetStays()
    {
        var input = new Input("see-c\nc-ba-c-c\nc-a-b",
            [
                (0.78, 0.93, 0.01), (2.28, 2.53, 0.049), (3.58, 3.693, 0.01), (4.848, 4.878, 0.3), (4.878, 5.361, 0.01), (5.361, 5.605, 0.06),
                (5.77, 5.913, 0.01), (7.12, 7.242, 0.06), (7.558, 7.579, 0.06)
            ],
            [(1, 114, 0.94, 1), (3, 242, 0.045, 2), (4, 240, 0.3, 1), (5, 270, 0.94, 1), (2, 124, 0.94, 1), (8, 370, 0.94, 1), (8, 386, 0.944, 1), (6, 278, 0.94, 1)],
            [(2.22, 3.02, 0.5f), (2.33, 3.48, 0.5f), (4.75, 5.81, 0.5f), (5.41, 5.67, 0.5f), (7.02, 7.78, 0.5f)])
        {
            Options = LyricAlignmentOptions.Default with { OnsetBiasSeconds = 0, VowelInitialOnsetBiasSeconds = 0 }
        };

        var (once, twice) = input.RunTwice();

        AssertSame(once, twice);
        Assert.Equal(3.58, once[2].StartSeconds, 1e-9);
        Assert.True(once[2].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    private static void AssertSame(IReadOnlyList<AlignedSyllable> once, IReadOnlyList<AlignedSyllable> twice)
    {
        for (var i = 0; i < once.Count; i++)
        {
            Assert.True(once[i].StartSeconds == twice[i].StartSeconds && once[i].EndSeconds == twice[i].EndSeconds &&
                        once[i].Confidence == twice[i].Confidence && once[i].ReviewReasons == twice[i].ReviewReasons,
                $"syllable {i}: {once[i]} then {twice[i]}");
        }
    }

    /// <summary>
    ///     A sheet, its measured spans, letters (syllable, first frame, strength, how many of its
    ///     letters, each for three frames) and voice regions with their levels.
    /// </summary>
    private sealed record Input(
        string Text, (double Start, double End, double Confidence)[] Spans, (int Syllable, int At, double Strength, int Count)[] Letters,
        (double From, double To, float Level)[] Voice)
    {
        internal LyricAlignmentOptions Options { get; init; } =
            LyricAlignmentOptions.Default with { OnsetBiasSeconds = -0.01, VowelInitialOnsetBiasSeconds = -0.04 };

        internal (IReadOnlyList<AlignedSyllable> Once, IReadOnlyList<AlignedSyllable> Twice) RunTwice()
        {
            var sheet = LyricSheetParser.Parse(Text);
            var symbol = Enumerable.Repeat(-1, Frames).ToArray();
            var strength = new double[Frames];
            foreach (var (syllable, at, value, count) in Letters)
            {
                var text = sheet.Syllables[syllable].Text;
                for (var j = 0; j < count; j++)
                {
                    for (var f = at + 3 * j; f < at + 3 * j + 3; f++)
                    {
                        if (value < 0.5 && strength[f] >= 0.94) continue;

                        (symbol[f], strength[f]) = (Alphabet.IndexOf(text[j]) + 1, value);
                    }
                }
            }

            const int width = 11;
            var probabilities = new float[Frames * width];
            for (var f = 0; f < Frames; f++)
            {
                for (var column = 0; column < width; column++)
                {
                    var p = symbol[f] < 0 ? column == 0 ? 0.94 : 0.01
                        : column == symbol[f] ? strength[f]
                        : column == 0 ? strength[f] >= 0.5 ? 0.01 : 0.94 : 0.01;
                    probabilities[f * width + column] = (float)Math.Log(p);
                }
            }

            var levels = Enumerable.Range(0, Frames * 2)
                .Select(i => Voice.Where(v => i * Hop >= v.From - 1e-9 && i * Hop < v.To - 1e-9).Select(v => v.Level).DefaultIfEmpty(0f).Max()).ToArray();
            var vocabulary = LyricVocabulary.FromTokens(["<pad>", .. Alphabet.Select(c => c.ToString())]);
            var emissions = new CtcEmissions(probabilities, Frames, width, Frame);
            var envelope = new VocalEnvelope(levels, Hop);
            var aligned = Spans.Select((s, i) => new AlignedSyllable(i, s.Start, s.End, s.Confidence, false)).ToArray();
            var ct = TestContext.Current.CancellationToken;
            var once = LyricLocalRepair.Repair(sheet, vocabulary, emissions, aligned, envelope, Options, ct);
            return (once, LyricLocalRepair.Repair(sheet, vocabulary, emissions, once, envelope, Options, ct));
        }
    }
}
