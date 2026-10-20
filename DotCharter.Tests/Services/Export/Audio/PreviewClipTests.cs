using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export.Audio;

public sealed class PreviewClipTests
{
    private static PcmAudio Tone(int seconds, short amplitude = 1000)
    {
        var samples = new short[48_000 * seconds * 2];
        Array.Fill(samples, amplitude);
        return new PcmAudio(samples, 2, 48_000);
    }

    [Fact]
    public void Clip_is_28s_from_the_requested_start_with_faded_edges()
    {
        var clip = PreviewClip.Create(Tone(60), startMs: 10_000);

        Assert.Equal(48_000 * 28, clip.FrameCount);
        Assert.Equal(0, clip.Samples[0]);
        Assert.Equal(0, clip.Samples[^1]);
        Assert.Equal(1000, clip.Samples[clip.Samples.Length / 2]);
    }

    [Fact]
    public void Short_songs_clip_to_the_remainder_and_halve_the_fades()
    {
        var clip = PreviewClip.Create(Tone(4), startMs: 0);

        Assert.Equal(48_000 * 4, clip.FrameCount);
        Assert.Equal(0, clip.Samples[0]);
        // Halved fade-in is 1.25 s — just after it the signal is at full level
        // (and still before the halved 1.5 s fade-out starting at 2.5 s).
        var afterFadeIn = (int)(48_000 * 1.3) * 2;
        Assert.Equal(1000, clip.Samples[afterFadeIn]);
    }

    [Fact]
    public void Start_beyond_the_end_clamps_into_the_song()
    {
        var clip = PreviewClip.Create(Tone(2), startMs: 999_999);

        Assert.True(clip.FrameCount > 0);
    }
}
