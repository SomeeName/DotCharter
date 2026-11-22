using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Engine;
using DotCharter.Tone.Audio.Vst;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

public sealed class RebuildPublicationTests
{
    [Fact]
    public async Task SameSongMasterReloadDuringDecode_RestoresEveryStemSetting()
    {
        var rig = new MediaRig();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.BeforeOpen = () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "decode barrier was not released");
        };
        var rebuild = Task.Run(rig.Rebuild, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            rig.Request("song-a.wav");
        }
        finally { release.Set(); }
        await rebuild.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Null(rig.Published);
        Assert.True(rig.Opened!.Disposed);
        Assert.Equal(rig.Snapshot, rig.LiveStems);
        rig.LoadRequestedMaster();
        Assert.Equal("song-a.wav", rig.Published!.Path);
        Assert.Equal(rig.Snapshot, rig.LiveStems);
    }

    [Theory]
    [InlineData("song-a.wav")]
    [InlineData("SONG-A.wav")]
    public void SameSongReloadQueuedBeforeReopen_RestoresStemsDespiteObsoleteMaster(string identity)
    {
        var rig = new MediaRig();
        rig.Request(identity);
        rig.Rebuild();
        Assert.Null(rig.Opened);
        Assert.Null(rig.Published);
        Assert.Equal(rig.Snapshot, rig.LiveStems);
    }

    [Fact]
    public void ANewSongDuringDecode_DiscardsTheOldSourceAndStems()
    {
        var rig = new MediaRig();
        rig.BeforeOpen = () => rig.Request("song-b.wav");
        rig.Rebuild();
        Assert.Null(rig.Published);
        Assert.True(rig.Opened!.Disposed);
        Assert.Null(rig.LiveStems);
        rig.LoadRequestedMaster();
        Assert.Equal("song-b.wav", rig.Published!.Path);
    }

    [Fact]
    public void PublicationProof_CurrentEngine_ReopensTheMasterAndRestoresStems()
    {
        var rig = new MediaRig();
        rig.Rebuild();
        var published = Assert.IsType<MediaSource>(rig.Published);
        Assert.Equal("song-a.wav", published.Path);
        Assert.False(published.Disposed);
        Assert.Equal(rig.Snapshot, rig.LiveStems);
    }

    [Fact]
    public void MasterDecodeFailure_DoesNotDropTheSameSongsStems()
    {
        var rig = new MediaRig
        {
            BeforeOpen = () => throw new InvalidOperationException("decode failed")
        };
        Assert.Throws<InvalidOperationException>(rig.Rebuild);
        Assert.Equal(rig.Snapshot, rig.LiveStems);
    }

    [Fact]
    public void ReplacedEngine_DiscardsItsOutstandingDecode()
    {
        var rig = new MediaRig();
        rig.BeforeOpen = () => rig.Engine = new object();
        rig.Rebuild();
        Assert.Null(rig.Published);
        Assert.True(rig.Opened!.Disposed);
        Assert.Null(rig.LiveStems);
    }

    [Fact]
    public void RebuildCapture_ReturnsExactlyWhatOneSaveStateReturned()
    {
        using var plugin = new RecordingRebuildPlugin([1, 2, 3, 4]);
        var captured = VstRebuildState.Capture(plugin, null);
        Assert.Equal(plugin.State, captured);
        Assert.Equal(1, plugin.ReadCount);
    }

    [Fact]
    public void ThrowingSaveState_IsLoggedAndSkippedDuringRebuild()
    {
        using var plugin = new RecordingRebuildPlugin([])
        {
            OnRead = _ => throw new InvalidOperationException("state grew")
        };
        var logger = new FakeAppLogger();
        Assert.Null(VstRebuildState.Capture(plugin, logger));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal("WARN", warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Equal(1, plugin.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationProof_CapturedParameters_AreRestoredBeforeEffectOrInstrumentAttachment(bool instrument)
    {
        using var outgoing = new RecordingRebuildPlugin([9, 8, 7]);
        var state = VstRebuildState.Capture(outgoing, null);
        Assert.NotNull(state);
        outgoing.Dispose();
        using var replacement = new RecordingRebuildPlugin([0]);
        VstRebuildState.Restore(replacement, state, () =>
        {
            if (instrument) replacement.AttachToInstrument();
            else replacement.AttachToInput();
        });
        Assert.Equal(new byte[] { 9, 8, 7 }, replacement.StateAtAttach);
    }

    [Fact]
    public void AQueuedSlotAddition_PreservesCapturedStateForUnchangedPlugins()
    {
        var id = Guid.NewGuid();
        var captured = new VstChainEntry { Id = id, PluginPath = "effect.vst3", State = [9, 8], Bypass = false, Player = 0 };
        var queued = new VstChainEntry { Id = id, PluginPath = "EFFECT.vst3", Bypass = true, Player = 1 };
        var added = new VstChainEntry { PluginPath = "new.vst3" };
        var merged = VstRebuildState.Merge([queued, added], [captured])!;
        Assert.Equal(new byte[] { 9, 8 }, merged[0].State);
        Assert.True(merged[0].Bypass);
        Assert.Equal(1, merged[0].Player);
        Assert.Equal(added.Id, merged[1].Id);
        Assert.Null(merged[1].State);
    }

    [Fact]
    public void ChangedPluginIdentityOrPath_DoesNotInheritCapturedState()
    {
        var old = new VstChainEntry { PluginPath = "old.vst3", State = [9] };
        var changedPath = new VstChainEntry { Id = old.Id, PluginPath = "new.vst3" };
        var changedId = new VstChainEntry { PluginPath = old.PluginPath };
        var explicitState = new VstChainEntry { Id = old.Id, PluginPath = old.PluginPath, State = [4] };
        var merged = VstRebuildState.Merge([changedPath, changedId, explicitState], [old])!;
        Assert.Null(merged[0].State);
        Assert.Null(merged[1].State);
        Assert.Equal(new byte[] { 4 }, merged[2].State);
    }

    [Fact]
    public void PublicationProof_ANewEngine_ReplaysTheLastAcceptedUpload_AndRejectsAnObsoleteReplay()
    {
        var oldEngine = new SynthBackend();
        IChartSynthBackend current = oldEngine;
        var driver = new ChartSynthEngineDriver(null, () => current, () => true);
        var upload = Upload(4, 42);
        Assert.True(driver.UploadNotes(upload));
        var fresh = new SynthBackend();
        current = fresh;
        driver.ReplayTo(fresh);
        Assert.Equal(upload.Notes, fresh.Notes);
        driver.ReplayTo(oldEngine);
        Assert.Equal(1, oldEngine.Writes);
        Assert.Equal(42, driver.UploadedFingerprint);
    }

    [Fact]
    public async Task AnUploadWhoseEngineChangesDuringNativeWrite_DoesNotPublishItsFingerprint()
    {
        var oldEngine = new SynthBackend();
        IChartSynthBackend current = oldEngine;
        var driver = new ChartSynthEngineDriver(null, () => Volatile.Read(ref current), () => true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldEngine.BeforeNotes = () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "upload barrier was not released");
        };
        var pending = Task.Run(() => driver.UploadNotes(Upload(2, 12)), TestContext.Current.CancellationToken);
        var fresh = new SynthBackend();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Volatile.Write(ref current, fresh);
        }
        finally { release.Set(); }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Null(driver.UploadedFingerprint);
        driver.ReplayTo(fresh);
        Assert.Empty(fresh.Notes);
        Assert.True(driver.UploadNotes(Upload(3, 13)));
        Assert.Equal(13, driver.UploadedFingerprint);
        Assert.Equal(3, fresh.Notes.Length);
    }

    private static ChartNoteUpload Upload(int count, long fingerprint) => new()
    {
        Notes = new JuceChartNote[count], Bends = [], Fingerprint = fingerprint
    };

    private sealed class SynthBackend : IChartSynthBackend
    {
        public object Identity => this;
        public bool IsLive => true;
        public JuceChartNote[] Notes { get; private set; } = [];
        public Action? BeforeNotes { get; set; }
        public int Writes { get; private set; }
        public void SetChartNotes(int player, JuceChartNote[] notes, JuceChartBendPoint[] bends)
        {
            BeforeNotes?.Invoke();
            Notes = [.. notes];
            Writes++;
        }
        public bool TrySetChartVoice(int player, JuceChartVoice voice) => true;
        public bool TrySetChartInstrument(int player, JuceChartInstrument instrument) => true;
        public void SetChartSourceParams(int player, float gain, double offset, JuceChartSourceMode mode) { }
        public void SetChartSourceEnabled(int player, bool enabled) { }
        public bool TrySetInstrumentRouteMask(uint seatMask) => true;
        public bool TrySetChartRouteMask(int player, uint seatMask) => true;
        public bool TryGetChartSourceStatus(int player, out JuceChartSourceStatus status)
        {
            status = default;
            return false;
        }
    }

    private sealed class MediaSource(string path) : IDisposable
    {
        public string Path => path;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class MediaRig
    {
        private readonly object _mediaGate = new();
        private readonly object _playbackGate = new();
        private readonly MediaLoadRequest _loadedRequest = new("song-a.wav", 1);
        private MediaLoadRequest _request = new("song-a.wav", 1);
        public object Engine { get; set; } = new();
        public Action? BeforeOpen { get; set; }
        public MediaSource? Opened { get; private set; }
        public MediaSource? Published { get; private set; }
        public JuceAudioPlaybackService.StemMixerSnapshot Snapshot { get; } = new(
            [("guitar", "g.wav", 0.4, true, false, 125), ("bass", "b.wav", 0.8, false, true, -50)], true);
        public JuceAudioPlaybackService.StemMixerSnapshot? LiveStems { get; private set; }
        public void Request(string identity)
        {
            lock (_playbackGate) Volatile.Write(ref _request, new MediaLoadRequest(identity, _request.Generation + 1));
        }
        public void LoadRequestedMaster() => Published = new MediaSource(_request.Identity!);
        public void Rebuild()
        {
            var snapshot = _loadedRequest;
            var engine = Engine;
            LiveStems = null;
            MediaRebuildReplay.Replay<MediaSource, JuceAudioPlaybackService.StemMixerSnapshot>(_mediaGate, _playbackGate, snapshot, () => Volatile.Read(ref _request),
                engine, () => Engine, () =>
                {
                    BeforeOpen?.Invoke();
                    var source = new MediaSource(snapshot.Identity!);
                    Opened = source;
                    return source;
                }, source => Published = source, () => Published is null, Snapshot, stems => LiveStems = stems);
        }
    }
}
