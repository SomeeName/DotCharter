using DotCharter.Services.Package.DcSong;
using DotCharter.Tests.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>The packaged chart points into the package and nowhere else.</summary>
public sealed class DcSongChartTests
{
    [Fact]
    public void EveryFileReference_PointsIntoThePackage_AndTheProjectIsUntouched()
    {
        var project = RichProjectFactory.Build();
        project.Chart.CoverArtPath = @"C:\Users\someone\Pictures\cover.png";
        var before = ChartProjectCloner.Clone(project);

        var packaged = DcSongChart.ForPackage(project, "audio/song.opus", "images/cover.png",
            [("bass", "stems/bass.opus"), ("vocals", "stems/vocals.opus")]);

        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
        Assert.Equal("audio/song.opus", packaged.AudioFileName);
        Assert.Equal("images/cover.png", packaged.Chart.CoverArtPath);
        Assert.Empty(packaged.ArrangementFiles);

        // bass keeps its mixer state; drums is not in the package; vocals is new.
        Assert.Equal(new[] { "bass", "vocals" }, packaged.Stems.Select(s => s.Name));
        Assert.Equal(0.5, packaged.Stems[0].Volume);
        Assert.All(packaged.Stems, s => Assert.True(s.Local));
        Assert.All(packaged.Stems, s => Assert.StartsWith("stems/", s.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheKeptArrangements_TravelInOrder_AndTheProjectKeepsAll()
    {
        var project = RichProjectFactory.Build();
        while (project.Arrangements.Count < 3) project.Arrangements.Add(new Arrangement { Name = "Extra " + project.Arrangements.Count });
        var names = project.Arrangements.Select(a => a.Name).ToList();

        var packaged = DcSongChart.ForPackage(project, "audio/song.opus", null, [], [2, 0]);

        Assert.Equal(new[] { names[0], names[2] }, packaged.Arrangements.Select(a => a.Name));
        Assert.Equal(names, project.Arrangements.Select(a => a.Name));
        Assert.Equal(names.Count, DcSongChart.ForPackage(project, "audio/song.opus", null, []).Arrangements.Count);
    }

    [Fact]
    public void NoCover_ClearsTheAuthorsPath()
    {
        var project = RichProjectFactory.Build();
        var packaged = DcSongChart.ForPackage(project, "audio/song.flac", null, []);

        Assert.Equal("", packaged.Chart.CoverArtPath);
        Assert.Empty(packaged.Stems);
    }
}
