using System.Collections.ObjectModel;
using System.Text.Json;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export.DlcBuilder;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Pins the <c>.rs2dlc</c> writer against DLC Builder's own schema.
/// </summary>
/// <remarks>
///     The reference is the real file
///     <c>Rocksmith2014.NET/tests/Rocksmith2014.IntegrationTests/project/Integration_Test.rs2dlc</c>
///     in the git-ignored reference checkout. It is NOT read at test time (that
///     checkout is not part of the build) — the shape it proved is asserted here
///     instead, because the three things a hand-written serializer gets wrong are
///     invisible in C#: the union envelope, numeric enums, and absent-vs-null.
/// </remarks>
public sealed class Rs2DlcProjectTests
{
    private static Rs2DlcProjectBuilder.Rs2DlcBuildOptions Options(
        string audio = "song.ogg", string art = "cover.jpg", double? lengthMs = 214_000) =>
        new(audio, art, lengthMs, "Alex");

    private static ChartProject Project()
    {
        var project = new ChartProject
        {
            AudioFileName = "song.ogg",
            Chart =
            {
                Artist = "The Polkadot Stingray",
                SongName = "Sakasama",
                AlbumName = "Ice Wall",
                AlbumYear = "2026",
                Charter = "Alex",
                CoverArtPath = "cover.jpg"
            }
        };

        project.Arrangements.Add(new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Subtype = ArrangementSubtype.Main,
            TuningPitches = new ObservableCollection<TuningOffset>(
                [new(0), new(0), new(0), new(0), new(0), new(0)])
        });
        project.Arrangements.Add(new Arrangement
        {
            ArrangementType = ArrangementType.Bass,
            Subtype = ArrangementSubtype.Alternate,
            PickedBass = true,
            TuningPitches = new ObservableCollection<TuningOffset>(
                [new(-2), new(0), new(0), new(0)])
        });

        project.ArrangementFiles.Add("1_Lead_Main_RS2.xml");
        project.ArrangementFiles.Add("2_Bass_Alternate_RS2.xml");

