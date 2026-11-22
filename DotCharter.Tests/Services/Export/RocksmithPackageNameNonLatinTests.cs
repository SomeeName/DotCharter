using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>#847: a title in another script never leaves an empty file-name segment; a derived name follows the title.</summary>
public sealed class RocksmithPackageNameNonLatinTests
{
    private static ChartProject Project(string artist, string title)
    {
        var project = new ChartProject();
        project.Chart.Artist = artist;
        project.Chart.SongName = title;
        return project;
    }

    [Fact]
    public void A_title_with_no_latin_letter_gets_a_stable_fallback_not_an_empty_segment()
    {
        var name = RocksmithPackageName.For(Project("SYU", "愛傷"), "1");

        Assert.DoesNotContain("__", name, StringComparison.Ordinal);
        Assert.Matches(@"^SYU_Song-[0-9A-F]{4}_v1_p\.psarc$", name);
        Assert.Equal(name, RocksmithPackageName.For(Project("SYU", "愛傷"), "1"));
        Assert.NotEqual(name, RocksmithPackageName.For(Project("SYU", "別れ"), "1"));
    }

    [Fact]
    public void An_artist_with_no_latin_letter_gets_a_fallback_too() =>
        Assert.Matches(@"^Artist-[0-9A-F]{4}_Aishou_v1_p\.psarc$", RocksmithPackageName.For(Project("無", "Aishou"), "1"));

    [Fact]
    public void The_romanised_title_names_the_file() =>
        Assert.Equal("SYU_Aishou_v1_p.psarc", RocksmithPackageName.For(Project("SYU", "Aishou"), "1"));

    [Theory]
    [InlineData("SYU_Song-1234_v1_p.psarc", "SYU_Aishou_v1_p.psarc", "SYU_Song-1234_v1_p.psarc", "SYU_Aishou_v1_p.psarc")]
    [InlineData("SYU_Song-1234_v1_m.psarc", "SYU_Aishou_v1_p.psarc", "SYU_Song-1234_v1_p.psarc", "SYU_Aishou_v1_m.psarc")]
    [InlineData("SYU_Song-1234_v1_rsxr_p.psarc", "SYU_Aishou_v1_p.psarc", "SYU_Song-1234_v1_p.psarc", "SYU_Aishou_v1_rsxr_p.psarc")]
    public void A_derived_output_name_follows_the_new_title(string file, string now, string was, string expected) =>
        Assert.Equal(Path.Combine("out", expected), RocksmithReleaseName.Retarget(Path.Combine("out", file), was, now));

    [Fact]
    public void A_typed_output_name_is_left_alone() =>
        Assert.Null(RocksmithReleaseName.Retarget(Path.Combine("out", "my-own_p.psarc"), "SYU_Song-1234_v1_p.psarc", "SYU_Aishou_v1_p.psarc"));
}
