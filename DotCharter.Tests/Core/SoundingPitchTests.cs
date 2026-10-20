namespace DotCharter.Tests.Core;

/// <summary>
///     What a (string, fret) pair actually SOUNDS.
///     <para>
///         The arithmetic lived privately inside the SNG mapper, which is why
///         nothing the charter can see could answer "what note is this?" — and
///         why moving a note to another string carried the fret across unchanged
///         and re-pitched it.
///     </para>
/// </summary>
public sealed class SoundingPitchTests
{
    [Theory]
    [InlineData(0, 0, 40)]  // low E open = E2
    [InlineData(0, 5, 45)]  // fret 5 on the low E = A2…
    [InlineData(1, 0, 45)]  // …which is the open A. The reported example.
    [InlineData(5, 12, 76)] // high E, 12th = E5
    public void StandardTuning_SoundsTheStandardPitches(int stringIndex, int fret, int expected) =>
        Assert.Equal(expected, SoundingPitch.MidiFor(Guitar(), stringIndex, fret));

    /// <summary>A bass sounds an octave below where it is written — the same −12
    /// the SNG mapper applies.</summary>
    [Fact]
    public void ABassSoundsAnOctaveLower() =>
        Assert.Equal(28, SoundingPitch.MidiFor(Bass(), 0, 0));

    [Fact]
    public void TheTuningOffsetMovesTheOpenString()
    {
        var dropD = Guitar();
        // Authored offsets, which is what TuningOffsetResolution prefers over
        // the note letters.
        foreach (var step in new[] { -2, 0, 0, 0, 0, 0 })
            dropD.TuningPitches.Add(new TuningOffset(step));
        Assert.Equal(38, SoundingPitch.MidiFor(dropD, 0, 0)); // D2
    }

    /// <summary>
    ///     Fret 0 is OPEN and a capo stops it at the capo fret; a non-zero fret
    ///     BEHIND the capo is malformed rather than lower-sounding (inspector
    ///     <c>L01</c>).
    /// </summary>
    [Fact]
    public void ACapoStopsTheOpenString()
    {
        var capoed = Guitar();
        capoed.Capo = 3;

        Assert.Equal(43, SoundingPitch.MidiFor(capoed, 0, 0));  // open = the capo, G2
        Assert.Equal(43, SoundingPitch.MidiFor(capoed, 0, 3));  // and so is fret 3
        Assert.Null(SoundingPitch.MidiFor(capoed, 0, 2));       // behind the capo: malformed
    }

    [Theory]
    [InlineData(45, "A2")]
    [InlineData(40, "E2")]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    public void NamesAreScientificPitch(int midi, string expected) => Assert.Equal(expected, SoundingPitch.NameFor(midi));

    /// <summary>The reported move: fret 5 on the low E is fret 0 on the A.</summary>
    [Fact]
    public void TheSamePitchLandsOnTheRightFretOfTheNextString()
    {
        var guitar = Guitar();
        var midi = SoundingPitch.MidiFor(guitar, 0, 5)!.Value;
        Assert.Equal(0, SoundingPitch.FretForPitch(guitar, 1, midi));
    }

    [Fact]
    public void AStringThatCannotReachThePitch_AnswersNull()
    {
        var guitar = Guitar();
        var lowE = SoundingPitch.MidiFor(guitar, 0, 0)!.Value; // E2

        // The high E cannot sound E2 — it is two octaves above it.
        Assert.Null(SoundingPitch.FretForPitch(guitar, 5, lowE));
    }

    [Fact]
    public void APitchPastTheTwentyFourthFret_AnswersNull()
    {
        var guitar = Guitar();
        var high = SoundingPitch.MidiFor(guitar, 5, 24)!.Value;
        Assert.Null(SoundingPitch.FretForPitch(guitar, 0, high));
    }

    /// <summary>Behind a capo the sound at the capo IS the open string, and that
    /// is how every fret consumer expects it spelled.</summary>
    [Fact]
    public void AtTheCapo_TheAnswerIsFretZero()
    {
        var capoed = Guitar();
        capoed.Capo = 3;
        var atCapo = SoundingPitch.MidiFor(capoed, 1, 0)!.Value;
        Assert.Equal(0, SoundingPitch.FretForPitch(capoed, 1, atCapo));
    }

    [Fact]
    public void MidiAndFretAreInverses()
    {
        var guitar = Guitar();
        for (var stringIndex = 0; stringIndex < 6; stringIndex++)
        {
            for (var fret = 0; fret <= 24; fret++)
            {
                var midi = SoundingPitch.MidiFor(guitar, stringIndex, fret)!.Value;
                Assert.Equal(fret, SoundingPitch.FretForPitch(guitar, stringIndex, midi));
            }
        }
    }

    private static Arrangement Guitar() =>
        new() { Name = "Lead", ArrangementType = ArrangementType.Lead };

    private static Arrangement Bass() =>
        new() { Name = "Bass", ArrangementType = ArrangementType.Bass };
}
