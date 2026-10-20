using System.Globalization;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI.Converters;

/// <summary>
///     The tuning line under each row of the Tracks flyout's arrangement picker —
///     the fact that actually tells two similarly-named arrangements apart.
/// </summary>
public sealed class ArrangementTuningConverterTests
{
    private static object? Convert(Arrangement arrangement) =>
        ArrangementTuningConverter.Instance.Convert(
            arrangement, typeof(string), null, CultureInfo.InvariantCulture);

    private static Arrangement WithOffsets(params int[] offsets)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var step in offsets) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0 }, "E Standard")]
    [InlineData(new[] { -2, 0, 0, 0, 0, 0 }, "Drop D")]
    [InlineData(new[] { -1, -1, -1, -1, -1, -1 }, "Eb Standard")]
    public void AGuitarTuningResolvesToItsName(int[] offsets, string expected) => Assert.Equal(expected, Convert(WithOffsets(offsets)));

    /// <summary>
    ///     <b>The reason offsets come first.</b> Rocksmith stores every tuning in
    ///     six slots whatever the instrument, so a bass read as a guitar lands
    ///     under a tuning no guitar plays — 122 of the owner's 4,321 songs were
    ///     filed that way before v0.42.1. The array's own LENGTH is the
    ///     authoritative string count, so a 4-slot arrangement resolves as a bass.
    /// </summary>
    [Fact]
    public void AFourStringBassResolvesAsABass() => Assert.Equal("Drop D", Convert(WithOffsets(-2, 0, 0, 0)));

    /// <summary>
    ///     27 of the tunings in the owner's library are deliberately unnamed
    ///     (one-off, or one string away from a named one), so "no name" is an
    ///     ordinary answer — it must render the note letters, never an empty row.
    /// </summary>
    [Fact]
    public void AnUnnamedTuningFallsBackToTheNoteLetters()
    {
        var arrangement = WithOffsets(1, 2, 3, 4, 5, 6);
        arrangement.Tuning = ["F", "B", "F", "B", "E", "A"];

        Assert.Equal("F B F B E A", Convert(arrangement));
    }

    /// <summary>The picker binds the collection directly, and Avalonia hands a
    /// converter whatever is in it — including the null placeholder while the
    /// selection is empty. It must not throw into a layout pass.</summary>
    [Fact]
    public void ANonArrangementValueIsEmpty()
    {
        Assert.Equal(string.Empty,
            ArrangementTuningConverter.Instance.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty,
            ArrangementTuningConverter.Instance.Convert("not an arrangement", typeof(string), null,
                CultureInfo.InvariantCulture));
    }
}
