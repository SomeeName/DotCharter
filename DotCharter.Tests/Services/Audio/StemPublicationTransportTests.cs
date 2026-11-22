using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #571 row 2: a stem load read tempo, pitch and pan BEFORE the decode and published
///     those values. The setters skip the stem group while no stem is loaded, so a speed
///     change made during the decode left the new stems at the old speed. The change is
///     made from the seam between the decode and the publication, on the null device.
/// </summary>
[Collection("NativeEngine")]
public sealed class StemPublicationTransportTests : IDisposable
{
    private readonly JuceAudioPlaybackService _audio = new(FakeLocalization.English());
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stem-publish-" + Guid.NewGuid().ToString("N"));

    public StemPublicationTransportTests()
    {
        _audio.ReplaceEngineForTesting(new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Wasapi, OutputTypeName = "Null", InputTypeName = "Null",
            SampleRate = 48_000, BufferFrames = 2048, OutputChannels = 2, InputChannels = 1,
            EnableNullDevice = true
        });
        Directory.CreateDirectory(_dir);
        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48_000, 1, leaveOpen: true)) writer.Write(new float[4800]);
        File.WriteAllBytes(Path.Combine(_dir, "drums.wav"), stream.ToArray());
    }

    [Fact]
    public async Task ASpeedAndPitchChangeDuringTheDecode_ReachTheStemGroupItPublishes()
    {
        Assert.NotNull(_audio.EngineForTesting);
        _audio.PlaybackSpeed = 1.0;
        _audio.PitchShift = 0.0;
        _audio.StemSetOpenedHook = () =>
        {
            _audio.PlaybackSpeed = 1.5;
            _audio.PitchShift = 3.0;
        };

        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Path.Combine(_dir, "drums.wav"))]);

        Assert.True(_audio.HasStems);
        var engine = _audio.EngineForTesting!;
        Assert.Equal(1.5f, engine.StemGroupTempo);
        Assert.Equal(3.0f, engine.StemGroupPitchSemitones);
    }

    [Fact]
    public async Task AClearDuringTheDecode_DiscardsTheOpenedSet()
    {
        _audio.StemSetOpenedHook = _audio.ClearStems;

        await _audio.LoadStemsAsync([new StemLoadRequest("drums", Path.Combine(_dir, "drums.wav"))]);

        Assert.False(_audio.HasStems);
    }

    public void Dispose()
    {
        _audio.Dispose();
        BestEffortDelete.Directory(_dir);
    }
}
