namespace DotCharter.Tests.Core.Utilities;

public sealed class ArrangementNoteSpellingTests
{
    [Theory]
    [InlineData(-1, "C#7(b9,#11)/G#", "Db7(b9,#11)/Ab")]
    [InlineData(0, "Dbm7/Ab", "C#m7/G#")]
    [InlineData(1, "Ab6/9", "G#6/9")]
    [InlineData(-1, "F#min7(b5)/C#", "Gbmin7(b5)/Db")]
    public void GeneratedChordSpelling_PreservesQualitiesAndRespectsSlashBasses(int shift, string source, string expected) =>
        Assert.Equal(expected, TuningDisplayNames.ChordSuggestion(source, Make(6, false, shift)));

    [Theory]
    [InlineData(6, false, 0, "E A D G B E")]
    [InlineData(6, false, -1, "Eb Ab Db Gb Bb Eb")]
    [InlineData(6, false, -2, "D G C F A D")]
    [InlineData(6, false, 1, "F A# D# G# C F")]
    [InlineData(7, false, 0, "B E A D G B E")]
    [InlineData(8, false, 0, "F# B E A D G B E")]
    [InlineData(9, false, 0, "C# F# B E A D G B E")]
    [InlineData(5, true, 0, "B E A D G")]
    [InlineData(6, true, 0, "B E A D G C")]
    public void OpenPitches_FollowTheArrangementSpelling(int count, bool bass, int shift, string expected)
    {
        var arrangement = Make(count, bass, shift);
        var names = Enumerable.Range(0, count)
            .Select(i => SoundingPitch.NameFor(arrangement, i, 0)!.TrimEnd("0123456789-".ToCharArray()));
        Assert.Equal(expected, string.Join(" ", names));
        Assert.Equal(expected, string.Join(" ", TuningDisplayNames.For(arrangement)!));
    }

    [Fact]
    public void LoweredArrangement_UsesFlatsForFrettedPitches()
    {
        var arrangement = Make(6, false, -1);
        Assert.Equal("Ab3", SoundingPitch.NameFor(arrangement, 3, 2));
        Assert.Equal("Db4", SoundingPitch.NameFor(arrangement, 4, 3));
        Assert.Equal("Gb4", SoundingPitch.NameFor(arrangement, 5, 3));
    }

    [Fact]
    public void DropD_AndUnchangedExtendedStrings_KeepTheirStandardNames()
    {
        Assert.Equal(new[] { "D", "A", "D", "G", "B", "E" }, TuningDisplayNames.For(6, [-2, 0, 0, 0, 0, 0]));
        Assert.Equal(new[] { "C#", "F#", "Bb", "Eb", "Ab", "Db", "Gb", "Bb", "Eb" },
            TuningDisplayNames.For(9, [0, 0, -1, -1, -1, -1, -1, -1, -1]));
        Assert.False(TuningDisplayNames.PreferFlats(6, [-1, 1, 0, 0, 0, 0]));
    }

    [Fact]
    public void DisplayDoesNotChangeTheStoredNamingContractOrRecognition()
    {
        int[] offsets = [-1, -1, -1, -1, -1, -1];
        Assert.Equal(new[] { "Eb", "G#", "C#", "F#", "Bb", "Eb" }, TuningUtilities.GetTuningNames(6, offsets));
        Assert.Equal("Eb Standard", TuningNameResolver.Resolve("Eb Ab Db Gb Bb Eb"));
        Assert.Equal("Eb Standard", TuningNameResolver.Resolve("D# G# C# F# A# D#"));
        Assert.Equal("Eb Ab Db Gb Bb Eb", TuningDisplayNames.Summary("D# G# C# F# A# D#"));
        Assert.Equal("5-String Bass Standard (B E A D G)", TuningDisplayNames.Describe("B E A D G"));
    }

    private static Arrangement Make(int count, bool bass, int shift)
    {
        var arrangement = new Arrangement { StringCount = count, ArrangementType = bass ? ArrangementType.Bass : ArrangementType.Lead };
        foreach (var offset in ExtendedRangeTunings.StandardOffsets(count, bass))
            arrangement.TuningPitches.Add(new TuningOffset { Step = offset + shift });
        return arrangement;
    }

    [Fact]
    public void DeepDropLibrarySummary_KeepsTheKnownLoweringDirection() =>
        Assert.Equal("Drop Ab (Ab Eb Ab Db F Bb)", TuningDisplayNames.Describe("G# D# G# C# F A#"));

    [Fact]
    public void A220BassOnAGuitarPath_UsesTheBassStandardForSpelling()
    {
        var arrangement = Make(6, true, 1);
        arrangement.ArrangementType = ArrangementType.Lead;
        arrangement.CentOffset = -1200;
        Assert.Equal(new[] { "C", "F", "A#", "D#", "G#", "C#" }, TuningDisplayNames.For(arrangement));
    }

    [Fact]
    public void UnresolvedExtendedTuning_PreservesTheAuthoredStringPitches()
    {
        var arrangement = new Arrangement
        {
            StringCount = 7, TuningType = "CUSTOM", Tuning = ["A", "E", "A", "D", "G", "B", "E"]
        };
        Assert.Empty(TuningOffsetResolution.Resolve(arrangement));
        Assert.Equal(arrangement.Tuning, TuningDisplayNames.For(arrangement));
        arrangement.Tuning = ["Bb", "Eb", "G#", "C#", "F#", "Bb", "Eb"];
        Assert.Empty(TuningOffsetResolution.Resolve(arrangement));
        Assert.Equal(new[] { "Bb", "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, TuningDisplayNames.For(arrangement));
    }
}
