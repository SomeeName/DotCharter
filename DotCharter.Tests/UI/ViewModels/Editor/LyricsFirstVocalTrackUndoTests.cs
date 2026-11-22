using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #385: a lyrics import into a project with NO vocal track used to add the empty
///     track straight to <c>VocalTracks</c>, outside any history node. Undo took the
///     phrases back and left the track behind (saved, undoable never), and a refused
///     replacement left it behind with the project still clean. The track now joins the
///     same node as the phrase replacement.
/// </summary>
public sealed class LyricsFirstVocalTrackUndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-firstvocal-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task Lrc_import_adds_the_track_in_one_undoable_node()
    {
        var rig = Build("[00:01.00]hello world\n[00:03.00]second line\n");
        await rig.Inspector.ImportLrcCommand.ExecuteAsync(null);
        AssertOneNodeUndoRedo(rig);
    }

    [Fact]
    public async Task UltraStar_import_adds_the_track_in_one_undoable_node()
    {
        var rig = Build("#BPM:120\n: 0 4 0 Solo \n- 8\n: 8 4 0 line \nE\n");
        await rig.Inspector.ImportUltraStarCommand.ExecuteAsync(null);
        AssertOneNodeUndoRedo(rig);
    }

    [Fact]
    public async Task Auto_sync_adds_the_track_in_one_undoable_node()
    {
        var rig = Build("");
        rig.Inspector.RawLyrics = "hel-lo world";
        await rig.Inspector.AutoSyncCommand.ExecuteAsync(null);
        AssertOneNodeUndoRedo(rig);
    }

    [Fact]
    public async Task A_refused_import_leaves_no_empty_track_behind()
    {
        var rig = Build("#BPM:120\n: 0 4 0 Solo \nE\n");
        rig.History.RefuseNextDo = true;

        await rig.Inspector.ImportUltraStarCommand.ExecuteAsync(null);

        Assert.Empty(rig.Project.VocalTracks);
        Assert.Null(rig.Editor.ActiveVocalTrack);
        Assert.Equal(rig.CleanNode, rig.History.CurrentNodeId);
    }

    private static void AssertOneNodeUndoRedo(Rig rig)
    {
        var track = Assert.Single(rig.Project.VocalTracks);
        Assert.Same(track, rig.Editor.ActiveVocalTrack);
        Assert.NotEmpty(track.Phrases.SelectMany(p => p.Lyrics));
        Assert.NotEqual(rig.CleanNode, rig.History.CurrentNodeId);

        rig.History.Undo();

        Assert.Empty(rig.Project.VocalTracks);
        Assert.Null(rig.Editor.ActiveVocalTrack);
        Assert.Equal(rig.CleanNode, rig.History.CurrentNodeId);

        rig.History.Redo();

        Assert.Same(track, Assert.Single(rig.Project.VocalTracks));
        Assert.Same(track, rig.Editor.ActiveVocalTrack);
        Assert.NotEmpty(track.Phrases.SelectMany(p => p.Lyrics));
    }

    private sealed record Rig(
        LyricsInspectorViewModel Inspector,
        ChartEditorViewModel Editor,
        RefusableHistory History,
        ChartProject Project,
        int CleanNode);

    private Rig Build(string fileText)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "lyrics.txt");
        File.WriteAllText(file, fileText);
        var projectPath = Path.Combine(_root, "song.rscp");
        File.WriteAllText(projectPath, "<project/>");
        File.WriteAllBytes(Path.Combine(_root, "song.wav"), new byte[64]);

        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new RefusableHistory(logger);
        var selection = new SelectionService();

        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
            });
        }

        state.ActiveProject = project;
        state.CurrentProjectPath = projectPath;
        state.CurrentAudioFileName = "song.wav";

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart
        };

        var storage = new FakeFileStorageService { NextOpenFile = file };
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger, storage,
            new LyricsFileService(new ExportTargetWriter(), new LrcLyricsParser()),
            new LyricAutoSyncRunner(new FixedAutoSync(), null, null, state), FakeLocalization.English());

        return new Rig(inspector, editor, history, project, history.CurrentNodeId);
    }

    /// <summary>Real history, except a test can make <c>Do</c> refuse.</summary>
    private sealed class RefusableHistory(IAppLogger logger) : HistoryService(logger)
    {
        public bool RefuseNextDo { get; set; }

        public override bool Do(IChartAction action) => !RefuseNextDo && base.Do(action);
    }

    private sealed class FixedAutoSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => true;

        public Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var phrase = new LyricPhrase { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = "hello", Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1)
            });
            return Task.FromResult(new LyricAutoSyncOutcome(true, null, [phrase], 1, 0, 0, "mix"));
        }
    }
}
