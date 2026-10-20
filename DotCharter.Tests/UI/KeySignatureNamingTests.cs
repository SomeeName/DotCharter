using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI;

/// <summary>
///     The song-metadata dialog names the key its signature actually spells.
///
///     <para>
///         It used to take the MAJOR name for the accidental count and append
///         "m", so every minor key was named a minor third too high: no sharps
///         and no flats with Minor ticked printed "Cm", but that signature is
///         A minor — C minor has three flats. The stored value was always right
///         (the chart persists a count and a flag); only the label lied, which
///         is worse than it sounds, because the label is the only way to check
///         the number.
///     </para>
/// </summary>
public sealed class KeySignatureNamingTests
{
    /// <summary>The circle of fifths, sharps side. Majors.</summary>
    [Theory]
    [InlineData(0, "C")]
    [InlineData(1, "G")]
    [InlineData(2, "D")]
    [InlineData(3, "A")]
    [InlineData(4, "E")]
    [InlineData(5, "B")]
    [InlineData(6, "F#")]
    [InlineData(7, "C#")]
    public void SharpMajorKeys(int accidentals, string expected) =>
        Assert.Equal(expected, SongMetadataViewModel.KeyName(accidentals, minor: false));

    /// <summary>…and the flats side.</summary>
    [Theory]
    [InlineData(-1, "F")]
    [InlineData(-2, "Bb")]
    [InlineData(-3, "Eb")]
    [InlineData(-5, "Db")]
    [InlineData(-7, "Cb")]
    public void FlatMajorKeys(int accidentals, string expected) =>
        Assert.Equal(expected, SongMetadataViewModel.KeyName(accidentals, minor: false));

    /// <summary>
    ///     The RELATIVE MINORS — the set that was wrong. Each is a minor third
    ///     below its major, which is exactly the error the old "append m" made.
    /// </summary>
    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "E")]
    [InlineData(2, "B")]
    [InlineData(3, "F#")]
    [InlineData(7, "A#")]
    [InlineData(-1, "D")]
    [InlineData(-2, "G")]
    [InlineData(-3, "C")]
    [InlineData(-7, "Ab")]
    public void MinorKeysAreTheRelativeMinor(int accidentals, string expected) =>
        Assert.Equal(expected, SongMetadataViewModel.KeyName(accidentals, minor: true));

    /// <summary>
    ///     The specific regression, stated as itself: three flats is C minor and
    ///     no accidentals is A minor. The old code returned "Cm" for zero and
    ///     "Ebm" for minus three.
    /// </summary>
    [Fact]
    public void TheOldBugSpecifically()
    {
        Assert.Equal("A", SongMetadataViewModel.KeyName(0, minor: true));
        Assert.Equal("C", SongMetadataViewModel.KeyName(-3, minor: true));
        Assert.NotEqual(
            SongMetadataViewModel.KeyName(0, minor: false),
            SongMetadataViewModel.KeyName(0, minor: true));
    }

    /// <summary>A major and its relative minor share a signature — which is
    /// what makes the two tables two tables.</summary>
    [Theory]
    [InlineData(0, "C", "A")]
    [InlineData(2, "D", "B")]
    [InlineData(-4, "Ab", "F")]
    public void ARelativePairSharesItsSignature(int accidentals, string major, string minor)
    {
        Assert.Equal(major, SongMetadataViewModel.KeyName(accidentals, minor: false));
        Assert.Equal(minor, SongMetadataViewModel.KeyName(accidentals, minor: true));
    }

    /// <summary>Out-of-range counts clamp rather than throwing. The stepper is
    /// bounded to ±7, but a chart file is not.</summary>
    [Theory]
    [InlineData(99)]
    [InlineData(-99)]
    [InlineData(int.MinValue + 1)]
    public void AnOutOfRangeSignatureClampsInsteadOfThrowing(int accidentals)
    {
        var name = SongMetadataViewModel.KeyName(accidentals, minor: false);
        Assert.False(string.IsNullOrEmpty(name));
    }
}
