using DotCharter.Services.Export.Audio;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Gets a song into the one shape <see cref="LyricAcousticModel" /> accepts:
///     16 kHz mono in [-1, 1].
///     <para>
///         Decoding goes through <see cref="PcmAudioLoader" /> — the app's
///         standard path, the editor's own decoder first, ffmpeg and in-process after —
///         so every format the rest of DotCharter opens is alignable too. It
///         lands at 48 kHz, and 48 → 16 kHz is an exact 3:1 reduction.
///     </para>
///     <para>
///         <b>The downsample must be band-limited, and the obvious helper is
///         not.</b> <c>JuceAudioDecoder.DecodeAs</c> resamples by linear
///         interpolation, which does not remove the 8–24 kHz content that a 3:1
///         decimation folds straight back down into the speech band as aliasing —
///         directly on top of the formants the model reads. The Lanczos-8
///         converter scales its cutoff with the ratio, so it low-passes before it
///         decimates.
///     </para>
/// </summary>
public static class LyricAudioSource
{
    public static float[] LoadMono16k(string path, IList<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        var pcm = PcmAudioLoader.Load(path, warnings ?? []);
        return ToMono16k(pcm);
    }

    internal static float[] ToMono16k(PcmAudio pcm)
    {
        var frames = pcm.FrameCount;
        var channels = Math.Max(1, pcm.Channels);
        var mono = new float[frames];

        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += pcm.Samples[f * channels + c];
            mono[f] = sum / (channels * 32768f);
        }

        return pcm.SampleRate == LyricAcousticModel.RequiredSampleRate
            ? mono
            : SampleRateConverter.Resample(mono, 1, pcm.SampleRate, LyricAcousticModel.RequiredSampleRate);
    }
}
