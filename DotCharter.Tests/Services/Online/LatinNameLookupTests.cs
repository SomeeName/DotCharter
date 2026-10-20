using DotCharter.Services.Online.MusicBrainz;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     The official Latin-script name lookup (owner request 2026-08-21): when a
///     song's tags are in another script, offer the romanized artist, title and
///     album instead of making the charter look them up by hand.
///     <para>
///     <b>The suggestion is never applied automatically, and the three fields
///     are not equally trustworthy.</b> MusicBrainz's style guide requires an
///     artist's <c>sort-name</c> to be Latin and its English aliases are well
///     curated, so the ARTIST nearly always has a defensible answer; an official
///     English SONG TITLE or ALBUM exists only when the release carries one.
///     That asymmetry is the reason for a suggestion rather than an overwrite.
///     </para>
///     <para>
///     The album is also the only one that is LOSSY to apply: a song keeps both
///     its Latin and its original title and artist, but the Rocksmith manifest
///     has no Japanese album field, so taking that half replaces it.
///     </para>
/// </summary>
public sealed class LatinNameLookupTests
{
    // ---- ScriptDetection ----------------------------------------------------

    [Theory]
    [InlineData("Yorushika")]
    [InlineData("August, A Certain, Moonlight")]
    [InlineData("Sigur Rós")]          // accented Latin is still Latin
    [InlineData("Motörhead")]
    [InlineData("1985")]               // no letters at all
    [InlineData("!!!")]
    [InlineData("")]
    [InlineData(null)]
    public void LatinMetadata_NeedsNoRomanization(string? value)
        => Assert.False(ScriptDetection.NeedsRomanization(value));

    [Theory]
    [InlineData("ヨルシカ")]              // katakana
    [InlineData("八月、某、月明かり")]      // kanji + kana + full-width punctuation
    [InlineData("8月")]                  // one digit, one kanji: the digit carries no script
    [InlineData("소녀시대")]              // Hangul
    [InlineData("Мельница")]             // Cyrillic
    public void NonLatinMetadata_NeedsRomanization(string value)
        => Assert.True(ScriptDetection.NeedsRomanization(value));

    [Fact]
    public void AStrayNonLatinWordDoesNotFlipAMostlyLatinTitle() =>
        // "Neon Genesis エヴァンゲリオン" is majority Latin; romanizing it would
        // be a downgrade, so it must not be offered.
        Assert.False(ScriptDetection.NeedsRomanization("Neon Genesis Evangelion Soundtrack エヴァ"));

    [Theory]
    [InlineData("ヨルシカ", "Yorushika", true)]
    [InlineData("ヨルシカ", "", false)]                 // nothing to offer
    [InlineData("ヨルシカ", "ヨルシカ", false)]          // not actually a romanization
    [InlineData("Yorushika", "yorushika", false)]      // same name, different case
    public void OnlyARealImprovement_IsOffered(string current, string candidate, bool useful)
        => Assert.Equal(useful, ScriptDetection.IsUsefulRomanization(current, candidate));

    // ---- the MusicBrainz artist mapping -------------------------------------

    private const string ArtistResponse = """
        {"artists":[
          {"name":"ヨルシカ","sort-name":"Yorushika","score":100,
           "aliases":[
             {"name":"よるしか","locale":"ja","primary":true},
             {"name":"Yorushika","locale":"en","primary":true}
           ]},
          {"name":"Some Other Band","sort-name":"Other Band, Some","score":60}
        ]}
        """;

    [Fact]
    public void ThePrimaryEnglishAlias_WinsOverEverythingElse()
        => Assert.Equal("Yorushika", MusicBrainzJson.ParseLatinArtistName(ArtistResponse, "ヨルシカ"));

    [Fact]
    public void SortName_IsTheFloorWhenThereIsNoEnglishAlias()
    {
        // MusicBrainz's style guide requires sort-name to be Latin, which is
        // what makes the artist half reliable at all.
        const string json = """
            {"artists":[{"name":"ヨルシカ","sort-name":"Yorushika","score":100,
              "aliases":[{"name":"よるしか","locale":"ja"}]}]}
            """;
        Assert.Equal("Yorushika", MusicBrainzJson.ParseLatinArtistName(json, "ヨルシカ"));
    }

    [Fact]
    public void AnArtistThatDoesNotMatchTheQuery_CannotRenameTheSong()
    {
        // MusicBrainz's score is a text-match score and hands out 100 freely, so
        // a row that resembles nothing we asked for must be ignored outright.
        const string json = """
            {"artists":[{"name":"Completely Different","sort-name":"Different, Completely","score":100}]}
            """;
        Assert.Null(MusicBrainzJson.ParseLatinArtistName(json, "ヨルシカ"));
    }

    [Fact]
    public void AnAlreadyLatinArtist_ProducesNoSuggestion()
    {
        const string json = """
            {"artists":[{"name":"Radiohead","sort-name":"Radiohead","score":100}]}
            """;
        Assert.Null(MusicBrainzJson.ParseLatinArtistName(json, "Radiohead"));
    }

    [Fact]
    public void AnEmptyOrShapelessResponse_IsNotAnError()
    {
        Assert.Null(MusicBrainzJson.ParseLatinArtistName("""{"artists":[]}""", "ヨルシカ"));
        Assert.Null(MusicBrainzJson.ParseLatinArtistName("""{"count":0}""", "ヨルシカ"));
    }

    [Fact]
    public void AnEmptySuggestion_IsNotWorthShowing()
        => Assert.False(new LatinNameSuggestion("", "", "").HasAnything);

    [Theory]
    [InlineData("Yorushika", "", "")]
    [InlineData("", "Moonlight", "")]
    [InlineData("", "", "That's Why I Gave Up on Music")]   // the album alone is enough
    public void AnySingleField_IsWorthShowing(string artist, string title, string album)
        => Assert.True(new LatinNameSuggestion(artist, title, album).HasAnything);

    /// <summary>
    ///     The album rides on the SAME recording search the title needs — the
    ///     release MusicBrainz matched IS the album — so it costs no extra
    ///     request and no extra rate-gate slot. This pins the mapping the client
    ///     reads (<c>MusicBrainzMatch.ReleaseTitle</c>) rather than the transport.
    /// </summary>
    [Fact]
    public void TheRecordingSearch_CarriesTheAlbumAlongsideTheTitle()
    {
        const string json = """
            {"recordings":[{
              "id":"r1","title":"August, A Certain, Moonlight","score":100,
              "artist-credit":[{"name":"ヨルシカ"}],
              "releases":[{"id":"rel1","title":"That's Why I Gave Up on Music"}]
            }]}
            """;

        var match = MusicBrainzJson.ParseBestRecording(json, "ヨルシカ", "八月、某、月明かり");

        Assert.NotNull(match);
        Assert.Equal("That's Why I Gave Up on Music", match!.ReleaseTitle);
        Assert.Equal("August, A Certain, Moonlight", match.Title);
    }

    [Fact]
    public void AnAlreadyLatinAlbum_IsNotOffered()
    {
        // Same rule as the other two: only a real improvement is shown.
        Assert.False(ScriptDetection.IsUsefulRomanization(
            "That's Why I Gave Up on Music", "That's Why I Gave Up on Music"));
    }

    [Fact]
    public void ANonLatinAlbum_IsOfferedItsRomanization()
        => Assert.True(ScriptDetection.IsUsefulRomanization("だから僕は音楽を辞めた", "That's Why I Gave Up on Music"));
}
