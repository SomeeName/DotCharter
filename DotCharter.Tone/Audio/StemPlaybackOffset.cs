using JuceSharp.AudioIo;

namespace DotCharter.Tone.Audio;

/// <summary>Applies project timing once at load, so native seeks and tempo changes keep the offset.</summary>
internal static class StemPlaybackOffset
{
    internal static T Open<T>(StemLoadRequest request, Func<string, T> openFile,
        Func<string, AudioBuffer> decode, Func<byte[], T> openMemory)
    {
        if (request.OffsetMs == 0) return openFile(request.Path);
        if (!double.IsFinite(request.OffsetMs)) throw new ArgumentOutOfRangeException(nameof(request));

        var audio = decode(request.Path);
        var frameOffset = Math.Round(request.OffsetMs * audio.SampleRate / 1000.0);
        var maxFrames = (Array.MaxLength - 64L) / (sizeof(float) * audio.Channels);
        if (frameOffset > maxFrames - audio.FrameCount)
            throw new NotSupportedException("The offset stem exceeds the in-memory playback limit.");
        var frames = (long)Math.Max(-audio.FrameCount, frameOffset);
        var skip = frames < 0 ? (int)Math.Min(audio.FrameCount, -frames) : 0;
        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, audio.SampleRate, audio.Channels, leaveOpen: true))
        {
            if (frames > 0)
            {
                var silence = new float[checked(4096 * audio.Channels)];
                var remaining = frames;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, 4096);
                    writer.Write(silence.AsSpan(0, count * audio.Channels));
                    remaining -= count;
                }
            }
            if (skip < audio.FrameCount) writer.Write(audio.Interleaved.AsSpan(skip * audio.Channels));
            // A fully trimmed stem is silence, but still needs a decodable source.
            else if (frames <= 0) writer.Write(new float[audio.Channels]);
        }

        return openMemory(stream.ToArray());
    }
}
