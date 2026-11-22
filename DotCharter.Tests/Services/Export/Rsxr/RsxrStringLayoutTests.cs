using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrStringLayoutTests
{
    [Theory]
    [InlineData(ArrangementType.Lead, 6, new[] { 40, 45, 50, 55, 59, 64 }, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(ArrangementType.Lead, 7, new[] { 35, 40, 45, 50, 55, 59, 64 }, new[] { 6, 0, 1, 2, 3, 4, 5 })]
    [InlineData(ArrangementType.Lead, 8, new[] { 30, 35, 40, 45, 50, 55, 59, 64 }, new[] { 7, 6, 0, 1, 2, 3, 4, 5 })]
    [InlineData(ArrangementType.Bass, 4, new[] { 28, 33, 38, 43 }, new[] { 0, 1, 2, 3 })]
    [InlineData(ArrangementType.Bass, 5, new[] { 23, 28, 33, 38, 43 }, new[] { 4, 0, 1, 2, 3 })]
    [InlineData(ArrangementType.Bass, 6, new[] { 23, 28, 33, 38, 43, 48 }, new[] { 4, 0, 1, 2, 3, 5 })]
    public void Standard_notes_and_both_mappings_are_positional(ArrangementType type, int strings, int[] notes, int[] slots)
    {
        Assert.Equal(notes, RsxrStringLayout.StandardMidi(type, strings));
        for (var i = 0; i < strings; i++)
        {
            Assert.Equal(slots[i], RsxrStringLayout.SlotOf(i, strings, type));
            Assert.Equal(i, RsxrStringLayout.StringOf(slots[i], strings, type));
        }

        Assert.Equal(strings > (type == ArrangementType.Bass ? 4 : 6),
            RsxrStringLayout.IsExtended(new Arrangement { ArrangementType = type, StringCount = strings }));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 5)]
    [InlineData(ArrangementType.Lead, 9)]
    [InlineData(ArrangementType.Bass, 7)]
    [InlineData(ArrangementType.Vocal, 6)]
    public void Unsupported_standard_rows_throw(ArrangementType type, int strings) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RsxrStringLayout.StandardMidi(type, strings));

    [Theory]
    [InlineData(5, new[] { -7, -4, -3, -6, -1 }, new[] { -2, 1, 2, -1, 3 })]
    [InlineData(6, new[] { -7, -4, -3, -6, -1, -5 }, new[] { -2, 1, 2, -1, 3, -1 })]
    public void Bass_tuning_keeps_the_sounding_pitch(int strings, int[] chart, int[] expected)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = strings };
        foreach (var offset in chart) arrangement.TuningPitches.Add(new TuningOffset(offset));
        Assert.Equal(expected, RsxrStringLayout.TuningOffsets(arrangement));
    }

    [Theory]
    [InlineData(ArrangementType.Lead, 9, "'Wide' has 9 strings; RS Extended Range supports at most 8 for this instrument.")]
    [InlineData(ArrangementType.Bass, 7, "'Wide' has 7 strings; RS Extended Range supports at most 6 for this instrument.")]
    public void Wide_arrangements_are_refused_with_their_name(ArrangementType type, int strings, string message) =>
        Assert.Equal(message, RsxrStringLayout.ExportRefusal(new Arrangement { Name = "Wide", ArrangementType = type, StringCount = strings }));

    [Theory]
    [InlineData(-25)]
    [InlineData(25)]
    public void Extended_offsets_outside_the_bounds_are_refused(int offset)
    {
        var arrangement = new Arrangement { Name = "Low", ArrangementType = ArrangementType.Lead, StringCount = 7 };
        arrangement.TuningPitches.Add(new TuningOffset(offset));
        Assert.Equal($"'Low' has tuning offset {offset} on string 0; RS Extended Range requires offsets from -24 to 24.",
            RsxrStringLayout.ExportRefusal(arrangement));
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public void Boundary_offsets_are_accepted(int offset)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 7 };
        arrangement.TuningPitches.Add(new TuningOffset(offset));
        Assert.Null(RsxrStringLayout.ExportRefusal(arrangement));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Bass_limits_apply_to_the_sounding_pitch_offset(int strings)
    {
        var arrangement = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, StringCount = strings };
        foreach (var offset in Enumerable.Repeat(-5, strings)) arrangement.TuningPitches.Add(new TuningOffset(offset));
        arrangement.TuningPitches[0] = new TuningOffset(20);
        Assert.Equal("'Bass' has tuning offset 25 on string 0; RS Extended Range requires offsets from -24 to 24.",
            RsxrStringLayout.ExportRefusal(arrangement));
    }

    [Fact]
    public void Stock_offsets_are_not_restricted_by_the_extended_target()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.TuningPitches.Add(new TuningOffset(25));
        Assert.Null(RsxrStringLayout.ExportRefusal(arrangement));
    }
}
