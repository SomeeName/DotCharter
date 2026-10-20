using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     Names a person reads, not identifiers: every project saves as <c>project.rscp</c>,
///     so the file name called every chart "project", and the stats store keys an
///     arrangement as <c>Type:Name</c>, which Home printed as "Lead:Lead".
/// </summary>
public sealed class ProjectDisplayNameTests
{
    [Fact]
    public void A_standard_project_file_is_named_by_its_song_then_its_folder()
    {
        var path = Path.Combine("C:", "Charts", "Polkadot Stingray - Sakasma", "project.rscp");

        Assert.Equal("Polkadot Stingray - Sakasma", ProjectDisplayName.For(path));
        Assert.Equal("Ultra Pop - Sakasma",
            ProjectDisplayName.For(path, new ChartData { Artist = "Ultra Pop", SongName = "Sakasma" }));
        Assert.Equal("Sakasma", ProjectDisplayName.For(path, new ChartData { Artist = "", SongName = "Sakasma" }));
    }

    [Fact]
    public void Any_other_file_keeps_its_own_name_and_no_path_is_no_name()
    {
        Assert.Equal("my chart", ProjectDisplayName.For(Path.Combine("C:", "x", "my chart.rscp")));
        Assert.Equal("", ProjectDisplayName.For(null));
    }

    [Theory]
    [InlineData("Lead:Lead", "Lead")]
    [InlineData("Rhythm:Rhythm 2", "Rhythm 2")]
    [InlineData("Bass", "Bass")]
    [InlineData("Notatype:Lead", "Notatype:Lead")]
    public void A_stats_arrangement_key_reads_as_the_arrangement_name(string key, string label)
    {
        var stats = new SongPlayStats("song", key, 1, 0, 0, 0, LetterGrade.F, DateTime.UtcNow, "Song");
        Assert.Equal(label, stats.ArrangementLabel);
    }
}
