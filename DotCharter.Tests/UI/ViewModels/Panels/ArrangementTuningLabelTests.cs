using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The song browser's path list and Play Mode's tiles state each arrangement's own tuning
///     (index schema v8): a Lead in E standard and a Bass in drop D with a capo each say theirs.
///     A package source fills these entries; that a <c>.psarc</c> scan does is the plugin's tests'.
/// </summary>
public sealed class ArrangementTuningLabelTests
{
    private static readonly LibraryArrangementEntry Lead = new()
    {
        ArrangementName = "Lead", PersistentId = "L", RouteMask = 1, Tuning = "E Standard", Capo = 0
    };

    private static readonly LibraryArrangementEntry Bass = new()
    {
        ArrangementName = "Bass", PersistentId = "B", RouteMask = 4, Tuning = "Drop D", Capo = 2
    };

    [Fact]
    public void The_browser_path_list_and_the_play_mode_tiles_state_each_arrangements_tuning()
    {
        var english = FakeLocalization.English();
        var bassChoice = new PsarcArrangementChoice(english, Bass.PathLabel, Bass.ArrangementName, Bass.NoteCount, Bass.Tuning, Bass.Capo);
        Assert.True(bassChoice.HasTuning);
        Assert.Equal("Drop D · " + english.Format("SongBrowser_Facet_CapoAt", 2), bassChoice.TuningLabel);
        var leadChoice = new PsarcArrangementChoice(english, Lead.PathLabel, Lead.ArrangementName, Lead.NoteCount, Lead.Tuning, Lead.Capo);
        Assert.Equal("E Standard", leadChoice.TuningLabel);

        // A pre-v8 row (no tuning) prints the path alone — never the song's
        // guitar tuning dressed up as this arrangement's.
        Assert.False(new PsarcArrangementChoice(english, "Bass", "Bass", 0).HasTuning);

        var facts = new SongArrangementFacts().For(PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            PackagePath = "x.psarc", SongKey = "Song", TuningSummary = "E Standard",
            Arrangements = [Lead, Bass]
        }, english)));
        Assert.Equal(["Lead", "Bass"], facts.Select(f => f.Label).ToArray());
        // The Lead plays in the song's own tuning, which the rail prints once under the
        // tiles; only the Bass, in Drop D with a capo, says what it needs.
        Assert.Equal("", facts[0].TuningLabel);
        Assert.False(facts[0].HasTuning);
        Assert.Equal("Drop D · " + english.Format("SongBrowser_Facet_CapoAt", 2), facts[1].TuningLabel);
        Assert.True(facts[1].HasTuning);
    }
}
