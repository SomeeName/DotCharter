using DotCharter.Services.Audio.SilentTransport;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// The audio-less-chart silent-transport fallback: pure length derivation +
/// decision, and the cached silent-WAV generation (valid, silent, right length).
/// </summary>
public sealed class SilentAudioTrackTests : IDisposable
{
    private readonly string _dir;
    private readonly SilentAudioTrack _track;

    public SilentAudioTrackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dc-silent-test-" + Guid.NewGuid().ToString("N"));
        _track = new SilentAudioTrack(logger: null, cacheDirectoryOverride: _dir);
    }

    // ---- length derivation -------------------------------------------------

    [Fact]
    public void DeriveProjectLengthMs_EmptyProject_IsZero()
        => Assert.Equal(0, SilentAudioTrack.DeriveProjectLengthMs(new ChartProject()));

    [Fact]
    public void DeriveProjectLengthMs_UsesLastBeat()
    {
        var project = new ChartProject();
        project.Beats.Add(new BeatMarker { Time = 0 });
        project.Beats.Add(new BeatMarker { Time = 45_000 });
        project.Beats.Add(new BeatMarker { Time = 90_000 });
        Assert.Equal(90_000, SilentAudioTrack.DeriveProjectLengthMs(project));
    }

    // ---- plan (floor + tail + bucket) --------------------------------------

    [Fact]
    public void PlanLengthMs_FloorsShortCharts()
        => Assert.Equal(SilentAudioTrack.BucketMs, SilentAudioTrack.PlanLengthMs(1_000));

    [Fact]
    public void PlanLengthMs_AddsTailAndRoundsUpToBucket() =>
        // 200_000 + 2_000 tail = 202_000 → ceil to the 15_000 grid = 210_000.
        Assert.Equal(210_000, SilentAudioTrack.PlanLengthMs(200_000));

    [Fact]
    public void PlanLengthMs_NearbyLengths_ShareABucket()
        => Assert.Equal(SilentAudioTrack.PlanLengthMs(60_000), SilentAudioTrack.PlanLengthMs(63_000));

    // ---- fallback decision -------------------------------------------------

    [Theory]
    [InlineData(false, 120_000, true)]  // content, no audio → fallback
    [InlineData(true, 120_000, false)]  // has audio → no fallback
    [InlineData(false, 0, false)]       // no content (empty/new) → no fallback (free engine)
    public void ShouldUseFallback_Decision(bool hasAudio, double contentMs, bool expected)
        => Assert.Equal(expected, SilentAudioTrack.ShouldUseFallback(hasAudio, contentMs));

    // ---- silent WAV generation --------------------------------------------

    [Fact]
    public void GetOrCreateSilentWav_ProducesAValidSilentWavOfTheRightLength()
    {
        var path = _track.GetOrCreateSilentWav(60_000);
        Assert.True(File.Exists(path));

        var bytes = File.ReadAllBytes(path);
        // RIFF/WAVE header.
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        var audioFormat = BitConverter.ToUInt16(bytes, 20);   // 1 = PCM
        var channels = BitConverter.ToUInt16(bytes, 22);
        var sampleRate = BitConverter.ToUInt32(bytes, 24);
        var bits = BitConverter.ToUInt16(bytes, 34);
        var dataLen = BitConverter.ToUInt32(bytes, 40);
        Assert.Equal(1, audioFormat);
        Assert.Equal(1, channels);
        Assert.Equal(16, bits);

        // Duration matches the planned (bucketed) length: PlanLengthMs(60_000)=75_000.
        var durationMs = dataLen / (double)(sampleRate * channels * (bits / 8)) * 1000.0;
        Assert.Equal(SilentAudioTrack.PlanLengthMs(60_000), durationMs, 1.0);

        // Every PCM sample is silence.
        Assert.All(bytes.Skip(44), b => Assert.Equal(0, b));
    }

    [Fact]
    public void GetOrCreateSilentWav_ReusesTheFile_ForLengthsInTheSameBucket()
    {
        var p1 = _track.GetOrCreateSilentWav(60_000);
        var p2 = _track.GetOrCreateSilentWav(63_000); // same 75s bucket
        Assert.Equal(p1, p2);
    }

    [Fact]
    public void GetOrCreateSilentWav_RegeneratesLonger_WhenProjectGrows()
    {
        var shortPath = _track.GetOrCreateSilentWav(60_000);
        var longPath = _track.GetOrCreateSilentWav(300_000);
        Assert.NotEqual(shortPath, longPath);
        Assert.True(new FileInfo(longPath).Length > new FileInfo(shortPath).Length);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
