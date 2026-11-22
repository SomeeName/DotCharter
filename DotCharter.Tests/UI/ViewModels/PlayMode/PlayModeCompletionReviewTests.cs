using System.Reflection;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Completion follows authoritative position events and the classic detector's actual capture delay.</summary>
public sealed class PlayModeCompletionReviewTests
{
    [Fact]
    public void AThrottledTerminalPosition_StillCompletesTheRun()
    {
        using var h = new PlayModeReviewHarness();
        h.Begin();
        h.Audio.PublishPosition(990);
        h.Ui.Flush();
        Assert.Equal(990, h.State.CurrentTimeMs);
        PlayModeReviewHarness.Set(h.Coordinator, "_lastUiUpdateTicks", long.MaxValue);

        h.Audio.IsPlaying = false;
        h.Audio.PublishPosition(1000);
        h.Ui.Flush();
        Assert.Equal(990, h.State.CurrentTimeMs);
        Assert.Equal(1000, h.Audio.CurrentTimeMs);
        h.EndWatch();

        Assert.False(h.Live.IsActive);
    }

    [Fact]
    public void ClassicAttackTwentyMillisecondsBeforeEof_IsFinalizedBeforeSaving()
    {
        using var h = new PlayModeReviewHarness(finalNote: true);
        // Capture sync, event age and the EOF stall budget all read this clock: the test owns time, so
        // a starved testhost cannot age the attack or expire the evidence wait between two ticks (#659).
        var clock = h.UseCaptureClock();
        h.Begin();
        var signal = PluckAt(PlayModeReviewHarness.SampleRate * 98 / 100);
        var sampleRate = PlayModeReviewHarness.SampleRate;
        const int block = 441;
        const int blockMs = 10;
        for (var offset = 0; offset < sampleRate; offset += block)
        {
            clock.Advance(blockMs);
            h.Audio.Feed(signal.AsSpan(offset, block));
            h.Audio.PublishPosition((offset + block) * 1000.0 / sampleRate);
            h.Tick();
        }
        var matcher = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Matcher!;
        var sound = Assert.Single(matcher.Expected);
        Assert.Null(sound.Verdict);
        h.Audio.IsPlaying = false;
        PlayModeReviewHarness.Set(h.Coordinator, "_lastUiUpdateTicks", 0L);
        h.Audio.PublishPosition(1000);
        h.Ui.Flush();
        Assert.Equal(1000, h.State.CurrentTimeMs);
        h.EndWatch();
        Assert.True(h.Live.IsActive, "EOF expired the judgment before the real detector could finalize the attack");
        // Further EOF polls without capture do not advance the injected evidence clock.
        h.EndWatch();
        h.EndWatch();

        for (var offset = sampleRate; offset + block <= signal.Length && h.Live.IsActive; offset += block)
        {
            clock.Advance(blockMs);
            h.Audio.Feed(signal.AsSpan(offset, block));
            h.Tick();
            h.EndWatch();
        }

        Assert.False(h.Live.IsActive);
        Assert.Equal(LivePlayVerdict.Hit, sound.Verdict);
        var profile = Assert.Single(h.Store.GetAll().Values);
        var saved = Assert.Single(profile.Sessions);
        Assert.Equal(1, saved.Hits);
        Assert.Equal(0, saved.Misses);
        Assert.True(profile.FullComboAchieved);
    }

    private static float[] PluckAt(int attack)
    {
        var samples = new float[PlayModeReviewHarness.SampleRate * 2];
        for (var i = attack; i < samples.Length; i++)
        {
            var time = (i - attack) / (double)PlayModeReviewHarness.SampleRate;
            var value = 0.0;
            for (var harmonic = 1; harmonic <= 6; harmonic++)
                value += Math.Sin(2 * Math.PI * 110 * harmonic * time) / harmonic;
            samples[i] = (float)(0.4 * value * (1 - Math.Exp(-time * 400)) * Math.Exp(-time * 2.2));
        }
        return samples;
    }
}

internal sealed class PlayModeReviewHarness : IDisposable
{
    internal const int SampleRate = 44_100;
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter_completion_").FullName;
    private readonly StemMixerViewModel? _stemMixer;
    public ProjectState State { get; } = new();
    public ReviewCapture Audio { get; } = new() { InputSampleRate = SampleRate };
    public ReviewDispatcher Ui { get; } = new();
    public FakeConfigurationService Config { get; } = new();
    public PracticeProfileService Store { get; }
    public PlayerProfileService Profiles { get; }
    public TransportViewModel Transport { get; }
    public LivePlayViewModel Live { get; }
    public AudioPlaybackCoordinator Coordinator { get; }
    public PlayModeViewModel Vm { get; }

