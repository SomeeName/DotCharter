using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     DLC Builder's release file name (<c>ReleasePackageBuilder.fs</c>): authored sort values or the names as written, then
///     <c>StringValidator.fileName</c>, then <c>_v&lt;version with . as _&gt;</c>.
/// </summary>
public sealed class RocksmithReleaseNameTests
{
    [Theory]
    // a leading article stays (owner decision; DLC Builder's convertToSortField would drop it)
    [InlineData("Ave Mujica", "", "A Song Of Romance", "", "1", "Ave-Mujica_A-Song-Of-Romance_v1")]
    [InlineData("Ave Mujica", "", "A Song Of Romance", "", "", "Ave-Mujica_A-Song-Of-Romance_v1")]
    // authored sort values win, and a version's dots become underscores
    [InlineData("The Beatles", "Beatles", "Help!", "", "1.2.3", "Beatles_Help_v1_2_3")]
    [InlineData("Ave Mujica", "", "Song", "A Song Of Romance", "1", "Ave-Mujica_A-Song-Of-Romance_v1")]
    // a sort that only mirrors the name is not authored
    [InlineData("The Who", "The Who", "Baba O'Riley", "", "2", "The-Who_Baba-ORiley_v2")]
    // punctuation goes, diacritics fold, whitespace runs become one dash
    [InlineData("Blink-182", "", "(Don't) Panic", "", "1", "Blink-182_Dont-Panic_v1")]
    [InlineData("Mötley Crüe", "", "Kickstart My Heart", "", "1", "Motley-Crue_Kickstart-My-Heart_v1")]
    [InlineData("Beyoncé", "", "Crazy   In  Love", "", "1", "Beyonce_Crazy-In-Love_v1")]
    public void The_file_name_follows_DLC_Builder(string artist, string artistSort, string title, string titleSort,
        string version, string expected) =>
        Assert.Equal(expected, RocksmithReleaseName.Create(artist, artistSort, title, titleSort, version));

    [Fact]
    public void A_chart_is_named_from_its_artist_title_and_version()
    {
        var project = new ChartProject();
        project.Chart.Artist = "The Who";
        project.Chart.ArtistSort = "The Who";
        project.Chart.SongName = "Baba O'Riley";

        Assert.Equal("The-Who_Baba-ORiley_v1", RocksmithReleaseName.For(project, "1"));
        Assert.Equal("The-Who_Baba-ORiley_v3_1", RocksmithReleaseName.For(project, "3.1"));

        project.Chart.ArtistSort = "Who, The";
        Assert.Equal("Who-The_Baba-ORiley_v1", RocksmithReleaseName.For(project, "1"));
    }
}
