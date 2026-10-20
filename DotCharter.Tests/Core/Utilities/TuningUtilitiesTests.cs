namespace DotCharter.Tests.Core.Utilities;

public class TuningUtilitiesTests
{
    private static readonly string[] ExpectedSixStringE = ["E", "A", "D", "G", "B", "E"];
    private static readonly string[] ExpectedFourStringE = ["E", "A", "D", "G"];
    private static readonly string[] ExpectedSixStringEbHalfStep = ["Eb", "G#", "C#", "F#", "Bb", "Eb"];

    [Fact]
    public void StandardSixString_ReturnsEADGBE()
    {
        var names = TuningUtilities.GetTuningNames(6, [0, 0, 0, 0, 0, 0]);
        Assert.Equal(ExpectedSixStringE, names);
    }

    [Fact]
    public void StandardFourString_Bass_ReturnsEADG()
    {
        var names = TuningUtilities.GetTuningNames(4, [0, 0, 0, 0]);
        Assert.Equal(ExpectedFourStringE, names);
    }

    [Fact]
    public void DropD_ReturnsDADGBE()
    {
        var names = TuningUtilities.GetTuningNames(6, [-2, 0, 0, 0, 0, 0]);
        Assert.Equal("D", names[0]);
        Assert.Equal("A", names[1]);
    }

    [Fact]
    public void EbStandard_LowersAllStrings()
    {
        var names = TuningUtilities.GetTuningNames(6, [-1, -1, -1, -1, -1, -1]);
        Assert.Equal(ExpectedSixStringEbHalfStep, names);
    }

    [Fact]
    public void VeryNegativeOffset_WrapsPositivelyAroundOctave()
    {
        // -12 is a full octave below — note name stays the same.
        var names = TuningUtilities.GetTuningNames(6, [-12, 0, 0, 0, 0, 0]);
        Assert.Equal("E", names[0]);
    }

    [Fact]
    public void StandardSevenString_StartsAtB()
    {
        // The GP importers measure a 7-string against { 35, 40, 45, 50, 55, 59, 64 }
        // = B E A D G B E, so an all-zero offset set must name that. Falling back
        // to E past index 5 named string 0 "E" and string 6 "E" instead of "B"/"E".
        var names = TuningUtilities.GetTuningNames(7, [0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(["B", "E", "A", "D", "G", "B", "E"], names);
    }

    [Fact]
    public void StandardEightString_StartsAtFSharp()
    {
        var names = TuningUtilities.GetTuningNames(8, [0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(["F#", "B", "E", "A", "D", "G", "B", "E"], names);
    }

    [Fact]
    public void SevenString_DropA_LowersOnlyTheLowestString()
    {
        var names = TuningUtilities.GetTuningNames(7, [-2, 0, 0, 0, 0, 0, 0]);
        Assert.Equal("A", names[0]);
        Assert.Equal("E", names[1]);
    }

    [Fact]
    public void MissingOffsets_DefaultToZero()
    {
        // offsets array too short → remaining strings use offset=0
        var names = TuningUtilities.GetTuningNames(6, []);
        Assert.Equal(ExpectedSixStringE, names);
    }

    // --- GetOffsetsForTuningType: rscharter getTuningValues slice convention ---
    // For stringCount <= 6 the slice starts at index 3 of the 9-entry table
    // (fullSize - max(6, strings)), NOT at (fullSize - stringCount). Regression
    // for the bug where 4/5-string drop/open tunings loaded as standard.

    [Fact]
    public void GetOffsets_EDropD_FourStringBass_KeepsDropString()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_DROP_D", 4);
        Assert.Equal([-2, 0, 0, 0], offsets);
    }

    [Fact]
    public void GetOffsets_EDropD_FiveStringBass_KeepsDropString()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_DROP_D", 5);
        Assert.Equal([-2, 0, 0, 0, 0], offsets);
    }

    [Fact]
    public void GetOffsets_EDropD_SixString_Unchanged()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_DROP_D", 6);
        Assert.Equal([-2, 0, 0, 0, 0, 0], offsets);
    }

    [Fact]
    public void GetOffsets_EDropD_SevenString_UsesLastSevenEntries()
    {
        // >= 6 strings keeps the old last-N slice (start = 9 - stringCount).
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_DROP_D", 7);
        Assert.Equal([0, -2, 0, 0, 0, 0, 0], offsets);
    }

    [Fact]
    public void GetOffsets_UniformTuning_FourString_Unaffected()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_FLAT_STANDARD", 4);
        Assert.Equal([-1, -1, -1, -1], offsets);
    }

    [Fact]
    public void GetOffsets_Dadgad_SixString_MatchesReference()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("DADGAD", 6);
        Assert.Equal([-2, 0, 0, 0, -2, -2], offsets);
    }

    [Fact]
    public void GetOffsets_OpenG_FourString_TakesIndicesThreeToSix()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("OPEN_G", 4);
        Assert.Equal([-2, -2, 0, 0], offsets);
    }

    [Fact]
    public void GetOffsets_FourStringDropD_NamesRenderDADG()
    {
        var offsets = TuningUtilities.GetOffsetsForTuningType("E_DROP_D", 4);
        var names = TuningUtilities.GetTuningNames(4, offsets);
        Assert.Equal(["D", "A", "D", "G"], names);
    }
}
