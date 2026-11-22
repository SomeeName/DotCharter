using DotCharter.Services.Editing;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The song fields DLC Builder shows (sort names, DLC key, version) end to end: the project file keeps
///     them through a save and load (parser AND writer), both exporters write them, a field left at its
///     derived value stays blank, and a built package names the running tool version.
/// </summary>
public sealed class SongMetadataFieldsTests
{
    private static ChartProject Project() => new()
    {
        Chart = new ChartData
        {
            Artist = "The Polkadot Stingray",
            SongName = "The Sakasama",
            AlbumName = "Ice Wall",
            AlbumYear = "2026",
            Charter = "Alex",
            ArtistSort = "Polkadot Stingray, The",
            TitleSort = "Sakasama, The",
            AlbumSort = "Wall, Ice",
            DlcKey = "DCSakasamaKey",
            Version = "3"
        }
    };

    [Fact]
    public void TheProjectFile_KeepsTheSortNamesKeyAndVersionThroughASaveAndLoad()
    {
        var folder = Directory.CreateTempSubdirectory("dotcharter-fields-").FullName;
        try
        {
            var path = Path.Combine(folder, "song.rscp");
            var serializer = new RscpChartSerializer();
            serializer.Save(path, Project());
            var chart = serializer.Load(path).Chart;

            Assert.Equal("Polkadot Stingray, The", chart.ArtistSort);
            Assert.Equal("Sakasama, The", chart.TitleSort);
            Assert.Equal("Wall, Ice", chart.AlbumSort);
            Assert.Equal("DCSakasamaKey", chart.DlcKey);
            Assert.Equal("3", chart.Version);
        }
        finally
        {
            BestEffortDelete.Directory(folder);
        }
    }

    [Fact]
    public void AFieldLeftBlank_IsNotWrittenToTheProjectFile()
    {
        var folder = Directory.CreateTempSubdirectory("dotcharter-fields-").FullName;
        try
        {
            var path = Path.Combine(folder, "song.rscp");
            var project = Project();
            project.Chart.TitleSort = project.Chart.AlbumSort = project.Chart.DlcKey = "";
            new RscpChartSerializer().Save(path, project);

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("dcTitleSort", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dcAlbumSort", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dcDlcKey", text, StringComparison.Ordinal);
        }
        finally
        {
            BestEffortDelete.Directory(folder);
        }
    }

    [Fact]
    public void TheDlcBuilderProject_WritesTheAuthoredFields()
    {
        var dto = Rs2DlcProjectBuilder.Build(Project(), new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("song.ogg", "cover.jpg", 1000, "Alex"));

        Assert.Equal("DCSakasamaKey", dto.DlcKey);
        Assert.Equal("3", dto.Version);
        Assert.Equal("Sakasama, The", dto.Title.SortValue);
        Assert.Equal("Wall, Ice", dto.AlbumName.SortValue);
        Assert.Equal("Polkadot Stingray, The", dto.ArtistName.SortValue);
    }

    [Fact]
    public void TheDlcBuilderProject_DerivesWhatIsLeftBlank()
    {
        var project = Project();
        project.Chart.TitleSort = project.Chart.AlbumSort = project.Chart.DlcKey = "";
        project.Chart.Version = "";

        var dto = Rs2DlcProjectBuilder.Build(project, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("song.ogg", "cover.jpg", 1000, "Alex"));

        Assert.Equal("1", dto.Version);
        Assert.Equal("Sakasama", dto.Title.SortValue);
        Assert.Equal("Ice Wall", dto.AlbumName.SortValue);
        Assert.False(string.IsNullOrWhiteSpace(dto.DlcKey));
        Assert.NotEqual("DCSakasamaKey", dto.DlcKey);
    }

    [Fact]
    public void ThePackageMetadata_TakesTheAuthoredSortNamesAndKey()
    {
        var meta = RsPackageMetadataFactory.Create(Project(), Options());

        Assert.Equal("DCSakasamaKey", meta.DlcKey);
        Assert.Equal("Sakasama, The", meta.TitleSort);
        Assert.Equal("Wall, Ice", meta.AlbumSort);
        Assert.Equal("Polkadot Stingray, The", meta.ArtistSort);
    }

    [Fact]
    public void ThePackageMetadata_DerivesWhatIsLeftBlank_AndTheBuildOptionsKeyStillWins()
    {
        var project = Project();
        project.Chart.TitleSort = project.Chart.AlbumSort = project.Chart.DlcKey = "";

        var derived = RsPackageMetadataFactory.Create(project, Options());
        Assert.Equal("Sakasama", derived.TitleSort);
        Assert.Equal(SongMetadataDefaults.DlcKey("The Polkadot Stingray", "The Sakasama"), derived.DlcKey);

        var forced = RsPackageMetadataFactory.Create(Project(), Options("ForcedKey1"));
        Assert.Equal("ForcedKey1", forced.DlcKey);
    }

    [Fact]
    public void AValueEqualToItsDerivedDefault_IsStoredBlank()
    {
        Assert.Equal("", SongMetadataDefaults.Authored(" Sakasama ", SongMetadataDefaults.TitleSort("The Sakasama")));
        Assert.Equal("Wall, Ice", SongMetadataDefaults.Authored("Wall, Ice", SongMetadataDefaults.AlbumSort("Ice Wall")));
    }

    [Fact]
    public void ThePackagesToolkitVersion_NamesTheRunningVersion()
    {
        var text = RocksmithPackageBuilder.ToolkitVersionText("Alex", Options());

        Assert.StartsWith($"Toolkit version: DotCharter v{AppVersionInfo.Current}\n", text, StringComparison.Ordinal);
        Assert.Contains("Package Author: Alex\n", text, StringComparison.Ordinal);
        Assert.Equal($"DotCharter v{AppVersionInfo.Current}", SongMetadataDefaults.ToolVersion);
    }

    [Fact]
    public void ABytePinsFixedToolVersion_ReplacesTheRunningOne()
    {
        var options = new RocksmithBuildOptions
        {
            OutputPath = "x.psarc",
            AudioPath = "x.wem",
            Identity = new RocksmithBuildIdentity
            {
                ConversionTime = DateTime.UnixEpoch,
                Arrangements = [],
                GraphItems = [],
                MainBank = default,
                PreviewBank = default,
                ToolVersion = "1.0.0"
            }
        };

        Assert.StartsWith("Toolkit version: DotCharter v1.0.0\n", RocksmithPackageBuilder.ToolkitVersionText("Alex", options), StringComparison.Ordinal);
    }

    private static RocksmithBuildOptions Options(string? dlcKey = null) => new()
    {
        OutputPath = "x.psarc",
        AudioPath = "x.wem",
        DlcKey = dlcKey ?? ""
    };
}
