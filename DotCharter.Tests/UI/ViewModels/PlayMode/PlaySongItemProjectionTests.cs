using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Every projection a Play Mode song ROW binds is computed once per song, in
///     the constructor — not once per binding read. The sibling guard for the
///     editor's browser row is
///     <c>Panels.SongBrowserFilterPassTests.EveryRowProjection_IsComputedOncePerSong_NotPerRead</c>;
///     this is the same claim for <see cref="PlaySongItem" />, whose two inputs
///     (an init-only library entry, a fixed project path) are equally
///     immutable. No Avalonia instance required.
/// </summary>
public sealed class PlaySongItemProjectionTests
{
    private static LibrarySongEntry Entry(
        string title = "Time", string artist = "Pink Floyd", string album = "Dark Side",
        string tuning = "D A D G B E", int capo = 2, double lengthSeconds = 413, int year = 1973,
        string songKey = "sk", string charter = "chr", string version = "1")
        => new()
        {
            Title = title, Artist = artist, Album = album, Year = year,
            SongLength = lengthSeconds, TuningSummary = tuning, CapoSummary = capo,
            SongKey = songKey, Charter = charter, Version = version,
            Arrangements =
            [
                new LibraryArrangementEntry { RouteMask = 1, SngPath = "lead.sng" },
                new LibraryArrangementEntry { RouteMask = 2, SngPath = "rhythm.sng" },
                new LibraryArrangementEntry { RouteMask = 4, SngPath = "bass.sng" }
            ]
        };

    private static PlaySongItem Owned(LibrarySongEntry? entry = null)
        => PlaySongItem.FromPsarc(new PsarcSongListItem(entry ?? Entry(), FakeLocalization.English()));

    // ── computed once, not per read ───────────────────────────────────────────

    /// <summary>
    ///     <c>ArrangementChips</c> is the load-bearing one: the row's chips
    ///     <c>ItemsControl</c> compares <c>ItemsSource</c> by REFERENCE, so a fresh
    ///     list per get made every recycled row discard and rebuild its chip
    ///     children on every re-bind — and the Song Library re-binds all of its
    ///     realized rows on every filter pass, i.e. per keystroke. The rest are
    ///     the same shape one step cheaper: interpolations and format calls that
    ///     ran on every read of an immutable fact.
    /// </summary>
    [Fact]
    public void EveryRowProjection_IsComputedOncePerSong_NotPerRead()
    {
        var item = Owned();

        Assert.Same(item.ArrangementChips, item.ArrangementChips);
        Assert.Same(item.ArrangementBadges, item.ArrangementBadges);
        Assert.Same(item.ArrangementSummary, item.ArrangementSummary);
        Assert.Same(item.Subtitle, item.Subtitle);
        Assert.Same(item.DetailSubtitle, item.DetailSubtitle);
        Assert.Same(item.TuningSummary, item.TuningSummary);
        Assert.Same(item.TuningDisplay, item.TuningDisplay);
        Assert.Same(item.TuningCellLabel, item.TuningCellLabel);
        Assert.Same(item.CapoLabel, item.CapoLabel);
        Assert.Same(item.SongLengthFormatted, item.SongLengthFormatted);
        Assert.Same(item.SongIdentity, item.SongIdentity);
        Assert.Same(item.Title, item.Title);
        Assert.Same(item.Artist, item.Artist);
        Assert.Same(item.Album, item.Album);
    }

    /// <summary>…and the own-chart row, whose title is a path projection and
    /// whose subtitle is the localized "own chart" wording.</summary>
    [Fact]
    public void EveryRowProjection_IsComputedOnce_ForAnOwnChartRowToo()
    {
        var item = PlaySongItem.FromProject(@"C:\charts\MyChart.dcp", false, FakeLocalization.English());

        Assert.Same(item.Title, item.Title);
        Assert.Same(item.Subtitle, item.Subtitle);
        Assert.Same(item.DetailSubtitle, item.DetailSubtitle);
        Assert.Same(item.SongIdentity, item.SongIdentity);
        Assert.Same(item.TuningCellLabel, item.TuningCellLabel);
        Assert.Same(item.ArrangementChips, item.ArrangementChips);
    }

    // ── …and computing them once did not change what they SAY ─────────────────

    [Fact]
    public void TheCachedProjections_StillPrintWhatTheLibraryEntrySays()
    {
        var entry = Entry();
        var item = Owned(entry);

        Assert.Equal("Time", item.Title);
        Assert.Equal("Pink Floyd", item.Artist);
        Assert.Equal("Dark Side", item.Album);
        Assert.Equal("Pink Floyd | Dark Side", item.Subtitle);
        Assert.Equal("Pink Floyd · 1973", item.DetailSubtitle);
        Assert.Equal(1973, item.Year);
        Assert.True(item.HasYear);

        Assert.Equal("D A D G B E", item.TuningSummary);
        Assert.Equal(SongRowFacts.TuningDisplay(entry.TuningSummary), item.TuningDisplay);
        Assert.Equal(SongRowFacts.TuningCell(entry.TuningSummary, entry.CapoSummary, FakeLocalization.English()), item.TuningCellLabel);
        Assert.True(item.IsNonStandardTuning);
        Assert.True(item.TuningNeedsAttention);

        Assert.Equal(2, item.Capo);
        Assert.True(item.HasCapo);
        Assert.Equal(FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 2), item.CapoLabel);

