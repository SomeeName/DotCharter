using System.Collections.ObjectModel;

namespace DotCharter.Tests.Core;

/// <summary>
///     A chart states its tuning twice — note LETTERS and per-string OFFSETS — and
///     four exporters used to read only the offsets, with <c>0</c> for a missing
///     entry. So an arrangement carrying only letters shipped as E standard, with no
///     error anywhere.
/// </summary>
public sealed class TuningOffsetResolutionTests
{
    private static Arrangement With(string[]? notes = null, int[]? offsets = null, int strings = 6)
    {
        var arrangement = new Arrangement { StringCount = strings };
        if (notes is not null) arrangement.Tuning = notes;
        if (offsets is not null)
        {
            arrangement.TuningPitches =
                new ObservableCollection<TuningOffset>(offsets.Select(o => new TuningOffset(o)));
        }

        return arrangement;
    }

    [Fact]
    public void AuthoredOffsetsAlwaysWin_SoEveryExistingChartExportsUnchanged()
    {
        // The conservative half, and the reason this is safe to put under four
        // exporters at once: if there are offsets at all, they are the answer —
        // even when the letters disagree with them.
        var arrangement = With(
            notes: ["E", "A", "D", "G", "B", "e"],
            offsets: [-4, -2, -2, -2, -2, -2]);

        Assert.Equal([-4, -2, -2, -2, -2, -2], TuningOffsetResolution.Resolve(arrangement));
    }

    [Theory]
    // 6-string guitar, the unambiguous base E A D G B E.
    [InlineData(new[] { "E", "A", "D", "G", "B", "e" }, new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { "D", "A", "D", "G", "B", "e" }, new[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { "C", "G", "C", "F", "A", "D" }, new[] { -4, -2, -2, -2, -2, -2 })]
    [InlineData(new[] { "Eb", "Ab", "Db", "Gb", "Bb", "Eb" }, new[] { -1, -1, -1, -1, -1, -1 })]
    // 4-string bass shares the first four of that base.
    [InlineData(new[] { "E", "A", "D", "G" }, new[] { 0, 0, 0, 0 })]
    [InlineData(new[] { "D", "A", "D", "G" }, new[] { -2, 0, 0, 0 })]
    public void WithNoOffsets_TheyAreDerivedFromTheLetters(string[] notes, int[] expected) =>
        Assert.Equal(expected, TuningOffsetResolution.Resolve(With(notes, strings: notes.Length)));

    [Fact]
    public void TheDerivationRoundTripsExactly_SoANameSurvivesIt()
    {
        // Offsets are the nearest signed spelling, so re-deriving the letters
        // reproduces the input — which is what lets TuningNameResolver name a
        // derived vector the same way it names the letters it came from.
        string[] notes = ["C", "G", "C", "F", "A", "D"];
        var offsets = TuningOffsetResolution.DeriveFromNoteNames(notes);

        Assert.NotNull(offsets);
        Assert.Equal(TuningNameResolver.Resolve(notes),
            TuningNameResolver.ResolveFromOffsets(6, offsets));
        Assert.Equal("Drop C", TuningNameResolver.ResolveFromOffsets(6, offsets));
    }

    [Fact]
    public void AnAmbiguousStringCount_DerivesNothingRatherThanGuessing()
    {
        // A 5-string bass offset has no agreed base (GP measures against B E A D G,
        // the RS SNG path against E A D G B). Guessing would write a WRONG tuning
        // into a shipped package, so this stays at the zeros callers already used.
        Assert.Null(TuningOffsetResolution.DeriveFromNoteNames(["B", "E", "A", "D", "G"]));
        Assert.Null(TuningOffsetResolution.DeriveFromNoteNames(["B", "E", "A", "D", "G", "C", "F"]));
    }

    [Fact]
    public void OneUnreadableString_MeansNoAnswerAtAll()
    {
        // A partial derivation would silently claim standard for the strings it
        // could not read, which is exactly the failure being fixed.
        Assert.Null(TuningOffsetResolution.DeriveFromNoteNames(["E", "A", "?", "G", "B", "e"]));
        Assert.Empty(TuningOffsetResolution.Resolve(With(["E", "A", "?", "G", "B", "e"])));
    }

    [Fact]
    public void ForReturnsZeroPastTheEnd_WhichIsWhatEveryCallersDefaultWas() =>
        Assert.Equal(0, TuningOffsetResolution.For(With(["E", "A", "D", "G"], strings: 4), 5));
}
