namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #493: the new chord vocabulary (both styles), the musical style's spelling
///     in the song's key, and its slash chords. Every shape here is one the
///     owner's tabs typed a name for.
/// </summary>
public class ChordNamingStyleTests
{
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "E"];
    private static readonly string[] EbStandard = ["Eb", "Ab", "Db", "Gb", "Bb", "Eb"];

    private static readonly ChordKeySignature DbMajor = new(-5, false);
    private static readonly ChordKeySignature EbMajor = new(-3, false);
    private static readonly ChordKeySignature GbMajor = new(-6, false);
    private static readonly ChordKeySignature BMajor = new(5, false);
    private static readonly ChordKeySignature EMajor = new(4, false);

    // ---- vocabulary: in EOF style the #493 shapes are a fallback ----------

    [Theory]
    [InlineData("x,9,8,9,9,x", "F#9")] // shell 9, no 5th
    [InlineData("x,8,6,8,8,x", "Fmin9")] // shell m9
    [InlineData("x,8,x,8,8,x", "F7sus2")] // 7sus2 without the 5th
    [InlineData("x,3,2,4,3,x", "CMaj9")] // shell maj9
    [InlineData("6,x,9,7,6,x", "A#(b9)")] // add b9
    [InlineData("8,x,7,8,9,x", "Cmin6(#5)")]
    public void Eof_NamesAShapeThatHadNoNameBefore(string frets, string expected) =>
        Assert.Equal(expected, First(Standard, frets, ChordNamingOptions.Default));

    [Fact]
    public void Eof_NamesTheSeventhFlatNineShell_InEbTuning() =>
        Assert.Equal("C7(b9)", First(EbStandard, "x,4,3,4,3,x", ChordNamingOptions.Default));

    /// <summary>
    ///     The fallback is what keeps EOF parity: ranked with the rest, the
    ///     root-position <c>Maj7sus2</c> / <c>7sus2</c> took first place from the
    ///     inversion name EOF writes (<c>C#add4</c>, <c>D#minadd4</c>).
    /// </summary>
    [Theory]
    [InlineData("x,9,11,10,9,x", "C#add4")]
    [InlineData("x,11,13,11,11,x", "D#minadd4")]
    [InlineData("6,x,x,5,6,5", "Fadd4")]
    public void Eof_KeepsItsOldNameWhereTheOldVocabularyHadOne(string frets, string expected) =>
        Assert.Equal(expected, First(Standard, frets, ChordNamingOptions.Default));

    [Theory]
    [InlineData("x,9,11,10,9,x", "Gbmaj7sus2")]
    [InlineData("x,11,13,11,11,x", "Ab7sus2")]
    [InlineData("x,9,8,9,9,x", "Gb9")]
    [InlineData("x,11,9,11,11,x", "Abm9")]
    [InlineData("x,7,x,7,7,x", "E7sus2")]
    [InlineData("x,9,8,6,6,x", "Gbmaj7")]
    public void Musical_RanksTheNewVocabularyWithTheRest(string frets, string expected) =>
        Assert.Equal(expected, First(Standard, frets, Musical(GbMajor)));

    [Fact]
    public void Musical_SpellsTheSuffixesTheMusicalWay()
    {
        Assert.Equal("Bbm7b5", First(Standard, "6,x,6,6,5,x", Musical(EbMajor)));
        Assert.Equal("Cmaj9", First(Standard, "x,3,2,4,3,x", Musical(null)));
        Assert.Equal("Cm6(#5)", First(Standard, "8,x,7,8,9,x", Musical(EbMajor)));
        Assert.Equal("Bb(b9)", First(Standard, "6,x,9,7,6,x", Musical(EbMajor)));
        Assert.Equal("G5(b5)", First(Standard, "3,4,5,x,x,x", Musical(null)));
    }

    // ---- inversions ------------------------------------------------------

    [Fact]
    public void AFirstInversion_IsPlainInEof_AndASlashChordInMusical()
    {
        const string eOverGSharp = "4,x,2,4,5,x";

        Assert.Equal("E", First(Standard, eOverGSharp, ChordNamingOptions.Default));
        Assert.Equal("E/G#", First(Standard, eOverGSharp, Musical(EMajor)));
        Assert.Contains("E", Names(Standard, eOverGSharp, Musical(EMajor)));
    }

    [Fact]
    public void ASecondInversion_IsASlashChordInMusical() =>
        Assert.Equal("G/D", First(Standard, "x,x,0,0,0,3", Musical(null)));

    [Fact]
    public void AThirdInversionShell_SpellsTheBassInTheKey()
    {
        const string cm7OverBb = "6,x,x,5,4,x";

        Assert.Equal("Cmin7", First(Standard, cm7OverBb, ChordNamingOptions.Default));
        Assert.Equal("Cm7/Bb", First(Standard, cm7OverBb, Musical(EbMajor)));
        Assert.Equal("Cm7/A#", First(Standard, cm7OverBb, Musical(null)));
    }

    [Fact]
    public void ARootPositionChord_IsNeverASlashChord()
    {
        Assert.Equal("C", First(Standard, "x,3,2,0,1,0", Musical(null)));
        Assert.Equal("Am", First(Standard, "x,0,2,2,1,0", Musical(null)));
    }

    // ---- the same shape in a sharp key and a flat key ----------------------

    [Fact]
    public void TheSameShape_IsSpelledByTheKey()
    {
        const string bbm7 = "x,1,3,1,2,x";

        Assert.Equal("Bbm7", First(Standard, bbm7, Musical(DbMajor)));
        Assert.Equal("A#m7", First(Standard, bbm7, Musical(BMajor)));
        Assert.DoesNotContain("A#m7", Names(Standard, bbm7, Musical(DbMajor)));
        Assert.DoesNotContain("Bbm7", Names(Standard, bbm7, Musical(BMajor)));
    }

    [Fact]
    public void WithNoKey_MusicalKeepsTheOldOrder_SharpFirstThenFlat()
    {
        var names = Names(Standard, "x,1,3,1,2,x", Musical(null));

        Assert.Equal("A#m7", names[0]);
        Assert.Equal("Bbm7", names[1]);
    }

    [Fact]
    public void EofStyle_IgnoresTheKey()
    {
        var withKey = new ChordNamingOptions(ChordNamingStyle.Eof, DbMajor);

        Assert.Equal(
            Names(Standard, "x,1,3,1,2,x", ChordNamingOptions.Default),
            Names(Standard, "x,1,3,1,2,x", withKey));
        Assert.Equal("A#min7", First(Standard, "x,1,3,1,2,x", withKey));
    }

    [Fact]
    public void ASingleNote_IsSpelledInTheKey()
    {
        Assert.Equal("Db", First(Standard, "x,4,x,x,x,x", Musical(DbMajor)));
        Assert.Equal("C#", First(Standard, "x,4,x,x,x,x", ChordNamingOptions.Default));
    }

    // ---- the double-stop gate does not care about the style ---------------

    [Fact]
    public void ADoubleStop_IsNotAutoNamedInMusicalStyleEither()
    {
        var arrangement = new Arrangement { StringCount = 6, Tuning = Standard };
        var frets = new Dictionary<int, int> { [3] = 4, [4] = 3 };

        Assert.Equal("", ChordNameSuggester.SuggestFirstChordName(arrangement, frets, naming: Musical(EMajor)));
        Assert.NotEmpty(ChordNameSuggester.SuggestChordNames(arrangement, frets, Musical(EMajor)));
    }

    [Fact]
    public void APowerChord_KeepsItsNameInMusicalStyle_WhenTheOptionSaysSo()
    {
        var arrangement = new Arrangement { StringCount = 6, Tuning = Standard };
        var frets = new Dictionary<int, int> { [0] = 2, [1] = 4 };
        var keep = new DoubleStopNameOptions(Suppress: true, KeepPowerChordNames: true);

        Assert.Equal("", ChordNameSuggester.SuggestFirstChordName(arrangement, frets, naming: Musical(GbMajor)));
        Assert.Equal("Gb5", ChordNameSuggester.SuggestFirstChordName(arrangement, frets, keep, Musical(GbMajor)));
    }

    [Fact]
    public void TheArrangementOverload_FoldsTheCapoInForMusicalToo()
    {
        // Capo 1 + the open-A-minor-seven grip sounds Bbm7.
        var arrangement = new Arrangement { StringCount = 6, Tuning = Standard, Capo = 1 };
        var frets = new Dictionary<int, int> { [1] = 0, [2] = 3, [3] = 0, [4] = 2 };

        Assert.Equal("Bbm7", ChordNameSuggester.SuggestChordNames(arrangement, frets, Musical(DbMajor))[0]);
    }

    private static ChordNamingOptions Musical(ChordKeySignature? key) => new(ChordNamingStyle.Musical, key);

    private static string First(string[] tuning, string frets, ChordNamingOptions naming) =>
        Names(tuning, frets, naming) is { Count: > 0 } names ? names[0] : "";

    private static IReadOnlyList<string> Names(string[] tuning, string frets, ChordNamingOptions naming) =>
        ChordNameSuggester.SuggestChordNames(tuning, ChordNameAnswerKeyTests.ParseFrets(frets), 0, naming);
}
