using DotCharter.Services.Library;
using DotCharter.Services.Library.Prefetch;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #425 convert-on-select is DRIVEN, not just registered: every selection
///     change in the library reaches the prefetcher — the loadable song, or null
///     for a song that cannot open (so a pending prefetch is cancelled).
/// </summary>
public sealed class SongBrowserAudioPrefetchTests
{
    [Fact]
    public void Each_selection_change_tells_the_prefetcher_which_loadable_song_is_selected()
    {
        var prefetcher = new RecordingPrefetcher();
        var h = new SongBrowserScanRaceTests.Harness(prefetcher);
        var loadable = new PsarcSongListItem(Loadable("song"), FakeLocalization.English());
        var chartless = new PsarcSongListItem(SongBrowserScanRaceTests.Entry("no-charts"), FakeLocalization.English());

        h.Vm.SelectedSong = loadable;
        h.Vm.SelectedSong = chartless;
        h.Vm.SelectedSong = null;

        Assert.Equal(["song", null, null], prefetcher.Selections.Select(s => s?.SongKey));
    }

    private static LibrarySongEntry Loadable(string key) => new()
    {
        SongKey = key,
        Title = key,
        PackagePath = $@"C:\lib\{key}.psarc",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "songs/bin/generic/lead.sng" }]
    };

    private sealed class RecordingPrefetcher : ILibraryAudioPrefetcher
    {
        public List<LibrarySongEntry?> Selections { get; } = [];

        public void SelectionChanged(LibrarySongEntry? song) => Selections.Add(song);
    }
}
