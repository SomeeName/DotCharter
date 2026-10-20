using static DotCharter.Tone.UI.Controls.Dsp.DspModelGrouping;

namespace DotCharter.Tests.UI;

/// <summary>
/// The pure amp/cab model-family grouping (ABI v24 gear roster) behind the model
/// pickers. The LABEL strings come from the native ABI; only this index table is
/// under test — pin the roster size + the signature members so a stale table (a
/// native reorder) is caught. No Avalonia instance.
/// </summary>
public sealed class DspModelGroupingTests
{
    [Fact]
    public void AmpRoster_CoversExactly27_WithNoOtherFallbackInRange()
    {
        Assert.Equal(27, AmpVoicingCount);
        for (var i = 0; i < AmpVoicingCount; i++)
            Assert.NotEqual(AmpModelGroup.Other, AmpVoicingGroup(i)); // every roster index is grouped
    }

    [Theory]
    [InlineData(0, AmpModelGroup.American)]   // Blackface Twin 85
    [InlineData(7, AmpModelGroup.American)]   // Tweed Champ 5W
    [InlineData(1, AmpModelGroup.British)]    // Plexi 50
    [InlineData(10, AmpModelGroup.British)]   // Plexi 100
    [InlineData(11, AmpModelGroup.British)]   // JMP-800
    [InlineData(3, AmpModelGroup.British)]    // AC-30
    [InlineData(4, AmpModelGroup.HiGain)]     // Recto Dual
    [InlineData(15, AmpModelGroup.HiGain)]    // Modern Djent
    [InlineData(17, AmpModelGroup.Boutique)]  // Overdrive Special (Dumble)
    [InlineData(19, AmpModelGroup.Bass)]      // Bass Three-Sixty
    [InlineData(21, AmpModelGroup.Bass)]      // SVT Bass 300W
    [InlineData(22, AmpModelGroup.Di)]        // Acoustic Pre
    [InlineData(23, AmpModelGroup.Di)]        // Studio Direct
    [InlineData(24, AmpModelGroup.American)]  // Tweed Bass 45 (circuit)
    [InlineData(25, AmpModelGroup.British)]   // Plexi 50 Circuit
    [InlineData(26, AmpModelGroup.British)]   // Plexi 100 Circuit
    public void AmpVoicingGroup_MapsSignatureModels(int index, AmpModelGroup expected)
        => Assert.Equal(expected, AmpVoicingGroup(index));

    [Fact]
    public void AmpVoicingGroup_OutOfRange_IsOther()
    {
        Assert.Equal(AmpModelGroup.Other, AmpVoicingGroup(-1));
        Assert.Equal(AmpModelGroup.Other, AmpVoicingGroup(27));
        Assert.Equal(AmpModelGroup.Other, AmpVoicingGroup(999));
    }

    [Fact]
    public void AmpGroupOrder_IsComplete_AndEveryGroupHasAResourceKey()
    {
        // Every group used by the roster appears in the display order.
        for (var i = 0; i < AmpVoicingCount; i++)
            Assert.Contains(AmpVoicingGroup(i), AmpGroupOrder);

        foreach (var g in AmpGroupOrder)
            Assert.False(string.IsNullOrEmpty(AmpGroupResourceKey(g)));
    }

    [Theory]
    [InlineData(0, false)]  // 1x12 Combo
    [InlineData(8, false)]  // 4x12 V30 Modern
    [InlineData(9, true)]   // 4x10 Bass
    [InlineData(11, true)]  // 1x15 Vintage Bass
    [InlineData(12, false)] // 4x10 Tweed Open (#74)
    [InlineData(14, false)] // 2x15 Guitar
    [InlineData(15, false)] // PA 15 + Horn — full range, not bass
    [InlineData(18, true)]  // 2x10 Bass
    [InlineData(25, true)]  // 1x15 + 1x10 Bass
    public void CabModel_SplitsGuitarVsBass(int index, bool isBass)
        => Assert.Equal(isBass, IsBassCab(index));

    /// <summary>#74: the 26-model roster lands in three picker groups, every
    /// model in exactly one, and the PA / hi-fi boxes in their own.</summary>
    [Fact]
    public void CabModel_EveryModelHasOneGroup_AndTheFullRangeBoxesAreTheirOwn()
    {
        var groups = Enumerable.Range(0, CabModelCount).GroupBy(CabGroup).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 13, 14], groups[CabModelGroup.Guitar]);
        Assert.Equal([9, 10, 11, 18, 19, 20, 21, 22, 23, 24, 25], groups[CabModelGroup.Bass]);
        Assert.Equal([15, 16, 17], groups[CabModelGroup.FullRange]);
    }
}
