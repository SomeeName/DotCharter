using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     After the gate no syllable ends past the next one's start (#627): a kept neighbour's end is
///     cut back to a moved start when one onset of length is left, and otherwise the move is
///     withdrawn, never the kept neighbour moved.
/// </summary>
public sealed class LyricLocalRepairEndTests
{
    /// <summary>
    ///     Syllable 0 heard at 10.00-10.60 s; syllable 1 unheard at 10.60 s, its letters at
    ///     <paramref name="letters" /> s. A vowel correction of <paramref name="vowelBias" /> makes
    ///     one onset 20 ms plus its size.
    /// </summary>
    private static IReadOnlyList<AlignedSyllable> Repair(double letters, double vowelBias = 0) =>
        LyricLocalRepairStrandedTests.Run("a-b", [(10.0, 10.6, 0.9), (10.6, 10.9, 0.01)], [(10.0, 0.94), (letters, 0.94)], [(9.95, 11.0)],
            LyricAlignmentOptions.Default with { VowelInitialOnsetBiasSeconds = vowelBias });

    [Fact]
    public void AKeptNeighbourEndsWhereTheMovedSyllableNowStarts()
    {
        var result = Repair(10.3);

        Assert.InRange(result[1].StartSeconds, 10.28, 10.32);
        Assert.Equal(10.0, result[0].StartSeconds, 1e-9);
        Assert.Equal(0.9, result[0].Confidence, 1e-9);
        Assert.Equal(result[1].StartSeconds, result[0].EndSeconds, 1e-9);
    }

    /// <summary>Syllable 1's letters 20 ms after syllable 0's start, one onset 60 ms: no room to cut syllable 0's end to.</summary>
    [Fact]
    public void AMoveThatLeavesTheKeptNeighbourLessThanOneOnsetIsWithdrawnAndFlagged()
    {
        var result = Repair(10.02, -0.04);

        Assert.Equal(10.6, result[1].StartSeconds, 1e-9);
        Assert.Equal(10.9, result[1].EndSeconds, 1e-9);
        Assert.Equal(10.6, result[0].EndSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.False(result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }
}
