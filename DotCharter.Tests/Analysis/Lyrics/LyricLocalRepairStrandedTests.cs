using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     A line's first syllable the letter model never heard, left on the previous line's held
///     voice (or in silence) while the rest of the line is sung after a long silence: it moves to
///     where its line's voice begins, and only then (#627).
/// </summary>
public sealed class LyricLocalRepairStrandedTests
{
    private const double Unheard = double.NaN;

    /// <summary>The previous line's voice runs on from 0.9 to 1.7 s; the line's own voice is 3.0 to 4.0 s.</summary>
    private static readonly (double From, double To)[] Voiced = [(0.9, 1.7), (3.0, 4.0)];

    [Theory]
    [InlineData(1.3, 1.7)]
    [InlineData(1.4, 1.25)]
    public void AnUnheardOpeningLeftBehindBeforeALongSilenceStartsWithItsLinesVoice(double oldStart, double tailEnd)
    {
        var result = Repair("a\nb-c-d", [1.0, oldStart, 3.2, 3.6], [0.01, 0.9, 0.9], [1.0, Unheard, 3.2, 3.6], [(0.9, tailEnd), (3.0, 4.0)]);

        Assert.Equal(3.0, result[1].StartSeconds, 1e-9);
        Assert.Equal(3.2, result[1].EndSeconds, 1e-9);
        Assert.Equal(0.01, result[1].Confidence, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.Repaired));
        Assert.Equal(3.2, result[2].StartSeconds, 1e-9);
        Assert.Equal(3.6, result[3].StartSeconds, 1e-9);
        Assert.False(result[2].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Fact]
    public void AnUnheardNextSyllableOnTheVoiceStartIsPushedOneOnsetLaterToMakeRoom()
    {
        var result = Repair("a\nb-c-d", [1.0, 1.3, 3.01, 3.6], [0.01, 0.01, 0.9], [1.0, Unheard, 3.01, 3.6], Voiced);

        Assert.Equal(3.0, result[1].StartSeconds, 1e-9);
        Assert.Equal(3.02, result[1].EndSeconds, 1e-6);
        Assert.Equal(3.02, result[2].StartSeconds, 1e-6);
        Assert.Equal(3.31, result[2].EndSeconds, 1e-9);
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.Repaired));
        Assert.Equal(3.6, result[3].StartSeconds, 1e-9);
    }

    [Fact]
    public void AConfidentNextSyllableOnTheVoiceStartLeavesNoRoomAndBothAreFlagged()
    {
        var result = Repair("a\nb-c-d", [1.0, 1.3, 3.01, 3.6], [0.01, 0.9, 0.9], [1.0, Unheard, 3.01, 3.6], Voiced);

        Assert.Equal(1.3, result[1].StartSeconds, 1e-9);
        Assert.Equal(3.01, result[2].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.All(result, s => Assert.False(s.ReviewReasons.HasFlag(LyricReviewReason.Repaired)));
    }

    public static TheoryData<string, string, double[], double[], double[], (double, double)[]> Kept => new()
    {
        // Heard where it stands.
        { "heard", "a\nb-c-d", [1.0, 1.3, 3.2, 3.6], [0.01, 0.9, 0.9], [1.0, 1.3, 3.2, 3.6], Voiced },
        // An onset of its own: its voice began after the previous line's last start (a sung pick-up).
        { "own onset", "a\nb-c-d", [1.0, 1.3, 3.2, 3.6], [0.01, 0.9, 0.9], [1.0, Unheard, 3.2, 3.6], [(0.9, 1.1), (1.3, 1.7), (3.0, 4.0)] },
        // The silence is shorter than a gap.
        { "short silence", "a\nb-c-d", [1.0, 1.3, 2.7, 3.1], [0.01, 0.9, 0.9], [1.0, Unheard, 2.7, 3.1], [(0.9, 1.7), (2.5, 4.0)] },
        // Not the line's first syllable.
        { "not first", "a\nd-b-c", [1.0, 1.25, 1.3, 3.2], [0.9, 0.01, 0.9], [1.0, 1.25, Unheard, 3.2], Voiced },
        // Heard by its score (G0).
        { "confident", "a\nb-c-d", [1.0, 1.3, 3.2, 3.6], [0.9, 0.9, 0.9], [1.0, Unheard, 3.2, 3.6], Voiced }
    };

    [Theory]
    [MemberData(nameof(Kept))]
    public void AnOpeningThatFailsOneConditionKeepsItsStart(
        string condition, string text, double[] starts, double[] lineConfidence, double[] peaks, (double, double)[] voiced)
    {
        var result = Repair(text, starts, lineConfidence, peaks, voiced);

        for (var i = 0; i < starts.Length; i++) Assert.True(Math.Abs(starts[i] - result[i].StartSeconds) < 1e-9, $"{condition}: {i} moved to {result[i].StartSeconds}");
    }

    /// <summary>
    ///     The previous line's last syllable 8.00-8.50 s; the opening at 9.00 s on unvoiced frames,
    ///     never heard; the line's voice from 11.00 s with the next syllable at 11.10 s (room for the
    ///     opening: one onset here is 20 ms, so 11.02 s would refuse it for want of room). A faint voice
    ///     of the opening's own at 9.30-9.70 s (its letters only 0.03 there) is a sung pick-up: the
    ///     silence before the line is broken, so the opening stays and is flagged.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AVoiceOfItsOwnBetweenTheOpeningAndTheSilenceKeepsTheOpening(bool pickUp)
    {
        (double, double)[] voiced = pickUp ? [(8.0, 8.5), (9.3, 9.7), (11.0, 12.0)] : [(8.0, 8.5), (11.0, 12.0)];
        var result = Run("a\nb-c", [(8.0, 8.5, 0.9), (9.0, 9.3, 0.01), (11.1, 11.4, 0.9)], [(8.0, 0.94), (9.3, 0.03), (11.1, 0.94)], voiced);

        Assert.Equal(pickUp ? 9.0 : 11.0, result[1].StartSeconds, 1e-9);
        Assert.NotEqual(LyricReviewReason.None, result[1].ReviewReasons);
        Assert.Equal(!pickUp, result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        Assert.Equal(11.1, result[2].StartSeconds, 1e-9);
    }

    /// <summary>
    ///     The previous line's held voice (to 1.7 s) began <paramref name="began" /> s; the previous
    ///     syllable starts at 1.0 s and one onset is 20 ms. Voice that began no later than one onset
    ///     after it is that syllable's: the opening on it moves. Later, it is the opening's own.
    /// </summary>
    [Theory]
    [InlineData(0.99, true)]
    [InlineData(1.01, true)]
    [InlineData(1.03, false)]
    public void VoiceThatBeganWithinOneOnsetOfThePreviousSyllableIsItsTail(double began, bool moves)
    {
        var result = Repair("a\nb-c-d", [1.0, 1.3, 3.2, 3.6], [0.01, 0.9, 0.9], [1.0, Unheard, 3.2, 3.6], [(began, 1.7), (3.0, 4.0)]);

        Assert.Equal(moves ? 3.0 : 1.3, result[1].StartSeconds, 1e-9);
    }

    /// <summary>
    ///     The previous line's last syllable 5.00-5.80 s on voice to 5.80 s; the opening at 6.20 s on
    ///     unvoiced frames, never heard. Its next two syllables stand unheard on unvoiced frames at
    ///     7.90 and 8.00 s, next to a short voice 7.30-7.80 s carrying their letters from 7.50 and
    ///     <paramref name="second" /> s; the rest of the line is heard on voice from 8.40 s. The vowel
    ///     correction (-40 ms, so one onset is 60 ms) moves the second 40 ms earlier: from 7.56 s that
    ///     leaves 20 ms of room after the first, both moves are withdrawn, the next start is back on
    ///     unvoiced frames and the opening has no voice of its line to move to: it stays, flagged.
    ///     From 7.62 s there is room, both moves stand and the opening moves to 7.30 s.
    /// </summary>
    [Theory]
    [InlineData(7.56, false)]
    [InlineData(7.62, true)]
    public void AnOpeningMovedTowardsAStartThatIsWithdrawnLaterGoesBackToItsOldStart(double second, bool moves)
    {
        var result = Run("d\nc-b-a-d-d-d",
            [(5.0, 5.8, 0.9), (6.2, 6.4, 0.02), (7.9, 8.0, 0.02), (8.0, 8.2, 0.02), (8.6, 9.0, 0.9), (9.0, 9.4, 0.9), (9.4, 9.8, 0.9)],
            [(5.0, 0.94), (Unheard, 0), (7.5, 0.94), (second, 0.94), (8.6, 0.94), (9.0, 0.94), (9.4, 0.94)], [(5.0, 5.8), (7.3, 7.8), (8.4, 10.0)],
            LyricAlignmentOptions.Default with { VowelInitialOnsetBiasSeconds = -0.04 });

        Assert.Equal(moves ? 7.3 : 6.2, result[1].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.Equal(moves, result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        Assert.Equal(moves ? 7.5 : 7.9, result[2].StartSeconds, 1e-9);
        Assert.Equal(moves, result[3].StartSeconds < 8.0);
        Assert.Equal(moves ? 7.5 : 6.4, result[1].EndSeconds, 1e-9);
    }

    /// <summary>
    ///     Syllable 0 is heard (score 0.9) and ends at 1.2 s; the others score
    ///     <paramref name="lineConfidence" /> and end 0.3 s after their start (the last 0.2 s).
    ///     Emissions peak for 120 ms from each entry of <paramref name="peaks" />.
    /// </summary>
    private static IReadOnlyList<AlignedSyllable> Repair(
        string text, double[] starts, double[] lineConfidence, double[] peaks, (double From, double To)[] voiced)
    {
        var spans = starts.Select((s, i) => i == 0
            ? (s, 1.2, 0.9)
            : (s, Math.Round(Math.Min(s + 0.3, i + 1 < starts.Length ? starts[i + 1] : s + 0.2), 3), lineConfidence[i - 1])).ToArray();
        return Run(text, spans, peaks.Select(p => (p, 0.94)).ToArray(), voiced);
    }

    /// <summary>
    ///     Each syllable's letters emit at the given strength for 120 ms from the given time (NaN:
    ///     never); under 0.5 the blank still dominates there. The voice is 0.5 where given.
    ///     Default options unless <paramref name="options" /> are given.
    /// </summary>
    internal static IReadOnlyList<AlignedSyllable> Run(
        string text, (double Start, double End, double Confidence)[] spans, (double At, double Strength)[] letters, (double From, double To)[] voiced,
        LyricAlignmentOptions? options = null)
    {
        var sheet = LyricSheetParser.Parse(text);
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);
        const int width = 5;
        const int frames = 700;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var (symbol, strength) = (0, 0.0);
            for (var i = 0; i < letters.Length; i++)
            {
                if (f * 0.02 >= letters[i].At - 1e-9 && f * 0.02 < letters[i].At + 0.12 - 1e-9)
                    (symbol, strength) = (sheet.Syllables[i].Text[0] - 'a' + 1, letters[i].Strength);
            }
            for (var column = 0; column < width; column++)
            {
                var p = symbol == 0 ? column == 0 ? 0.94 : 0.01
                    : column == symbol ? strength
                    : column == 0 && strength < 0.5 ? 0.94 : 0.01;
                probabilities[f * width + column] = (float)Math.Log(p);
            }
        }

        var levels = Enumerable.Range(0, frames * 2)
            .Select(i => voiced.Any(v => i * 0.01 >= v.From - 1e-9 && i * 0.01 < v.To - 1e-9) ? 0.5f : 0f).ToArray();
        var aligned = spans.Select((s, i) => new AlignedSyllable(i, s.Start, s.End, s.Confidence, false)).ToArray();
        return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, frames, width, 0.02),
            aligned, new VocalEnvelope(levels, 0.01), options ?? LyricAlignmentOptions.Default, TestContext.Current.CancellationToken);
    }
}
