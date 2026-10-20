using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Lrc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Lyric auto-sync is a multi-second decode + CTC alignment on a worker, and
///     it used to hand that worker the LIVE
///     <c>ObservableCollection&lt;BeatMarker&gt;</c> off
///     <c>ProjectState.ActiveProject</c> and then apply whatever came back to
///     whatever project was active when it returned.
///     <para>
///         Two separate faults in one line. The aligner walks a collection the UI
///         thread may be editing — or REPLACING outright, which is what opening a
///         project does. And a result computed against one song's beat grid was
///         written into whichever song happened to be open at the end, timing the
///         new chart's lyrics to the old chart's beats. The stem pipeline already
///         guards its own long await this way
///         (<c>StemSeparationViewModel</c>'s <c>ReferenceEquals</c> check);
///         this path had nothing.
///     </para>
/// </summary>
public sealed class LyricAutoSyncProjectIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-lyricsync-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task The_worker_is_handed_a_snapshot_not_the_live_collection()
    {
        var rig = Build();

        await rig.Inspector.AutoSyncCommand.ExecuteAsync(null);

        Assert.NotNull(rig.AutoSync.SeenBeats);
        Assert.False(ReferenceEquals(rig.AutoSync.SeenBeats, rig.Project.Beats),
            "the aligner was handed the project's live ObservableCollection — the UI thread can " +
            "mutate or replace it mid-alignment");
        Assert.IsNotType<ObservableCollection<BeatMarker>>(rig.AutoSync.SeenBeats);
        // The snapshot must still be the real grid, or this guard would pass on
        // a change that simply stopped passing the beats.
        Assert.Equal(rig.Project.Beats.Count, rig.AutoSync.SeenBeats!.Count);
    }

    [Fact]
    public async Task A_result_for_a_project_that_is_no_longer_open_is_dropped()
    {
        var rig = Build();
        var other = new ChartProject();

        // The aligner "takes minutes": the user opens another song while it runs.
        rig.AutoSync.WhileRunning = () => rig.State.ActiveProject = other;

        await rig.Inspector.AutoSyncCommand.ExecuteAsync(null);

        Assert.Same(other, rig.State.ActiveProject);
        // Neither project may have been given the phrases: they were timed
        // against a beat grid that belongs to a chart nobody is looking at.
        Assert.Empty(rig.Track.Phrases.SelectMany(p => p.Lyrics));
        Assert.Empty(other.VocalTracks);
    }

    [Fact]
    public async Task A_result_for_the_project_that_is_still_open_is_applied()
    {
        var rig = Build();

        await rig.Inspector.AutoSyncCommand.ExecuteAsync(null);

        Assert.Same(rig.Project, rig.State.ActiveProject);
        Assert.NotEmpty(rig.Track.Phrases.SelectMany(p => p.Lyrics));
    }

    // ---- rig ---------------------------------------------------------------

    private sealed record Rig(
        LyricsInspectorViewModel Inspector,
        ProjectState State,
        ChartProject Project,
        VocalTrack Track,
        CapturingAutoSync AutoSync);

    private Rig Build()
    {
        Directory.CreateDirectory(_root);
        // ProjectAudioLocator must resolve real audio beside the project, or the
        // command bails before it ever reaches the aligner.
        var projectPath = Path.Combine(_root, "song.rscp");
        File.WriteAllText(projectPath, "<project/>");
        File.WriteAllBytes(Path.Combine(_root, "song.wav"), new byte[64]);

        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        var selection = new SelectionService();

        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0
            });
        }

        var track = new VocalTrack { Name = "Vocals" };
        project.VocalTracks.Add(track);

        state.ActiveProject = project;
        state.CurrentProjectPath = projectPath;
        state.CurrentAudioFileName = "song.wav";

        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state,
            new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
        {
            CurrentChart = project.Chart,
            ActiveVocalTrack = track
        };

        var autoSync = new CapturingAutoSync();
        var inspector = new LyricsInspectorViewModel(editor, history, state, logger,
            new FakeFileStorageService(), new LrcLyricsParser(), autoSync, FakeLocalization.English())
        {
            RawLyrics = "hel-lo world"
        };

        return new Rig(inspector, state, project, track, autoSync);
    }

    /// <summary>An aligner that records what it was handed, lets the test move
    /// the world underneath it, and always returns a usable result — so anything
    /// the test observes afterwards is the CALLER's decision, not the aligner's.</summary>
    private sealed class CapturingAutoSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "";
        public bool IsModelInstalled => true;
        public IList<BeatMarker>? SeenBeats { get; private set; }
        public Action? WhileRunning { get; set; }

        public async Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            SeenBeats = beats;
            // A real await, so the continuation genuinely runs after whatever the
            // test does next — a synchronous return would never reach the guard.
            await Task.Yield();
            WhileRunning?.Invoke();

            var phrase = new LyricPhrase
            {
                Position = FractionalPosition.Zero,
                EndPosition = new FractionalPosition(2)
            };
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = "hello",
                Position = FractionalPosition.Zero,
                EndPosition = new FractionalPosition(1)
            });
            return new LyricAutoSyncOutcome(true, null, [phrase], 1, 0, 0, "mix");
        }
    }
}