        return project;
    }

    // ---- schema ----------------------------------------------------------

    [Fact]
    public void AnArrangement_IsWrittenAsTheFSharpUnionEnvelope_NotAFlatObject()
    {
        // JsonFSharpConverter's default AdjacentTag form. A flat object here
        // deserializes to nothing at all in DLC Builder.
        var json = Rs2DlcWriter.Serialize(Rs2DlcProjectBuilder.Build(Project(), Options()));
        using var document = JsonDocument.Parse(json);

        var first = document.RootElement.GetProperty("Arrangements")[0];
        Assert.Equal("Instrumental", first.GetProperty("Case").GetString());
        Assert.Equal(JsonValueKind.Array, first.GetProperty("Fields").ValueKind);
        Assert.Equal(1, first.GetProperty("Fields").GetArrayLength());
        Assert.Equal(Path.Combine("DTRS XML", "1_Lead_Main_RS2.xml"),
            first.GetProperty("Fields")[0].GetProperty("XML").GetString());
    }

    [Fact]
    public void Enums_AreWrittenAsNumbers_NotNames()
    {
        // A JsonStringEnumConverter on the writer's options would produce
        // "Bass"/"Alternative" here, which upstream reads back as the default.
        var json = Rs2DlcWriter.Serialize(Rs2DlcProjectBuilder.Build(Project(), Options()));
        using var document = JsonDocument.Parse(json);

        var bass = document.RootElement.GetProperty("Arrangements")[1].GetProperty("Fields")[0];
        Assert.Equal(3, bass.GetProperty("Name").GetInt32());       // ArrangementName.Bass
        Assert.Equal(4, bass.GetProperty("RouteMask").GetInt32());  // RouteMask.Bass
        Assert.Equal(1, bass.GetProperty("Priority").GetInt32());   // Priority.Alternative
        Assert.True(bass.GetProperty("BassPicked").GetBoolean());
    }

    [Fact]
    public void AnUnsetOptionalField_IsAbsent_NotNull()
    {
        // Upstream writes with ignoreNull = true. A literal null makes
        // FSharp.SystemTextJson's option reader throw rather than default.
        var project = Project();
        var dto = Rs2DlcProjectBuilder.Build(project, Options(lengthMs: null));
        using var document = JsonDocument.Parse(Rs2DlcWriter.Serialize(dto));

        Assert.False(document.RootElement.TryGetProperty("AudioFileLength", out _));
        Assert.False(document.RootElement.TryGetProperty("PitchShift", out _));
        Assert.False(document.RootElement.TryGetProperty("JapaneseTitle", out _));
        // An empty ARRAY still writes, which is what the reference file shows.
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("IgnoredIssues").ValueKind);
    }

    [Fact]
    public void ItRoundTripsThroughAReaderThatKnowsOnlyTheSchema()
    {
        var dto = Rs2DlcProjectBuilder.Build(Project(), Options());
        var reread = JsonSerializer.Deserialize<Rs2DlcProjectDto>(Rs2DlcWriter.Serialize(dto));

        Assert.NotNull(reread);
        Assert.Equal("1", reread.Version);
        Assert.Equal(dto.DlcKey, reread.DlcKey);
        Assert.Equal("The Polkadot Stingray", reread.ArtistName.Value);
        Assert.Equal("Polkadot Stingray", reread.ArtistName.SortValue);
        Assert.Equal(2026, reread.Year);
        Assert.Equal(214.0, reread.AudioFileLength!.Value, 3);
        Assert.Equal(2, reread.Arrangements.Length);
    }

    // ---- content ---------------------------------------------------------

    [Fact]
    public void PathsAreRelativeToTheProjectRoot_BecauseThatIsWhereTheProjectFileLives()
    {
        // The .rs2dlc sits beside the .rscp and the audio, so those are bare
        // names and the arrangements are reached through the bundle FOLDER. It
        // deliberately does not live inside that folder: the bundle is swapped in
        // by renaming the old one aside, which would move this file with it.
        var dto = Rs2DlcProjectBuilder.Build(Project(), Options());

        Assert.Equal(Path.Combine("DTRS XML", "1_Lead_Main_RS2.xml"),
            ((Rs2DlcInstrumentalDto)dto.Arrangements[0].Fields[0]).Xml);
        Assert.Equal("song.ogg", dto.AudioFile.Path);
        Assert.Equal("cover.jpg", dto.AlbumArtFile);
        Assert.Equal("song_preview.ogg", dto.AudioPreviewFile.Path);
    }

    [Fact]
    public void AVocalTrackWithNoLyrics_IsNotNamed_BecauseTheBundleDoesNotWriteOne()
    {
        var project = Project();
        project.VocalTracks.Add(new VocalTrack { Name = "Empty" });

        var withoutLyrics = Rs2DlcProjectBuilder.Build(project, Options());
        Assert.DoesNotContain(withoutLyrics.Arrangements, a => a.Case == "Vocals");

        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "la" });
        project.VocalTracks[0].Phrases.Add(phrase);

        var withLyrics = Rs2DlcProjectBuilder.Build(project, Options());
        var vocals = Assert.Single(withLyrics.Arrangements, a => a.Case == "Vocals");
        Assert.Equal(Path.Combine("DTRS XML", "1_Vocals_Empty_RS2.xml"),
            ((Rs2DlcVocalsDto)vocals.Fields[0]).Xml);
    }

    [Theory]
    // 4-string bass: zero-padded to the six slots RS always stores.
    [InlineData(new[] { -2, 0, 0, 0 }, new short[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0 }, new short[] { 0, 0, 0, 0, 0, 0 })]
    // 7-string: truncated to what the format can spell.
    [InlineData(new[] { -5, -5, -5, -5, -5, -5, -5 }, new short[] { -5, -5, -5, -5, -5, -5 })]
    public void TheTuningVector_IsAlwaysSixSlots(int[] steps, short[] expected)
    {
        var arrangement = new Arrangement
        {
            TuningPitches = new ObservableCollection<TuningOffset>(steps.Select(s => new TuningOffset(s)))
        };

        Assert.Equal(expected, Rs2DlcProjectBuilder.TuningVector(arrangement));
    }

    [Fact]
    public void TheKeyAndTheIds_AreStable_SoResavingRewritesAnIdenticalFile()
    {
        // A random key/GUID would churn the folder on every Ctrl+S and make DLC
        // Builder read each import as a different DLC.
        var first = Rs2DlcProjectBuilder.Build(Project(), Options());
        var second = Rs2DlcProjectBuilder.Build(Project(), Options());

        Assert.Equal(Rs2DlcWriter.Serialize(first), Rs2DlcWriter.Serialize(second));
        Assert.NotEqual(Guid.Empty, ((Rs2DlcInstrumentalDto)first.Arrangements[0].Fields[0]).PersistentId);
        Assert.NotEqual(0, ((Rs2DlcInstrumentalDto)first.Arrangements[0].Fields[0]).MasterId);
    }

    [Fact]
    public void TheFileName_IsArtistUnderscoreTitle_DlcBuilderStyle()
    {
        var project = Project();
        project.Chart.Artist = "Polkadot Stingray";
        Assert.Equal("Polkadot-Stingray_Sakasama.rs2dlc", Rs2DlcProjectBuilder.FileNameFor(project));
    }

    // ---- the string rules ------------------------------------------------

    [Theory]
    [InlineData("The Beatles", "Beatles")]
    [InlineData("A Perfect Circle", "Perfect Circle")]
    [InlineData("An Evening", "Evening")]
    [InlineData("Mötley Crüe", "Motley Crue")]   // official sort value
    [InlineData("blink-182", "Blink 182")]       // official sort value
    [InlineData("!!!", "")]                      // sortField strips leading non-alnum
    [InlineData("Radiohead", "Radiohead")]
    public void ArtistSortValues_FollowDlcBuildersOwnTable(string artist, string expected) =>
        Assert.Equal(expected, Rs2DlcStrings.ArtistSortValue(artist));

    [Fact]
    public void TheDlcKey_IsTwoCharterCharsThenFiveOfEach_AndNeverShorterThanFive()
    {
        // "Al" + "Polka" (of PolkadotStingray) + "Sakas" (of Sakasama)
        Assert.Equal("AlPolkaSakas",
            Rs2DlcStrings.CreateDlcKey("Alex", "Polkadot Stingray", "Sakasama", () => 'x'));
        // Nothing to build from ⇒ padded with the injected letter.
        Assert.Equal("xxxxx", Rs2DlcStrings.CreateDlcKey("", "", "", () => 'x'));
        // Non-alphanumerics never survive; a part shorter than 5 is taken whole.
        Assert.Equal("AlACDCBackI",
            Rs2DlcStrings.CreateDlcKey("Alex", "AC/DC!", "Back In Black", () => 'x'));
    }
}
