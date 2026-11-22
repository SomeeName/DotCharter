using DotCharter.Core.Domain.Vocals;
using System.Collections.ObjectModel;
using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Editing.Lyrics;

namespace DotCharter.Tests.Services.Editing.Lyrics;

public sealed class LyricAutoSyncRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-autosync-" + Guid.NewGuid().ToString("N"));

    public LyricAutoSyncRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "song.wav"), [0]);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static IProgress<string> NoProgress => new Progress<string>();

    private ProjectState State(bool withAudio = true)
        => new()
        {
            ActiveProject = new ChartProject(),
            CurrentProjectPath = Path.Combine(_dir, "chart.dcproj"),
            CurrentAudioFileName = withAudio ? "song.wav" : ""
        };

    private static ObservableCollection<BeatMarker> Beats()
        => [new BeatMarker { Time = 0, Bpm = 120, BeatsInMeasure = 4 }];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tempo_edits_during_sync_preserve_aligned_absolute_times(bool duringDownload)
    {
        var live = Beats();
        var phrase = new LyricPhrase
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3),
            Lyrics = { new LyricEvent { Text = "hi", Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3) } }
        };
        var aligned = new LyricAutoSyncOutcome(true, null, [phrase], 1, 0, 0, "mix");
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSync { Outcome = aligned, WhileRunning = () => live[0].Bpm = 60 };
        var downloads = duringDownload ? new FakeDownloads { Megabytes = 400, PendingDownload = pending } : null;

        var run = new LyricAutoSyncRunner(sync, downloads, null, State()).RunAsync("hi", live, _ => { }, NoProgress);
        if (duringDownload)
        {
            live[0].Bpm = 60;
            pending.SetResult(true);
        }
        var result = await run;

        Assert.Equal(LyricAutoSyncRunStatus.Synced, result.Status);
        var applied = Assert.Single(result.Outcome!.Phrases);
        var lyric = Assert.Single(applied.Lyrics);
        Assert.Equal(1000, applied.Position.GetPositionMs(live));
        Assert.Equal(1500, applied.EndPosition.GetPositionMs(live));
        Assert.Equal(1000, lyric.Position.GetPositionMs(live));
        Assert.Equal(1500, lyric.EndPosition.GetPositionMs(live));
        Assert.Equal(new FractionalPosition(2), phrase.Position);
        Assert.Equal(new FractionalPosition(2), phrase.Lyrics[0].Position);
    }

    [Theory]
    [InlineData(false, 500.0)]
    [InlineData(true, 1937.5)]
    public async Task Tempo_edits_respect_the_captured_word_span_setting(bool spanWords, double expectedMs)
    {
        var live = Beats();
        var config = new FakeConfigurationService();
        config.AppConfig.LyricSpanWordGap = spanWords;
        var phrase = new LyricPhrase
        {
            EndPosition = new FractionalPosition(5),
            Lyrics =
            [
                new LyricEvent { Text = "a", EndPosition = new FractionalPosition(1) },
                new LyricEvent { Text = "b", Position = new FractionalPosition(4), EndPosition = new FractionalPosition(5), IsPhraseEnd = true }
            ]
        };
        var sync = new FakeSync
        {
            Outcome = new LyricAutoSyncOutcome(true, null, [phrase], 2, 0, 0, "mix"),
            WhileRunning = () =>
            {
                live[0].Bpm = 60;
                config.AppConfig.LyricSpanWordGap = !spanWords;
            }
        };

        var result = await new LyricAutoSyncRunner(sync, null, config, State()).RunAsync("a b", live, _ => { }, NoProgress);

        var lyrics = Assert.Single(result.Outcome!.Phrases).Lyrics;
        Assert.Equal(2000, lyrics[1].Position.GetPositionMs(live));
        Assert.Equal(expectedMs, lyrics[0].EndPosition.GetPositionMs(live));
    }

    [Fact]
    public async Task A_run_aligns_a_snapshot_not_the_live_grid()
    {
        var sync = new FakeSync();
        var live = Beats();

        var result = await new LyricAutoSyncRunner(sync, null, null, State()).RunAsync("hi", live, _ => { }, NoProgress);

        Assert.Equal(LyricAutoSyncRunStatus.Synced, result.Status);
        Assert.NotSame(live, sync.SeenBeats);
        Assert.Equal(live.Count, sync.SeenBeats!.Count);
    }

    [Fact]
    public async Task A_project_switch_during_download_discards_the_run_before_alignment()
    {
        var state = State();
        var sync = new FakeSync();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = new FakeDownloads { Megabytes = 400, PendingDownload = pending };
        var run = new LyricAutoSyncRunner(sync, downloads, null, state)
            .RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.False(run.IsCompleted);
        state.ActiveProject = new ChartProject();
        pending.SetResult(true);
        var result = await run;

        Assert.Equal(LyricAutoSyncRunStatus.ProjectChanged, result.Status);
        Assert.Null(sync.SeenBeats);
    }

    [Fact]
    public async Task Download_and_inference_cannot_change_the_captured_grid_or_audio_input()
    {
        var state = State();
        var live = Beats();
        var stem = new Stem { Name = "vocals", Path = "original.wav" };
        state.ActiveProject!.Stems.Add(stem);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = new FakeDownloads { Megabytes = 400, PendingDownload = pending };
        var sync = new FakeSync { WhileRunning = () => live[0].Time = 2000 };
        var run = new LyricAutoSyncRunner(sync, downloads, null, state)
            .RunAsync("original lyrics", live, _ => { }, NoProgress);

        live[0].Time = 1000;
        live[0].Bpm = 60;
        stem.Path = "changed.wav";
        state.CurrentAudioFileName = "missing.wav";
        pending.SetResult(true);
        var result = await run;

        Assert.Equal(LyricAutoSyncRunStatus.Synced, result.Status);
        var captured = Assert.Single(sync.SeenBeats!);
        Assert.NotSame(live[0], captured);
        Assert.Equal(0, captured.Time);
        Assert.Equal(120, captured.Bpm);
        Assert.Equal("original lyrics", sync.SeenLyrics);
        Assert.Equal(Path.Combine(_dir, "song.wav"), sync.SeenAudioPath);
        Assert.Equal("original.wav", Assert.Single(sync.SeenOptions!.ProjectStems!).Path);
    }

    [Fact]
    public async Task Inference_uses_independent_beat_markers()
    {
        var live = Beats();
        var sync = new FakeSync { WhileRunning = () => live[0].Time = 1000 };

        await new LyricAutoSyncRunner(sync, null, null, State()).RunAsync("hi", live, _ => { }, NoProgress);

        Assert.Equal(0, Assert.Single(sync.SeenBeats!).Time);
        Assert.NotSame(live[0], sync.SeenBeats![0]);
    }

    [Fact]
    public async Task A_run_snapshots_the_project_stem_references_and_their_resolution_bases()
    {
        var state = State();
        var stem = new Stem { Name = "vocals", Path = "stems/silence-2/vocals.wav" };
        state.ActiveProject!.Stems.Add(stem);
        var sync = new FakeSync { WhileRunning = () => stem.Path = "stems/vocals.wav" };

        await new LyricAutoSyncRunner(sync, null, null, state).RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.NotNull(sync.SeenOptions);
        Assert.Equal(state.CurrentProjectPath, typeof(LyricAutoSyncOptions).GetProperty("ProjectPath")?.GetValue(sync.SeenOptions));
        Assert.Equal(state.CurrentAudioFileName, typeof(LyricAutoSyncOptions).GetProperty("ProjectAudioFileName")?.GetValue(sync.SeenOptions));
        var stems = Assert.IsAssignableFrom<IReadOnlyList<Stem>>(
            typeof(LyricAutoSyncOptions).GetProperty("ProjectStems")?.GetValue(sync.SeenOptions));
        var captured = Assert.Single(stems);
        Assert.NotSame(stem, captured);
        Assert.Equal("vocals", captured.Name);
        Assert.Equal("stems/silence-2/vocals.wav", captured.Path);
    }

    [Fact]
    public async Task A_download_wait_keeps_all_inference_inputs_from_the_start_of_the_run()
    {
        var state = State();
        var live = Beats();
        live[0].IsAnchor = true;
        live[0].IsMeasure = true;
        live[0].NoteDenominator = 8;
        var stem = new Stem { Name = "vocals", Path = "original.wav", Offset = 12 };
        state.ActiveProject!.Stems.Add(stem);
        var config = new FakeConfigurationService();
        config.AppConfig.LyricSpanWordGap = false;
        config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds;
        config.AppConfig.MinNoteDistanceMs = 23;
        config.AppConfig.MinNoteDistanceDivisor = 32;
        var downloads = new FakeDownloads { Megabytes = 400, Succeeds = true, Gate = new TaskCompletionSource<bool>() };
        var sync = new FakeSync();
        var run = new LyricAutoSyncRunner(sync, downloads, config, state)
            .RunAsync("original lyrics", live, _ => { }, NoProgress);

        live[0].Time = 123;
        live[0].Bpm = 42;
        live[0].BeatsInMeasure = 3;
        live[0].IsAnchor = false;
        live[0].IsMeasure = false;
        live[0].NoteDenominator = 16;
        live.Add(new BeatMarker());
        stem.Name = "changed";
        stem.Path = "changed.wav";
        stem.Offset = 99;
        state.CurrentProjectPath = Path.Combine(_dir, "changed.dcproj");
        state.CurrentAudioFileName = "changed.wav";
        File.WriteAllBytes(Path.Combine(_dir, "changed.wav"), [0]);
        config.AppConfig.LyricSpanWordGap = true;
        config.AppConfig.MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction;
        config.AppConfig.MinNoteDistanceMs = 99;
        config.AppConfig.MinNoteDistanceDivisor = 16;
        downloads.Gate.SetResult(true);
        var result = await run;

        Assert.Equal(LyricAutoSyncRunStatus.Synced, result.Status);
        Assert.Equal("original lyrics", sync.SeenLyrics);
        Assert.Equal(Path.Combine(_dir, "song.wav"), sync.SeenAudioPath);
        var beat = Assert.Single(sync.SeenBeats!);
        Assert.NotSame(live[0], beat);
        Assert.Equal(0, beat.Time);
        Assert.Equal(120, beat.Bpm);
        Assert.Equal(4, beat.BeatsInMeasure);
        Assert.True(beat.IsAnchor);
        Assert.True(beat.IsMeasure);
        Assert.Equal(8, beat.NoteDenominator);
        Assert.False(sync.SeenOptions!.SpanWordEndToNextWord);
        Assert.NotSame(config.AppConfig, sync.SeenOptions.SpacingConfig);
        Assert.Equal(MinNoteDistanceMode.Milliseconds, sync.SeenOptions.SpacingConfig!.MinNoteDistanceMode);
        Assert.Equal(23, sync.SeenOptions.SpacingConfig.MinNoteDistanceMs);
        Assert.Equal(32, sync.SeenOptions.SpacingConfig.MinNoteDistanceDivisor);
        Assert.Equal(Path.Combine(_dir, "chart.dcproj"), sync.SeenOptions.ProjectPath);
        Assert.Equal("song.wav", sync.SeenOptions.ProjectAudioFileName);
        var captured = Assert.Single(sync.SeenOptions.ProjectStems!);
        Assert.Equal("vocals", captured.Name);
        Assert.Equal("original.wav", captured.Path);
        Assert.Equal(12, captured.Offset);
    }

    [Fact]
    public async Task A_missing_model_reports_its_directory_without_aligning()
    {
        var sync = new FakeSync { IsModelInstalled = false };

        var result = await new LyricAutoSyncRunner(sync, null, null, State()).RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.Equal(LyricAutoSyncRunStatus.NoModel, result.Status);
        Assert.Equal("models-dir", result.ModelDirectory);
        Assert.Null(sync.SeenBeats);
    }

    [Fact]
    public async Task A_project_without_audio_reports_no_audio()
    {
        var sync = new FakeSync();

        var result = await new LyricAutoSyncRunner(sync, null, null, State(withAudio: false))
            .RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.Equal(LyricAutoSyncRunStatus.NoAudio, result.Status);
        Assert.Null(sync.SeenBeats);
    }

    [Fact]
    public async Task A_result_for_a_project_that_is_no_longer_active_is_discarded()
    {
        var state = State();
        var sync = new FakeSync { WhileRunning = () => state.ActiveProject = new ChartProject() };

        var result = await new LyricAutoSyncRunner(sync, null, null, state).RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.Equal(LyricAutoSyncRunStatus.ProjectChanged, result.Status);
    }

    [Fact]
    public async Task An_empty_outcome_is_a_failure_carrying_the_aligner_error()
    {
        var sync = new FakeSync { Outcome = LyricAutoSyncOutcome.Failed("boom") };

        var result = await new LyricAutoSyncRunner(sync, null, null, State()).RunAsync("hi", Beats(), _ => { }, NoProgress);

        Assert.Equal(LyricAutoSyncRunStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task A_pending_download_is_announced_and_a_failed_fetch_stops_the_run()
    {
        var sync = new FakeSync();
        var downloads = new FakeDownloads { Megabytes = 400, Succeeds = false };
        var announced = new List<int>();

        var result = await new LyricAutoSyncRunner(sync, downloads, null, State())
            .RunAsync("hi", Beats(), announced.Add, NoProgress);

        Assert.Equal([400], announced);
        Assert.Equal(LyricAutoSyncRunStatus.NoModel, result.Status);
        Assert.Null(sync.SeenBeats);
    }

    private sealed class FakeDownloads : ILyricModelDownloadService
    {
        public int Megabytes { get; init; }
        public bool Succeeds { get; init; }
        public TaskCompletionSource<bool>? Gate { get; init; }
        public TaskCompletionSource<bool>? PendingDownload { get; init; }
        public bool IsRequiredModelInstalled => Succeeds;
        public int PendingMegabytesFor(LyricSheet sheet) => Megabytes;

        public Task<bool> EnsureModelsForAsync(LyricSheet sheet, IProgress<string>? progress = null,
            CancellationToken ct = default)
            => Gate?.Task ?? PendingDownload?.Task ?? Task.FromResult(Succeeds);

        public IReadOnlyList<LyricModelStatus> Models => [];

        public Task<bool> InstallAsync(string id, bool repair, IProgress<string>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public bool RetryGpu(string id) => false;
    }

    private sealed class FakeSync : ILyricAutoSyncService
    {
        public string ModelDirectory => "models-dir";
        public bool IsModelInstalled { get; init; } = true;
        public IList<BeatMarker>? SeenBeats { get; private set; }
        public LyricAutoSyncOptions? SeenOptions { get; private set; }
        public string? SeenLyrics { get; private set; }
        public string? SeenAudioPath { get; private set; }
        public Action? WhileRunning { get; init; }
        public LyricAutoSyncOutcome? Outcome { get; init; }

        public async Task<LyricAutoSyncOutcome> SyncAsync(string lyricsText, string audioPath,
            IList<BeatMarker> beats, LyricAutoSyncOptions? options = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            SeenBeats = beats;
            SeenLyrics = lyricsText;
            SeenAudioPath = audioPath;
            SeenOptions = options;
            await Task.Yield();
            WhileRunning?.Invoke();
            if (Outcome != null) return Outcome;
            var phrase = new LyricPhrase { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2) };
            phrase.Lyrics.Add(new LyricEvent
            {
                Text = "hi", Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1)
            });
            return new LyricAutoSyncOutcome(true, null, [phrase], 1, 0, 0, "mix");
        }
    }
}
