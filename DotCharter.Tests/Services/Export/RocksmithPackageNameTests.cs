using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

public sealed class RocksmithPackageNameTests
{
    private const string ArtistTitle = "Example-Band-The_A-Bright-Song";

    [Theory]
    [InlineData(false, false, false, "", "Example-Band-The_A-Bright-Song_v2_3_p.psarc")]
    [InlineData(false, true, false, "", "Example-Band-The_A-Bright-Song_v2_3_rsxr_p.psarc")]
    [InlineData(true, false, false, "", "Example-Band-The_A-Bright-Song_v2_3_m.psarc")]
    [InlineData(true, true, false, "", "Example-Band-The_A-Bright-Song_v2_3_rsxr_m.psarc")]
    [InlineData(false, false, true, "", "Example-Band-The_A-Bright-Song_test_p.psarc")]
    [InlineData(false, true, true, "", "Example-Band-The_A-Bright-Song_test_rsxr_p.psarc")]
    [InlineData(true, false, true, "", "Example-Band-The_A-Bright-Song_test_m.psarc")]
    [InlineData(true, true, true, "", "Example-Band-The_A-Bright-Song_test_rsxr_m.psarc")]
    [InlineData(false, false, true, "v4", "Example-Band-The_A-Bright-Song_test_v4_p.psarc")]
    [InlineData(false, true, true, "v4", "Example-Band-The_A-Bright-Song_test_v4_rsxr_p.psarc")]
    [InlineData(true, false, true, "v4", "Example-Band-The_A-Bright-Song_test_v4_m.psarc")]
    [InlineData(true, true, true, "v4", "Example-Band-The_A-Bright-Song_test_v4_rsxr_m.psarc")]
    public void Release_and_test_names_follow_the_artist_title_version_and_platform_order(
        bool mac, bool extended, bool test, string tag, string expected) =>
        Assert.Equal(expected, RocksmithPackageName.For(Project(), "2.3", mac, extended, test, tag));

    [Theory]
    [InlineData(false, "", "Example-Band-The_A-Bright-Song_v2_3")]
    [InlineData(true, "", "Example-Band-The_A-Bright-Song_test")]
    [InlineData(true, "v4", "Example-Band-The_A-Bright-Song_test_v4")]
    public void The_shared_stem_omits_platform_and_range_suffixes(bool test, string tag, string expected) =>
        Assert.Equal(expected, RocksmithPackageName.Stem(Project(), "2.3", test, tag));

    [Theory]
    [InlineData("Example-Band-The_A-Bright-Song_v1_p.psarc", true)]
    [InlineData("Example-Band-The_A-Bright-Song_v2_3_rsxr_p.psarc", true)]
    [InlineData("Example-Band-The_A-Bright-Song_v2_3_m.psarc", true)]
    [InlineData("Example-Band-The_A-Bright-Song_vbeta-2_p.psarc", true)]
    [InlineData("EXAMPLE-BAND-THE_A-BRIGHT-SONG_V4_P.PSARC", true)]
    [InlineData("Example-Band-The_A-Bright-Song_test_p.psarc", false)]
    [InlineData("Example-Band-The_A-Bright-Song_test_v2_rsxr_p.psarc", false)]
    [InlineData("syntheticsong_p.psarc", false)]
    [InlineData("Example-Band-The_A-Bright-Song_v_p.psarc", false)]
    [InlineData("Example-Band-The_A-Bright-Song_v1_rsxr_m.psarc", true)]
    [InlineData("Example-Band-The_A-Bright-Song_v1_rsxr_rsxr_p.psarc", false)]
    [InlineData("Example-Band-The_A-Bright-Song_v1_p.psarc.bak", false)]
    [InlineData("Example-Band-The_A-Bright-Song_v1$_p.psarc", false)]
    [InlineData("Example-Band-The_A-Bright-Song2_v1_p.psarc", false)]
    [InlineData("Other-Band_A-Bright-Song_v1_p.psarc", false)]
    public void Release_family_recognition_excludes_test_foreign_and_malformed_packages(string name, bool expected) =>
        Assert.Equal(expected, RocksmithPackageName.IsReleaseFor(Project(), Path.Combine("export", name)));

