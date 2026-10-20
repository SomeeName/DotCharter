using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The 0.49 Rocksmith song browser redesign was commissioned to remove one
///     specific inconsistency: the Play Mode library folded tuning AND capo into
///     one fixed-width cell and went amber when either meant work on the guitar,
///     while the editor's browser printed a tuning chip always and a capo chip
///     only when there was a capo. These pin the shared derivation that removes
///     it, and the two other pieces of the redesign that are real DATA rather
///     than layout: the artist grouping and the path pick.
/// </summary>
public sealed class SongBrowserRowFactsTests
{
    private static PsarcSongListItem Song(string artist, string tuning = "E A D G B E", int capo = 0,
        string title = "Title", params (string Name, int Route, int Notes)[] arrangements)
    {
        var entries = arrangements.Length == 0
            ? new[] { ("Lead", 1, 982) }
            : arrangements.Select(a => (a.Name, a.Route, a.Notes)).ToArray();

        return new PsarcSongListItem(new LibrarySongEntry
        {
            Artist = artist,
            Title = title,
            TuningSummary = tuning,
            CapoSummary = capo,
            Arrangements = [.. entries.Select(a => new LibraryArrangementEntry
            {
                ArrangementName = a.Item1, RouteMask = a.Item2, NoteCount = a.Item3, SngPath = "x.sng"
            })]
        }, FakeLocalization.English());
    }

    // ── One tuning cell, shared with the Play Mode library ─────────────────

