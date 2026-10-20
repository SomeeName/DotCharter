namespace DotCharter.Tests.Core;

/// <summary>
///     Five-string bass tunings. They had no entries at all, so the seventh-most
///     common instrument in the owner's library showed as six bare note letters
///     everywhere a tuning is displayed.
/// </summary>
public sealed class TuningNameResolverBassTests
{
    [Theory]
    [InlineData(new[] { "B", "E", "A", "D", "G" }, "5-String Bass Standard")]
    [InlineData(new[] { "Bb", "Eb", "Ab", "Db", "Gb" }, "5-String Bass Eb Standard")]
    [InlineData(new[] { "A", "D", "G", "C", "F" }, "5-String Bass D Standard")]
    [InlineData(new[] { "G", "C", "F", "Bb", "Eb" }, "5-String Bass C Standard")]
    [InlineData(new[] { "A", "E", "A", "D", "G" }, "5-String Bass Drop A")]
    // Enharmonic spellings resolve identically — the table is keyed on pitch class.
    [InlineData(new[] { "A#", "D#", "G#", "C#", "F#" }, "5-String Bass Eb Standard")]
    public void AFiveStringBass_HasAName(string[] notes, string expected) =>
        Assert.Equal(expected, TuningNameResolver.Resolve(notes));

    [Fact]
    public void TheNamesCarryTheFiveStringBassPrefix_SoNothingCollidesWithAGuitar()
    {
        // A bare "B Standard" would read as the SIX-string guitar tuning of that
        // name, filing a bass arrangement under a guitar tuning. Same convention the
        // pre-existing "6-String Bass Standard" entry already uses.
        Assert.Equal("B Standard", TuningNameResolver.Resolve(["B", "E", "A", "D", "F#", "B"]));
        Assert.Equal("5-String Bass Standard", TuningNameResolver.Resolve(["B", "E", "A", "D", "G"]));
        Assert.Equal("6-String Bass Standard", TuningNameResolver.Resolve(["B", "E", "A", "D", "G", "C"]));
    }

    [Fact]
    public void AnUnrecognisedFiveStringTuning_IsStillUnknown() =>
        Assert.Null(TuningNameResolver.Resolve(["C", "E", "A", "D", "G"]));
}