        Assert.Equal(entry.ArrangementSummary, item.ArrangementSummary);
        Assert.Equal(PlaySongItem.ParseArrangementBadges(entry.ArrangementSummary), item.ArrangementBadges);
        Assert.Equal(
            PlaySongItem.BuildArrangementChips(entry.ArrangementSummary).Select(c => c.Initial),
            item.ArrangementChips.Select(c => c.Initial));

        Assert.Equal("6:53", item.SongLengthFormatted);
        Assert.Equal(413d, item.LengthSeconds);

        Assert.Equal("lib:sk", item.SongIdentity);
        Assert.True(item.CanPlay);   // the entry's arrangements make it loadable
    }

    /// <summary>An own chart's row still prints its file name and keys on its
    /// path, and <c>CanPlay</c> is the existence answer its BUILDER made once —
    /// the row itself never touches the disk, so a path that is not on disk
    /// still reads as playable when the builder said so, and the other way round.</summary>
    [Fact]
    public void AnOwnChartRow_KeysOnItsPath_AndCanPlayIsTheBuildersExistenceAnswer()
    {
        var missing = PlaySongItem.FromProject(@"C:\charts\does-not-exist.dcp", false, FakeLocalization.English());
        Assert.True(missing.IsOwnChart);
        Assert.Equal("does-not-exist", missing.Title);
        Assert.Equal(@"C:\charts\does-not-exist.dcp", missing.SongIdentity);
        Assert.False(missing.CanPlay);

        Assert.True(PlaySongItem.FromProject(@"C:\charts\does-not-exist.dcp", true, FakeLocalization.English()).CanPlay);
    }

    /// <summary>The mutable half of the row is untouched by the caching — stats
    /// still land through <c>ApplyStats</c>, and the genre the scan stamps on
    /// still arrives after construction.</summary>
    [Fact]
    public void TheMutableHalfOfTheRow_StillChangesAfterConstruction()
    {
        var item = Owned();
        Assert.False(item.HasBest);

        item.ApplyStats(null);
        Assert.False(item.HasBest);
        Assert.Equal(-1, item.BestScore);

        item.GroupHeader = "P";
        Assert.True(item.HasGroupHeader);
    }

    /// <summary>
    ///     The record column prints a record only when a run measured something. A stored
    ///     run with no score and no accuracy printed "0 – –" over an empty meter on the
    ///     owner's own chart; it says "No full run yet", as Home does.
    /// </summary>
    [Fact]
    public void A_run_that_measured_nothing_says_so_instead_of_printing_a_zero_record()
    {
        var loc = FakeLocalization.English();
        var item = Owned();

        Assert.False(item.ShowsRecord);
        Assert.Equal(loc["PlayMode_SongSelect_Unplayed"], item.RecordEmptyLabel);

        item.ApplyStats(Stats(bestScore: 0, accuracy: 0));
        Assert.True(item.HasBest, "the filters still see a played song");
        Assert.False(item.ShowsRecord);
        Assert.Equal(loc["PlayMode_SongSelect_NoFullRun"], item.RecordEmptyLabel);

        item.ApplyStats(Stats(bestScore: 12_400, accuracy: 0.81));
        Assert.True(item.ShowsRecord);
    }

    /// <summary>An own chart has no length on record: its Time cell is a dash, so the
    /// difficulty under it no longer reads as the time ("5,6").</summary>
    [Fact]
    public void An_unknown_length_is_a_dash()
    {
        Assert.Equal("—", PlaySongItem.FromProject(@"C:\charts\MyChart.dcp", false, FakeLocalization.English()).SongLengthFormatted);
        Assert.Equal("6:53", Owned().SongLengthFormatted);
    }

    /// <summary>An own chart's folder is "Artist - Title", so it groups under its artist;
    /// it sat under "Unknown artist" with the artist folded into the title.</summary>
    [Fact]
    public void An_own_chart_groups_under_the_artist_its_folder_names()
    {
        var item = PlaySongItem.FromProject(@"C:\charts\Polkadot Stingray - Sakasma\project.rscp", false, FakeLocalization.English());
        Assert.Equal("Polkadot Stingray", item.Artist);
        Assert.Equal("Sakasma", item.Title);

        Assert.Equal(("", "Mr. Brightside"), PlaySongItem.SplitArtistTitle("Mr. Brightside"));
        Assert.Equal(("AC/DC", "T.N.T. - Live"), PlaySongItem.SplitArtistTitle("AC/DC - T.N.T. - Live"));
        Assert.Equal(("", "- Intro"), PlaySongItem.SplitArtistTitle("- Intro"));
    }

    private static DotCharter.Services.Gameplay.Stats.SongPlayStats Stats(long bestScore, double accuracy) =>
        new("sk", "Lead:Lead", 1, bestScore, accuracy, accuracy * 100,
            accuracy > 0 ? DotCharter.Services.Gameplay.Scoring.LetterGrade.B : default, DateTime.UtcNow, "Time");
}
