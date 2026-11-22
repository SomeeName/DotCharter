using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Engine;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Orderings around a real engine rebuild on the Null device: a stem clear, a borrowed
///     take's hand-back and a superseded free each land between the teardown and the replay
///     (the service's <c>RebuildReplayHook</c> / <c>StemSetOpenedHook</c> observers), so the
///     service's own replay decides what comes back. Two facts guard rows found already
///     holding (they pass without this batch): the plain rebuild and the free during an open.
/// </summary>
[Collection("NativeEngine")]
public sealed class MediaRebuildOwnershipTests : IDisposable
{
    private readonly JuceAudioPlaybackService _audio;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-rebuild-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _wave;

    public MediaRebuildOwnershipTests()
    {
        _audio = new JuceAudioPlaybackService(FakeLocalization.English(), new FakeAppLogger());
        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48_000, 1, leaveOpen: true)) writer.Write(new float[480]);
        _wave = stream.ToArray();
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task ARebuildWithoutAClear_RestoresEveryStem()
    {
        Start();
        await _audio.LoadAudioAsync(Write("song.wav"));
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav")), new StemLoadRequest("bass", Write("bass.wav"))]);

        Rebuild();

        Assert.Equal(new[] { "bass", "drums" }, _audio.LoadedStemNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClearingStemsBetweenTeardownAndReplay_LeavesNoStemsAndNoPendingRecovery()
    {
        Start();
        var song = Write("song.wav");
        await _audio.LoadAudioAsync(song);
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav"))]);
        var cleared = false;
        _audio.RebuildReplayHook = () =>
        {
            cleared = true;
            _audio.ClearStems();
        };

        Rebuild();
        _audio.RebuildReplayHook = null;

        Assert.True(cleared, "the rebuild never reached its media replay");
        Assert.False(_audio.HasStems);
        Assert.Equal(song, _audio.CurrentAudioPath);
        Assert.False(_audio.HasPendingMediaRebuild);
    }

    [Fact]
    public async Task ClearingStemsWhileTheReplayDecodesThem_DiscardsTheRestoredSet()
    {
        Start();
        await _audio.LoadAudioAsync(Write("song.wav"));
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav"))]);
        // The replay's opens finished: after its own clear check, before the set publishes.
        var cleared = false;
        _audio.StemSetOpenedHook = () =>
        {
            if (cleared) return;
            cleared = true;
            _audio.ClearStems();
        };

        Rebuild();
        _audio.StemSetOpenedHook = null;

        Assert.True(cleared, "the replay never opened the stem set");
        Assert.False(_audio.HasStems);
    }

    [Fact]
    public async Task ATakeHandedBackBetweenTeardownAndReplay_GetsTheSongsStemsBack()
    {
        Start();
        var song = Write("song.wav");
        await _audio.LoadAudioAsync(song);
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav"))]);
        var take = Request(Write("take.wav"), true, out var previous);
        Swap(take, previous, CancellationToken.None);
        Assert.True(_audio.HasStems);
        Task<bool>? handBack = null;
        _audio.RebuildReplayHook = () => handBack ??= _audio.TryRestoreAudioAsync(song, take.Identity!, take.Generation, 0);

        Rebuild();
        _audio.RebuildReplayHook = null;

        Assert.NotNull(handBack);
        Assert.True(await handBack.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Equal(song, _audio.CurrentAudioPath);
        Assert.Contains("drums", _audio.LoadedStemNames);
    }

    [Fact]
    public async Task AFreeSupersededByANewerSong_LeavesThatSongAndItsStems()
    {
        Start();
        // FreeAudio registers its empty request, then a newer song publishes before the
        // free reaches its release half.
        var free = Request(null, false, out _);
        var song = Request(Write("song-b.wav"), false, out _);
        Swap(song, null, CancellationToken.None);
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav"))]);
        _audio.FreeAudioFor(free);

        Assert.Equal(song.Identity, _audio.CurrentAudioPath);
        Assert.True(_audio.OwnsMedia(song.Identity!, song.Generation));
        Assert.Contains("drums", _audio.LoadedStemNames);
    }

    [Fact]
    public async Task AMasterFreeSupersededByANewerSong_LeavesThatSong()
    {
        Start();
        var free = Request(null, false, out _);
        var song = Request(Write("song-b.wav"), false, out _);
        Swap(song, null, CancellationToken.None);
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Write("drums.wav"))]);

        _audio.FreeMasterAudioFor(free);

        Assert.Equal(song.Identity, _audio.CurrentAudioPath);
        Assert.True(_audio.OwnsMedia(song.Identity!, song.Generation));
        Assert.Contains("drums", _audio.LoadedStemNames);
    }

    [Fact]
    public void AFreeDuringANativeOpen_KeepsTheOpenedSourceOff()
    {
        Start();
        var song = Request(Write("song-b.wav"), false, out _);

        Swap(song, null, CancellationToken.None, _audio.FreeAudio);

        Assert.Empty(_audio.CurrentAudioPath);
        Assert.False(_audio.OwnsMedia(song.Identity!, song.Generation));
    }

    private void Start()
    {
        try { Rebuild(); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { Assert.Skip($"no null device here: {ex.Message}"); }
    }

    private void Rebuild() =>
        _audio.ReplaceEngine(new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Wasapi, OutputTypeName = "Null", InputTypeName = "Null",
            SampleRate = 48_000, BufferFrames = 2048, OutputChannels = 2, InputChannels = 1, EnableNullDevice = true
        });

    private string Write(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, _wave);
        return path;
    }

    private MediaLoadRequest Request(string? identity, bool temporary, out MediaLoadRequest previous) =>
        _audio.RequestMediaLoad(identity, temporary, out previous);

    private void Swap(MediaLoadRequest request, MediaLoadRequest? previous, CancellationToken cancellation, Action? beforeOpen = null) =>
        _audio.SwapMasterSource(request.Identity!, request, engine =>
        {
            beforeOpen?.Invoke();
            return engine.OpenMemory(_wave, "wav");
        }, cancellation, previous);

    public void Dispose()
    {
        _audio.Dispose();
        BestEffortDelete.Directory(_dir);
    }
}
