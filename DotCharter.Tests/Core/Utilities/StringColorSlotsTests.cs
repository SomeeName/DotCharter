namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #745: a string's colour names its IDENTITY, not its lane. The six standard strings keep
///     their slots on a 7- and 8-string (low E red = slot 0 … high E purple = slot 5); the extra
///     low strings are the two greys (7th = slot 6, 8th = slot 7). Six-string guitars and
///     four-string basses map exactly as before (slot = string index).
/// </summary>
public sealed class StringColorSlotsTests
{
    private static int[] Row(int stringCount) =>
        [.. Enumerable.Range(0, stringCount).Select(i => StringColorSlots.For(i, stringCount))];

    [Fact]
    public void SixStrings_AreUnchanged() => Assert.Equal([0, 1, 2, 3, 4, 5], Row(6));

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void BassCounts_AreUnchanged(int stringCount) =>
        Assert.Equal(Enumerable.Range(0, stringCount), Row(stringCount));

    [Fact]
    public void SevenStrings_LowBIsTheFirstGrey_AndTheRestKeepTheirSixStringColours() =>
        Assert.Equal([StringColorSlots.SeventhStringSlot, 0, 1, 2, 3, 4, 5], Row(7));

    [Fact]
    public void EightStrings_LowFSharpAndLowBAreTheTwoGreys() =>
        Assert.Equal([StringColorSlots.EighthStringSlot, StringColorSlots.SeventhStringSlot, 0, 1, 2, 3, 4, 5],
            Row(8));

    [Fact]
    public void TheLowE_IsSlotZero_OnEveryGuitar()
    {
        // The low E is string 0 on a 6-string, 1 on a 7-string and 2 on an 8-string
        // (StandardOpenStrings: extra strings extend DOWN) — always red.
        Assert.Equal(0, StringColorSlots.For(0, 6));
        Assert.Equal(0, StringColorSlots.For(1, 7));
        Assert.Equal(0, StringColorSlots.For(2, 8));
        Assert.Equal(0, StandardOpenStrings.SemitonesAboveLowE(7, 1));
        Assert.Equal(0, StandardOpenStrings.SemitonesAboveLowE(8, 2));
    }

    [Theory]
    [InlineData(-1, 6, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 5)]
    [InlineData(6, 6, 6)]
    [InlineData(7, 7, 6)]
    public void EdgeCases_StayInsideThePalette(int stringIndex, int stringCount, int expected)
    {
        var slot = StringColorSlots.For(stringIndex, stringCount);
        Assert.Equal(expected, slot);
        Assert.InRange(slot, 0, StringColorSlots.SlotCount - 1);
    }
}