    [Fact]
    public void TuningAndCapoShareOneCell()
    {
        Assert.Equal("E Standard", SongRowFacts.TuningCell("E A D G B E", 0, FakeLocalization.English()));
        Assert.Equal("E Standard · " + FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 2), SongRowFacts.TuningCell("E A D G B E", 2, FakeLocalization.English()));
    }

    /// <summary>A capo with no readable tuning still prints. The old browser
    /// dropped the capo chip entirely when there was no capo AND printed the
    /// tuning alone when there was one, so a capo could go unmentioned.</summary>
    [Fact]
    public void ACapoWithNoTuning_StillPrints()
        => Assert.Equal(FakeLocalization.English().Format("SongBrowser_Facet_CapoAt", 3), SongRowFacts.TuningCell("", 3, FakeLocalization.English()));

    /// <summary>Unknown is not "non-standard": a value we cannot judge never
    /// raises an alarm.</summary>
    [Theory]
    [InlineData("E A D G B E", 0, false)] // plain E standard, nothing to do
    [InlineData("E A D G B E", 2, true)] // a capo IS something to do
    [InlineData("D A D G B E", 0, true)] // drop D
    [InlineData("", 0, false)] // unknown
    public void AmberMeansSomethingHasToHappenToTheGuitar(string tuning, int capo, bool expected)
        => Assert.Equal(expected, SongRowFacts.NeedsAttention(tuning, capo));

    /// <summary>The browser row and the Play Mode library row must produce the
    /// SAME cell for the same song — that identity is the whole point of the
    /// shared helper, and it is what a second hand-written copy would break.</summary>
    [Fact]
    public void TheBrowserRow_PrintsWhatTheLibraryRowPrints()
    {
        var item = Song("3 Doors Down", "E A D G B E", 2);

        Assert.Equal(SongRowFacts.TuningCell(item.TuningSummary, item.Song.CapoSummary, FakeLocalization.English()), item.TuningCellLabel);
        Assert.Equal(SongRowFacts.NeedsAttention(item.TuningSummary, item.Song.CapoSummary),
            item.TuningNeedsAttention);
    }

    [Fact]
    public void ThePathsColumn_IsOneInitialPerArrangement()
    {
        var item = Song("311", arrangements:
            [("Lead", 1, 900), ("Rhythm", 2, 1100), ("Bass", 4, 600)]);

        Assert.Equal(["L", "R", "B"], item.ArrangementChips.Select(c => c.Initial));
        Assert.Equal(["Lead", "Rhythm", "Bass"], item.ArrangementChips.Select(c => c.Label));
    }

    // ── Artist grouping ────────────────────────────────────────────────────

    /// <summary>Each artist run's header is stamped on its FIRST row and cleared
    /// from every other one, so the list stays FLAT — one ListBox, one selection
    /// model, one virtualized panel over thousands of PSARCs.</summary>
    [Fact]
    public void ArtistHeaders_LandOnTheFirstRowOfEachRun()
    {
        var songs = new List<PsarcSongListItem>
        {
            Song("3 Doors Down", title: "a"), Song("3 Doors Down", title: "b"),
            Song("311", title: "c"), Song("311", title: "d"), Song("311", title: "e")
        };

        Stamp(songs, grouped: true);

        Assert.Equal(["3 Doors Down", "", "311", "", ""], songs.Select(s => s.GroupHeader));
        Assert.Equal([true, false, true, false, false], songs.Select(s => s.HasGroupHeader));
    }

    /// <summary>A title/year/length sort must not sprout artist headers — the
    /// runs would be meaningless.</summary>
    [Fact]
    public void AnUngroupedSort_CarriesNoHeaders()
    {
        var songs = new List<PsarcSongListItem> { Song("311"), Song("3 Doors Down") };
        Stamp(songs, grouped: true);
        Stamp(songs, grouped: false);

        Assert.All(songs, s => Assert.False(s.HasGroupHeader));
    }

    private static void Stamp(IReadOnlyList<PsarcSongListItem> songs, bool grouped)
        => SongArtistGrouping.Stamp(songs, grouped,
            static s => s.Artist,
            static (s, header, _) => s.GroupHeader = header,
            static (_, _) => "", FakeLocalization.English());

    // ── The detail pane's path pick ────────────────────────────────────────

    /// <summary>The pick is not decoration: it decides which arrangement the
    /// editor opens on after the load.</summary>
    [Fact]
    public void APickedPath_ResolvesToItsArrangement()
    {
        var arrangements = new List<Arrangement>
        {
            new() { Name = "Lead" }, new() { Name = "Rhythm" }, new() { Name = "Bass" }
        };

        Assert.Same(arrangements[1],
            SongBrowserViewModel.ResolvePreferredArrangement(arrangements, "Rhythm"));
        Assert.Same(arrangements[2],
            SongBrowserViewModel.ResolvePreferredArrangement(arrangements, "bass"));
    }

    /// <summary>An unresolvable pick returns NOTHING rather than an index guess —
    /// the caller then leaves the editor's own default first arrangement in
    /// place, which is the answer that cannot be wrong.</summary>
    [Theory]
    [InlineData("Vocals")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnresolvablePick_ChangesNothing(string? name)
    {
        var arrangements = new List<Arrangement> { new() { Name = "Lead" } };
        Assert.Null(SongBrowserViewModel.ResolvePreferredArrangement(arrangements, name));
    }

    /// <summary>An arrangement with no NotesHard in its manifest shows the path
    /// alone. A printed 0 would be a claim the data never made — an arrangement
    /// with genuinely zero notes does not ship.</summary>
    [Fact]
    public void AnArrangementWithNoNoteCount_PrintsNoNumber()
    {
        var counted = new PsarcArrangementChoice(FakeLocalization.English(), "Lead", "Lead", 982);
        var uncounted = new PsarcArrangementChoice(FakeLocalization.English(), "Vocals", "Vocals", 0);

        Assert.True(counted.HasNoteCount);
        Assert.Contains("982", counted.NoteCountLabel);

        Assert.False(uncounted.HasNoteCount);
        Assert.Equal("", uncounted.NoteCountLabel);
    }

    /// <summary>A counted string needs both forms — "1 notes" is the exact shape
    /// of bug the plural chooser exists for.</summary>
    [Fact]
    public void OneNote_ReadsAsOneNote()
    {
        Assert.Equal("1 note", new PsarcArrangementChoice(FakeLocalization.English(), "Lead", "Lead", 1).NoteCountLabel);
        Assert.Equal("2 notes", new PsarcArrangementChoice(FakeLocalization.English(), "Lead", "Lead", 2).NoteCountLabel);
    }
}
