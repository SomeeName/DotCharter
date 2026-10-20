using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     An UltraStar DUET imports as one vocal track per singer (#332). The importer
///     used to drop the <c>P1</c>/<c>P2</c> markers and keep both singers' notes, so
///     their overlapping lines interleaved in the active track. Now the first voice
///     replaces the active track's phrases and the second arrives as a NEW track —
///     in one undo node, so a single undo takes the whole import back.
/// </summary>
public sealed class LyricsInspectorUltraStarImportTests : IDisposable
{
    private const string DuetFile = """
        #TITLE:Duet
        #ARTIST:Two Singers
        #BPM:120
        #GAP:500
        #P1:Anna
        #P2:Ben
        P1
        : 0 4 0 Hel
        : 4 4 2 lo
        - 10
        : 16 4 4 there
        P2
        : 2 4 5 Hi
        - 8
        : 18 4 7 you
        E
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-ultrastar-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task A_duet_imports_the_second_singer_into_a_new_track()
    {
        var rig = Build(DuetFile);

        await rig.Inspector.ImportUltraStarCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Editor.VocalTracks.Count);
        Assert.Same(rig.Track, rig.Editor.VocalTracks[0]);
        Assert.Same(rig.Track, rig.Editor.ActiveVocalTrack);
        Assert.Equal(["Hel", "lo", "there"], rig.Track.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));

        var ben = rig.Editor.VocalTracks[1];
        Assert.Equal("Ben", ben.Name);
        Assert.Equal(["Hi", "you"], ben.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
    }

    [Fact]
    public async Task One_undo_takes_the_whole_duet_import_back()
    {
        var rig = Build(DuetFile);

        await rig.Inspector.ImportUltraStarCommand.ExecuteAsync(null);
        rig.History.Undo();

        Assert.Same(rig.Track, Assert.Single(rig.Editor.VocalTracks));
        Assert.Equal(["hold"], rig.Track.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
    }

    [Fact]
    public async Task A_solo_song_still_replaces_the_active_track_only()
    {
        var rig = Build("#BPM:120\n: 0 4 0 Solo \n- 8\n: 8 4 0 line \nE\n");

        await rig.Inspector.ImportUltraStarCommand.ExecuteAsync(null);

        Assert.Same(rig.Track, Assert.Single(rig.Editor.VocalTracks));
        Assert.Equal(["Solo", "line"], rig.Track.Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
    }

    private sealed record Rig(
        LyricsInspectorViewModel Inspector,
        ChartEditorViewModel Editor,
        HistoryService History,
        VocalTrack Track);

    private Rig Build(string ultraStarText)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "song.txt");
        File.WriteAllText(file, ultraStarText);

        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        var selection = new SelectionService();

        var project = new ChartProject();
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
            });
        }

        // The active track already holds a line, so the replacement is visible.
        var track = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "hold" });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        state.ActiveProject = project;

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveVocalTrack = track
        };

        var storage = new FakeFileStorageService { NextOpenFile = file };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, storage,
            new LrcLyricsParser(), new NoopAutoSync(), FakeLocalization.English());

        return new Rig(inspector, editor, history, track);
    }

    private sealed class NoopAutoSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => false;

        public Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("these tests never reach auto-sync");
    }
}