    [Fact]
    public void A_closed_game_test_plan_recognizes_new_and_legacy_names_without_scheduling_release_or_foreign_files()
    {
        var ours = new[]
        {
            ArtistTitle + "_test_p.psarc", ArtistTitle + "_test_v2_rsxr_p.psarc", ArtistTitle + "_test_v4_m.psarc",
            ArtistTitle + "_test_rsxr_m.psarc",
            "syntheticsong.psarc", "syntheticsong_v5_p.psarc"
        };
        var foreign = new[]
        {
            ArtistTitle + "_v9_p.psarc", ArtistTitle + "_test_v99_notes.txt", ArtistTitle + "_test_extra_p.psarc",
            ArtistTitle + "_test.psarc", "syntheticsong2_p.psarc"
        };

        var plan = RocksmithTestExport.Plan(Project(), "SyntheticSong", ours.Concat(foreign), false);

        Assert.Equal(ArtistTitle + "_test", plan.FileStem);
        Assert.Equal("SyntheticSong", plan.DlcKey);
        Assert.Equal("", plan.VersionTag);
        Assert.Equal("", plan.TitleSuffix);
        Assert.Equal(ours, plan.Superseded);
        Assert.Equal("test", RocksmithTestExport.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_game_versioning_uses_the_latest_number_across_new_and_legacy_test_names(bool legacyNewest)
    {
        var names = new[]
        {
            ArtistTitle + (legacyNewest ? "_test_v7_rsxr_p.psarc" : "_test_v12_rsxr_p.psarc"),
            legacyNewest ? "syntheticsong_v12_m.psarc" : "syntheticsong_v7_m.psarc",
            ArtistTitle + "_v99_p.psarc", ArtistTitle + "_test_v99_notes.txt"
        };

        var plan = RocksmithTestExport.Plan(Project(), "SyntheticSong", names, true);

        Assert.Equal(ArtistTitle + "_test_v13", plan.FileStem);
        Assert.Equal("SyntheticSongv13", plan.DlcKey);
        Assert.Equal("v13", plan.VersionTag);
        Assert.Equal(" v13", plan.TitleSuffix);
        Assert.Empty(plan.Superseded);
    }

    [Theory]
    [InlineData(0, "v1")]
    [InlineData(1, "v2")]
    [InlineData(2, "v3")]
    [InlineData(3, "v4")]
    public void Unnumbered_new_and_legacy_tests_keep_the_existing_count_fallback(int earlier, string tag)
    {
        var names = new[] { ArtistTitle + "_test_p.psarc", ArtistTitle + "_test_m.psarc", "syntheticsong_p.psarc" };

        var plan = RocksmithTestExport.Plan(Project(), "SyntheticSong", names.Take(earlier), true);

        Assert.Equal(tag, plan.VersionTag);
        Assert.Equal(ArtistTitle + "_test_" + tag, plan.FileStem);
    }

    [Fact]
    public void Planning_does_not_change_authored_metadata_or_use_the_stored_key_instead_of_the_passed_key()
    {
        var project = Project();
        project.Chart.DlcKey = "DifferentStoredKey";

        var plan = RocksmithTestExport.Plan(project, "SyntheticSong", [], true);

        Assert.Equal("SyntheticSongv1", plan.DlcKey);
        Assert.Equal(ArtistTitle + "_test_v1", plan.FileStem);
        Assert.Equal("DifferentStoredKey", project.Chart.DlcKey);
        Assert.Equal("A Bright Song!", project.Chart.SongName);
        Assert.Equal("The Example Band", project.Chart.Artist);
    }

    [Theory]
    [InlineData("?ASCIIKey", false)]
    [InlineData("!!!", false)]
    [InlineData("?ASCIIKey", true)]
    [InlineData("!!!", true)]
    public void File_name_hints_do_not_validate_or_modify_the_authored_dlc_key(string key, bool test)
    {
        var project = Project();
        project.Chart.DlcKey = key;

        var name = RocksmithPackageName.For(project, "2.3", test: test);

        Assert.Equal(ArtistTitle + (test ? "_test_p.psarc" : "_v2_3_p.psarc"), name);
        Assert.Equal(key, project.Chart.DlcKey);
    }

    [Theory]
    [InlineData(false, false, "_v1_p.psarc")]
    [InlineData(false, true, "_v1_rsxr_p.psarc")]
    [InlineData(true, false, "_v1_m.psarc")]
    [InlineData(true, true, "_v1_rsxr_m.psarc")]
    public void The_reserved_range_tag_is_normalized_even_when_a_version_ends_with_it(bool mac, bool extended, string suffix) =>
        Assert.Equal(ArtistTitle + suffix, RocksmithPackageName.For(Project(), "1.rsxr", mac, extended));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Version_renaming_recognizes_the_canonical_old_name_after_range_tag_normalization(bool mac, bool extended)
    {
        var project = Project();
        var previous = RocksmithPackageName.For(project, "1.rsxr", mac, extended);
        Assert.Equal(RocksmithPackageName.For(project, "2", mac, extended),
            RocksmithReleaseName.Rename(previous, project, "1.rsxr", "2", mac));
    }

    private static ChartProject Project() => new()
    {
        Chart = { Artist = "The Example Band", ArtistSort = "Example Band, The", SongName = "A Bright Song!", DlcKey = "SyntheticSong" }
    };
}
