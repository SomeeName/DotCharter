using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Pure display-only projections behind the redesigned Song Select card
///     (Wave V4): the per-arrangement badge list (replacing the old copy-paste
///     "Lead, Rhythm, Bass, Vocals" text line) and the capo chip. These read no
///     other state, so the <c>LibraryQuery</c> purity invariant is untouched —
///     they are covered here so a card restyle can't silently drop an arrangement
///     or mis-render a capo. No Avalonia instance required.
/// </summary>
public sealed class PlaySongCardProjectionTests
{
    // ---- ParseArrangementBadges (the tested static seam) ----------------------

    [Fact]
    public void ParseArrangementBadges_EmptyOrWhitespace_YieldsNoBadges()
    {
        Assert.Empty(PlaySongItem.ParseArrangementBadges(null));
        Assert.Empty(PlaySongItem.ParseArrangementBadges(""));
        Assert.Empty(PlaySongItem.ParseArrangementBadges("   "));
        Assert.Empty(PlaySongItem.ParseArrangementBadges(", ,"));
    }

    [Fact]
    public void ParseArrangementBadges_SplitsAndTrimsEachArrangement()
    {
        Assert.Equal(
            new[] { "Lead", "Rhythm", "Bass", "Vocals" },
            PlaySongItem.ParseArrangementBadges("Lead, Rhythm, Bass, Vocals"));
    }

    [Fact]
    public void ParseArrangementBadges_TrimsOddSpacingAndIgnoresBlankSegments()
    {
        Assert.Equal(
            new[] { "Lead", "Bass" },
            PlaySongItem.ParseArrangementBadges("  Lead ,, Bass  ,"));
    }

    // ---- BuildArrangementChips (the LIST-ROW spelling: `L R B V`) --------------

    /// <summary>The mockup's row chips are single letters and the full word is
    /// the tooltip. Both halves matter: the letter is what keeps four
    /// arrangements inside ~90 px instead of ~200, and the label is the only
    /// thing that still says WHICH path a chip stands for.</summary>
    [Fact]
    public void BuildArrangementChips_PrintsTheInitialAndKeepsTheFullLabel()
    {
        var chips = PlaySongItem.BuildArrangementChips("Lead, Rhythm, Bass, Vocals");

        Assert.Equal(4, chips.Count);
        Assert.Equal(new[] { "L", "R", "B", "V" }, chips.Select(c => c.Initial));
        Assert.Equal(new[] { "Lead", "Rhythm", "Bass", "Vocals" }, chips.Select(c => c.Label));
    }

    /// <summary>It is a projection OF the badge list, so it inherits its
    /// trimming, its de-duplication and its order — it must not re-implement
    /// any of them and drift.</summary>
    [Fact]
    public void BuildArrangementChips_InheritsTheBadgeListsTrimmingAndDedupe()
    {
        var chips = PlaySongItem.BuildArrangementChips("  lead ,, Lead, Bass  ,");

        Assert.Equal(new[] { "L", "B" }, chips.Select(c => c.Initial));
        // The FIRST spelling wins, exactly as ParseArrangementBadges decides it.
        Assert.Equal("lead", chips[0].Label);
        // …and the chip still prints an upper-case letter for a lower-case label.
        Assert.Equal("L", chips[0].Initial);
    }

    [Fact]
    public void BuildArrangementChips_EmptyOrWhitespace_YieldsNoChips()
    {
        Assert.Empty(PlaySongItem.BuildArrangementChips(null));
        Assert.Empty(PlaySongItem.BuildArrangementChips(""));
        Assert.Empty(PlaySongItem.BuildArrangementChips(", ,"));
    }

    [Fact]
    public void ParseArrangementBadges_CollapsesCaseInsensitiveDuplicates_PreservingOrder()
    {
        Assert.Equal(
            new[] { "Lead", "Rhythm" },
            PlaySongItem.ParseArrangementBadges("Lead, lead, Rhythm, RHYTHM"));
    }

    // ---- the projection on a real item ----------------------------------------

    // ---- capo chip ------------------------------------------------------------

    [Fact]
    public void Capo_KnownNonZero_ShowsCapoChipWithLabel()
    {
        var item = Owned(capo: 2);
        Assert.True(item.HasCapo);
        Assert.Equal(FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 2), item.CapoLabel);
    }

    [Fact]
    public void Capo_ZeroOrUnknown_HidesTheChip()
    {
        Assert.False(Owned(capo: 0).HasCapo);
        Assert.Equal("", Owned(capo: 0).CapoLabel);

        // Own-chart rows carry no capo (unknown) — never a chip.
        Assert.False(PlaySongItem.FromProject(@"C:\c\x.dcp", false, FakeLocalization.English()).HasCapo);
    }

    private static PlaySongItem Owned(int capo)
    {
        var entry = new LibrarySongEntry
        {
            Title = "Song", Artist = "Artist", Album = "Album", SongLength = 200,
            CapoSummary = capo, TuningSummary = "E Standard", SongKey = "sk", Charter = "chr", Version = "1",
            Arrangements = [new LibraryArrangementEntry { RouteMask = 1, SngPath = "a.sng" }]
        };
        return PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));
    }

    // ---- tuning display (resolved name; raw summary preserved) ------------------

    [Fact]
    public void TuningDisplay_ResolvesKnownTuning_WhileRawSummaryStaysForTooltipAndFilter()
    {
        // Note-letter summaries are what the library index stores per song.
        var eStd = FromTuning("E A D G B E");
        Assert.Equal("E Standard", eStd.TuningDisplay);
        Assert.Equal("E A D G B E", eStd.TuningSummary); // raw kept: tooltip + LibraryQuery match

        var dropC = FromTuning("C G C F A D");
        Assert.Equal("Drop C", dropC.TuningDisplay);
        Assert.Equal("C G C F A D", dropC.TuningSummary);
    }

    [Fact]
    public void TuningDisplay_UnknownOrAbsentTuning_FallsBackToRawSummary()
    {
        var weird = FromTuning("C C C C C C"); // not a named tuning
        Assert.Equal("C C C C C C", weird.TuningDisplay);

        // Own charts carry no library tuning summary → empty passes through.
        Assert.Equal("", PlaySongItem.FromProject(@"C:\c\x.dcp", false, FakeLocalization.English()).TuningDisplay);
    }

    private static PlaySongItem FromTuning(string tuning)
    {
        var entry = new LibrarySongEntry
        {
            Title = "Song", SongKey = "sk", Charter = "chr", Version = "1",
            TuningSummary = tuning,
            Arrangements = [new LibraryArrangementEntry { RouteMask = 1, SngPath = "a.sng" }]
        };
        return PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));
    }
}
