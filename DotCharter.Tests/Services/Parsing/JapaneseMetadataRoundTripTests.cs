using System.Text.Json.Nodes;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     The song's title and artist in their ORIGINAL script, end to end.
///     Rocksmith carries exactly this pair (<c>JapaneseSongName</c> /
///     <c>JapaneseArtistName</c>) and DLC Builder edits it; before 2026-08-21
///     DotCharter had nowhere to put it, so a psarc round-trip threw it away in
///     BOTH directions and the <c>.rs2dlc</c> fields — which had existed on the
///     model since it was written — were never filled by anything.
///     <para>
///     There is deliberately no Japanese ALBUM name: the manifest has no field
///     for one, so storing it would be a value we could never write out.
///     </para>
/// </summary>
public sealed class JapaneseMetadataRoundTripTests
{
    private const string JpTitle = "八月、某、月明かり";
    private const string JpArtist = "ヨルシカ";

    private static ChartProject ProjectWithOriginalNames() => new()
    {
        Chart = new ChartData
        {
            SongName = "August, A Certain, Moonlight",
            Artist = "Yorushika",
            ArtistSort = "Yorushika",
            AlbumName = "That's Why I Gave Up on Music",
            JapaneseTitle = JpTitle,
            JapaneseArtist = JpArtist
        }
    };

    /// <summary>
    ///     The `.rscp` IS the project, so a model field with no writer is
    ///     silently DELETED on every save. Parser and writer were added in the
    ///     same change; this is the field-level assertion that keeps them
    ///     together.
    /// </summary>
    [Fact]
    public void TheProjectFile_CarriesBothNamesThroughASaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-jp-{Guid.NewGuid():N}.rscp");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, ProjectWithOriginalNames());
            var reloaded = serializer.Load(path);

            Assert.Equal(JpTitle, reloaded.Chart.JapaneseTitle);
            Assert.Equal(JpArtist, reloaded.Chart.JapaneseArtist);
            // The Latin pair is not replaced by it — both are kept.
            Assert.Equal("Yorushika", reloaded.Chart.Artist);
            Assert.Equal("August, A Certain, Moonlight", reloaded.Chart.SongName);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    [Fact]
    public void AProjectWithoutThem_WritesNoElementAtAll()
    {
        // The dc* extension rule: only written when non-empty, so a plain
        // rscharter file stays byte-identical.
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-jp-{Guid.NewGuid():N}.rscp");
        try
        {
            new RscpChartSerializer().Save(path,
                new ChartProject { Chart = new ChartData { Artist = "Someone" } });
            var xml = File.ReadAllText(path);

            Assert.DoesNotContain("dcJapaneseTitle", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("dcJapaneseArtist", xml, StringComparison.Ordinal);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>
    ///     <c>ChartProjectCloner</c> is the single snapshot source of truth — a
    ///     field missing from it vanishes from undo history and from torn-save
    ///     protection, silently.
    /// </summary>
    [Fact]
    public void TheSnapshotCloner_CarriesBothNames()
    {
        var clone = ChartProjectCloner.Clone(ProjectWithOriginalNames());

        Assert.Equal(JpTitle, clone.Chart.JapaneseTitle);
        Assert.Equal(JpArtist, clone.Chart.JapaneseArtist);
    }

    [Fact]
    public void TheRocksmithManifest_EmitsBothAttributes()
    {
        var attr = new JsonObject();
        RsManifestGenerator.AddOriginalScriptNames(attr, JpTitle, JpArtist);

        Assert.Equal(JpTitle, attr["JapaneseSongName"]!.GetValue<string>());
        Assert.Equal(JpArtist, attr["JapaneseArtistName"]!.GetValue<string>());
    }

    [Fact]
    public void TheRocksmithManifest_OmitsThemWhenTheProjectHasNone()
    {
        // Official DLC omits the attributes entirely for Latin-titled songs, and
        // writing an empty string is not the same as omitting the key.
        var attr = new JsonObject();
        RsManifestGenerator.AddOriginalScriptNames(attr, "", "   ");

        Assert.False(attr.ContainsKey("JapaneseSongName"));
        Assert.False(attr.ContainsKey("JapaneseArtistName"));
    }
}
