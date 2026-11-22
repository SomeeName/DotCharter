using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The song map draws its notes below the section band (a translation by the band's height,
///     lanes measured over the remaining body). The selection halo was drawn over the FULL height
///     with no translation, so a selected note's halo landed up to a lane above its note: on a
///     40 px map with six strings the lowest string's halo sat on the string above it.
/// </summary>
public sealed class MinimapSelectionGeometryTests
{
    [Fact]
    public void TheNoteBodyStartsBelowTheSectionBand()
    {
        var (top, height) = MinimapOverviewControl.NoteBody(40);

        Assert.True(top > 0, "the note body must start below the section band");
        Assert.Equal(40, top + height, 6);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void AMarkIsCentredInItsLane(int stringIndex, bool flip)
    {
        const int strings = 6;
        const double bodyHeight = 27;
        var lane = bodyHeight / strings;
        var expectedLane = flip ? strings - 1 - stringIndex : stringIndex;

        var top = MinimapOverviewControl.MarkTop(stringIndex, strings, bodyHeight, 2, flip);

        Assert.Equal((expectedLane + 0.5) * lane, top + 1, 6);
    }

}