    public PlayModeReviewHarness(bool finalNote = false, Func<ProjectState, FakeConfigurationService, RsTonePlaybackCoordinator>? tones = null,
        int noteMs = 980, double inputOffsetMs = 0, Func<ProjectState, ReviewCapture, StemMixerViewModel>? stems = null)
    {
        Config.AppConfig.LivePlayUseCqtVerifier = false;
        Config.AppConfig.GameplayCountInBeats = 0;
        Config.AppConfig.GameplayRockMeterEnabled = false;
        Config.AppConfig.LivePlayLatencyOffsetMs = inputOffsetMs;
        var project = new ChartProject();
        for (var i = 0; i < 4; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        if (finalNote) level.Notes.Add(new GuitarNote { Position = new FractionalPosition(noteMs / 500, new Fraction(noteMs % 500, 500)), StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        State.ActiveProject = project;
        State.ActiveArrangement = arrangement;
        State.TotalAudioLengthMs = 1000;
        State.CurrentProjectPath = Path.Combine(_directory, "chart.dcproj");
        Profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_directory, "profiles.json"), "Tester");
        Store = new PracticeProfileService(Profiles, null, Path.Combine(_directory, "practice.json"));
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var scope = new EditingScope(State);
        var loop = new EditorLoopService(State, new EditTargetService(selection, scope, Config), scope, logger);
        Transport = new TransportViewModel(Audio, State, Config, FakeLocalization.English(), null, logger, loop);
        var editor = new ChartEditorViewModel(State, Transport, new HistoryService(logger), selection,
            new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.English());
        Coordinator = new AudioPlaybackCoordinator(Audio, Transport, editor, State, loop, Ui);
        Live = LivePlayTestFactory.Create(Audio, State, logger, Config, Store);
        _stemMixer = stems?.Invoke(State, Audio);
        Vm = TestRigBuilders.PlayMode(Profiles, new GameplayStatsService(Store), new LeaderboardService(Profiles), State,
            FakeLocalization.English(), Config, livePlay: Live, transport: Transport, audio: Audio,
            rsToneCoordinator: tones?.Invoke(State, Config), stemMixer: _stemMixer);
    }

    public void Begin()
    {
        Invoke(Vm, "BeginGameplay");
        Assert.True(Live.IsActive);
        Get<LivePlayDetectionPump>(Live, "_pump").Stop();
        Ui.Flush();
    }

    /// <summary>Puts the capture sync stamps and the EOF completion on one clock the test advances.</summary>
    public PlayModeCompletionMatrixTests.CaptureClock UseCaptureClock()
    {
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        Audio.Clock = clock;
        Live.CompletionClock = clock;
        return clock;
    }

    public void Tick() => Invoke(Live, "TickCore");
    public void EndWatch() => Invoke(Vm, "EndWatchTick");
    public static object? Invoke(object owner, string name, params object?[] args)
        => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    public static T Get<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    public static void Set(object owner, string name, object value)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    public void FinishForResults()
    {
        Get<LivePlaySessionService>(Live, "_session").CompleteAt(1000);
        var report = Invoke(Live, "StopCore");
        Live.IsActive = false;
        Invoke(Vm, "OnSessionCompleted", report);
        Assert.Equal(PlayModeScreen.Results, Vm.CurrentScreen);
    }

    public void Dispose()
    {
        Vm.BackToSongSelectCommand.Execute(null);
        Live.IsActive = false;
        _stemMixer?.Dispose();
        BestEffortDelete.Directory(_directory);
    }

    internal sealed class ReviewCapture : FakeAudioPlaybackService, IAudioPlaybackService
    {
        private long _seekGeneration;
        public Action? BeforeSeekGenerationRead { get; set; }
        public long SeekGeneration
        {
            get
            {
                BeforeSeekGenerationRead?.Invoke();
                return _seekGeneration;
            }
        }
        public new void SeekTo(double timeMs)
        {
            _seekGeneration++;
            base.SeekTo(timeMs);
        }

        public Action? BeforePause { get; set; }
        public Action? AfterPause { get; set; }
        public Action? BeforeCaptureRead { get; set; }

        public new void Pause()
        {
            BeforePause?.Invoke();
            base.Pause();
            AfterPause?.Invoke();
        }

        private float[] _block = [];
        private ulong _readPosition;
        private ulong _writePosition;
        private long _syncTicks;
        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public void RebuildCapture() => _readPosition = _writePosition = 0;

        public void Feed(ReadOnlySpan<float> samples)
        {
            Assert.Empty(_block);
            _block = samples.ToArray();
            _writePosition += (ulong)_block.Length;
            _syncTicks = Clock.GetTimestamp();
        }

        public override int ReadInputCapture(float[] destination, out ulong streamPosition)
        {
            BeforeCaptureRead?.Invoke();
            streamPosition = _readPosition;
            var length = _block.Length;
            _block.CopyTo(destination, 0);
            _block = [];
            _readPosition += (ulong)length;
            return length;
        }

        public override bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
        {
            capturedSamples = _writePosition;
            hostTicks = _syncTicks;
            return _writePosition > 0;
        }
    }

    internal sealed class ReviewDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posted = new();
        public bool CheckAccess() => true;
        public void Post(Action action) => _posted.Enqueue(action);
        public void PostNormal(Action action) => Post(action);
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        public void Flush()
        {
            while (_posted.TryDequeue(out var action)) action();
        }
    }
}
