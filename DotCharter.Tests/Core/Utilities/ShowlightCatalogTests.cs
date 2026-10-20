namespace DotCharter.Tests.Core.Utilities;

public class ShowlightCatalogTests
{
    [Theory]
    [InlineData(null, 57, "BEAMS_BLUE_PLUS", 57)]
    [InlineData("BEAMS_BLUE_PLUS", 0, "BEAMS_BLUE_PLUS", 57)]
    [InlineData("FOG_LIGHT_GREEN", 57, "BEAMS_BLUE_PLUS", 57)]
    [InlineData("LASERS_OFF", 66, "LASERS_OFF", 67)]
    public void NormalizeTypeAndNote_ResolveToExpectedDefinition(string? type, int note, string expectedType,
        int expectedNote)
    {
        Assert.Equal(expectedType, ShowlightCatalog.NormalizeType(type, note));
        Assert.Equal(expectedNote, ShowlightCatalog.NormalizeNote(type, note));
    }

    [Fact]
    public void TryGetLane_KnownType_ReturnsExpectedLane()
    {
        var found = ShowlightCatalog.TryGetLane("LASERS_ON", 0, out var lane);

        Assert.True(found);
        Assert.Equal(ShowlightLane.Lasers, lane);
    }

    [Fact]
    public void NormalizeUnknownValues_PreservesRawDataWhenDefinitionIsMissing()
    {
        Assert.Equal("CUSTOM_LIGHT", ShowlightCatalog.NormalizeType("CUSTOM_LIGHT"));
        Assert.Equal(99, ShowlightCatalog.NormalizeNote(null, 99));
        Assert.Equal("Note 99", ShowlightCatalog.GetDisplayName(null, 99));
    }
}
