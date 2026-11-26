using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public sealed class GpMarkerImportSanitizeTests
{
    [Theory]
    [InlineData("Verse 2", "verse")]
    [InlineData("Pre-Chorus", "prechorus")]
    [InlineData("CHORUS #3", "chorus")]
    [InlineData("Guitar Solo", "")]
    public void A_numbered_or_hyphenated_marker_still_finds_its_section(string marker, string expected)
    {
        var ev = GpMarkerImport.CreateMarkerEvent(new FractionalPosition(4), marker);

        Assert.NotNull(ev);
        Assert.Equal(expected, ev.Section);
    }

    [Fact]
    public void A_marker_name_Rocksmith_cannot_show_is_imported_under_its_legal_spelling()
    {
        var ev = GpMarkerImport.CreateMarkerEvent(new FractionalPosition(4), "Refrän (2x)");

        Assert.NotNull(ev);
        Assert.True(RsPhraseName.IsLegal(ev.Phrase), ev.Phrase);
    }

    [Fact]
    public void A_second_marker_at_the_same_position_is_dropped()
    {
        var arrangement = new Arrangement();

        GpMarkerImport.AddMarker(arrangement, GpMarkerImport.CreateMarkerEvent(new FractionalPosition(8), "Intro"));
        GpMarkerImport.AddMarker(arrangement, GpMarkerImport.CreateMarkerEvent(new FractionalPosition(8), "Riff A"));
        GpMarkerImport.AddMarker(arrangement, GpMarkerImport.CreateMarkerEvent(new FractionalPosition(16), "Intro"));

        Assert.Equal(2, arrangement.EventPoints.Count);
        Assert.Equal("Intro", arrangement.EventPoints[0].Phrase);
        Assert.Equal(["Intro"], arrangement.Phrases);
    }
}
