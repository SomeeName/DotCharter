using DotCharter.Tone.Audio.Tuner;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

/// <summary>
///     Tuning-match transpose (#312): which paths can be played in the player's own tuning by
///     shifting the SONG. Only a path whose every string sits the same distance from the player's
///     tuning keeps its frets; anything else is refused with its reason.
/// </summary>
public sealed class TuningMatchTransposeTests
{
    private static readonly int[] EStandard = [0, 0, 0, 0, 0, 0];

    private static Arrangement Guitar(params int[] steps)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    [Fact]
    public void EFlatStandard_PlayedInEStandard_ShiftsTheSongUpOneSemitone()
    {
        var match = TuningMatchTranspose.Resolve(Guitar(-1, -1, -1, -1, -1, -1), EStandard);

        Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, 1), match);
        Assert.True(match.CanShift);
    }

    [Fact]
    public void DStandard_PlayedInEStandard_ShiftsTheSongUpTwoSemitones()
        => Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, 2),
            TuningMatchTranspose.Resolve(Guitar(-2, -2, -2, -2, -2, -2), EStandard));

    /// <summary>Drop D against E standard moves ONE string: its frets would change, which a pitch
    /// shift cannot do — so it is refused, not shifted by a guess.</summary>
    [Fact]
    public void DropD_AgainstEStandard_IsUnsupported_BecauseTheStringsDifferUnevenly()
    {
        var match = TuningMatchTranspose.Resolve(Guitar(-2, 0, 0, 0, 0, 0), EStandard);

        Assert.Equal(TuningMatchVerdict.NotUniform, match.Verdict);
        Assert.False(match.CanShift);
        Assert.Equal(0, match.Semitones);
    }

    /// <summary>Uniform is measured against the PLAYER's tuning, not against E standard: Drop C#
    /// is one semitone below Drop D on every string.</summary>
    [Fact]
    public void DropCSharp_PlayedInDropD_ShiftsUpOne()
        => Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, 1),
            TuningMatchTranspose.Resolve(Guitar(-3, -1, -1, -1, -1, -1), [-2, 0, 0, 0, 0, 0]));

    /// <summary>The other direction: a song in E standard, a player tuned to E♭, shifts DOWN.</summary>
    [Fact]
    public void EStandard_PlayedInEFlat_ShiftsDownOne()
        => Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, -1),
            TuningMatchTranspose.Resolve(Guitar(0, 0, 0, 0, 0, 0), [-1, -1, -1, -1, -1, -1]));

    [Fact]
    public void ThePlayersOwnTuning_HasNothingToShift()
        => Assert.Equal(TuningMatchVerdict.AlreadyMatches,
            TuningMatchTranspose.Resolve(Guitar(0, 0, 0, 0, 0, 0), EStandard).Verdict);

    [Fact]
    public void FurtherThanTheCap_IsRefused_WithItsStep()
    {
        var step = TuningMatchTranspose.MaxShiftSemitones + 1;
        var chart = Enumerable.Repeat(-step, 6).ToArray();

        Assert.Equal(new TuningMatch(TuningMatchVerdict.TooFar, step),
            TuningMatchTranspose.Resolve(Guitar(chart), EStandard));
        // …and the cap itself is still allowed.
        Assert.True(TuningMatchTranspose.Resolve(
            Guitar(Enumerable.Repeat(-TuningMatchTranspose.MaxShiftSemitones, 6).ToArray()), EStandard).CanShift);
    }

    /// <summary>A bass path keeps six slots, of which four are strings; the two it does not have
    /// must not make an E♭ bass look non-uniform.</summary>
    [Fact]
    public void ABassPath_ComparesOnlyItsFourStrings()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        foreach (var step in new[] { -1, -1, -1, -1, 0, 0 }) bass.TuningPitches.Add(new TuningOffset(step));

        Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, 1), TuningMatchTranspose.Resolve(bass, EStandard));
    }

    /// <summary>A chart that carries its tuning only as note LETTERS is read the way live judging
    /// reads it (<see cref="TuningOffsetResolution" />), so the two cannot disagree.</summary>
    [Fact]
    public void ATuningGivenOnlyAsLetters_IsRead()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            Tuning = ["D", "G", "C", "F", "A", "D"]
        };

        Assert.Equal(new TuningMatch(TuningMatchVerdict.Shift, 2), TuningMatchTranspose.Resolve(arrangement, EStandard));
    }

    /// <summary>A tuning NAME nothing can decode, with no offsets and no letters, is not guessed
    /// at: shifting by a guess would move the song to a tuning nobody stated. (A path that states
    /// nothing at all is standard, as the tuner and judging both read it.)</summary>
    [Fact]
    public void ATuningNothingCanRead_IsUnknown()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, StringCount = 6, Tuning = [], TuningType = "CUSTOM"
        };

        Assert.Equal(TuningMatchVerdict.UnknownTuning, TuningMatchTranspose.Resolve(arrangement, EStandard).Verdict);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void AStringCountWithNoSharedBase_IsUnsupported(int strings)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));

        Assert.Equal(TuningMatchVerdict.UnsupportedStringCount,
            TuningMatchTranspose.Resolve(arrangement, EStandard).Verdict);
    }

    /// <summary>The guided check aims at the PLAYER's strings while the song is shifted: an E♭
    /// chart shifted +1 is tuned as E standard.</summary>
    [Fact]
    public void TheGuidedCheck_ShiftedToThePlayersTuning_TargetsTheirStrings()
    {
        var shifted = TuningCheckTargets.Build(Guitar(-1, -1, -1, -1, -1, -1), transposeSemitones: 1);

        Assert.Equal(TuningCheckTargets.Build(Guitar(0, 0, 0, 0, 0, 0)).Select(t => t.Midi),
            shifted.Select(t => t.Midi));
        Assert.Equal("E2", shifted[0].NoteName);
    }
}
