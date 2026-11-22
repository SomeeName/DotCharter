using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

public sealed class StemPlaybackOffsetTests
{
    [Theory]
    [InlineData(2, new float[] { 0, 0, 0, 0, 1, -1, 2, -2, 3, -3 })]
    [InlineData(-1, new float[] { 2, -2, 3, -3 })]
    [InlineData(-10, new float[] { 0, 0 })]
    public void OffsetChangesTheAudioHandedToNativePlayback(double offset, float[] expected)
    {
        var request = new StemLoadRequest("guitar", "guitar.wav", offset);
        var decoded = new AudioBuffer([1, -1, 2, -2, 3, -3], 1000, 2);
        using var result = StemPlaybackOffset.Open(request,
            _ => throw new InvalidOperationException("An offset must not open the unchanged file."),
            _ => decoded, bytes => new MemoryStream(bytes));
        var audio = WavIo.Read(result);

        Assert.Equal(1000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal(expected, audio.Interleaved);
    }

    [Fact]
    public void ZeroOffset_UsesTheExistingFileOpenWithoutDecoding()
    {
        using var expected = new MemoryStream();
        var actual = StemPlaybackOffset.Open(new StemLoadRequest("guitar", "guitar.wav"),
            path => { Assert.Equal("guitar.wav", path); return expected; },
            _ => throw new InvalidOperationException("Zero offset needs no decode."),
            _ => throw new InvalidOperationException("Zero offset needs no memory open."));

        Assert.Same(expected, actual);
    }
}
