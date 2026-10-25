using DotCharter.Services.Audio.Lyrics;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     Each lyric model really RUNS on DirectML: a graph with <c>Reshape allowzero</c>
///     opens on DirectML and fails its first encode, so opening is not enough. Encodes the
///     same audio on DirectML and on the CPU and expects the same symbol on almost every
///     frame. Skips without model folders (<c>DOTCHARTER_LYRIC_MODEL_DIRS</c>, each holding
///     <c>model.onnx</c> + <c>vocab.json</c>) or without a DirectML adapter.
/// </summary>
public sealed class LyricModelDirectMlSmokeTests(ITestOutputHelper output)
{
    private const string ModelDirsVariable = "DOTCHARTER_LYRIC_MODEL_DIRS";

    /// <summary>The CPU check of 2026-10-05 matched 99.9 % of frames; this leaves room for the input.</summary>
    private const double MinArgmaxAgreement = 0.95;

    [Fact]
    public void Every_model_encodes_on_DirectML_like_on_the_CPU()
    {
        var dirs = Environment.GetEnvironmentVariable(ModelDirsVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dirs),
            $"{ModelDirsVariable} is not set (path-separated lyric model folders).");

        var audio = SyntheticVoice(seconds: 24.0);
        foreach (var dir in dirs!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var model = new LyricModel(Path.GetFileName(dir), dir, 0.0);
            using var gpu = LyricAcousticSessionFactory.Default(model, OnnxExecutionProvider.DirectML, output.WriteLine);
            Assert.SkipWhen(gpu.ActiveProvider != OnnxExecutionProvider.DirectML, "No DirectML adapter on this machine.");
            using var cpu = LyricAcousticSessionFactory.Default(model, OnnxExecutionProvider.Cpu, null);

            var onGpu = gpu.Encode(audio, TestContext.Current.CancellationToken);
            var onCpu = cpu.Encode(audio, TestContext.Current.CancellationToken);

            Assert.Equal(onCpu.FrameCount, onGpu.FrameCount);
            Assert.True(onGpu.FrameCount > 0, $"{dir}: DirectML wrote no frames");
            var agreement = ArgmaxAgreement(onGpu.LogProbs, onCpu.LogProbs, onGpu.FrameCount, onGpu.Width);
            output.WriteLine($"{Path.GetFileName(dir)}: {onGpu.FrameCount} frames, argmax agreement {agreement:P2}");
            Assert.True(agreement >= MinArgmaxAgreement, $"{dir}: DirectML and CPU agree on {agreement:P2} of frames");
        }
    }

    private static double ArgmaxAgreement(float[] a, float[] b, int frames, int width)
    {
        var same = 0;
        for (var f = 0; f < frames; f++)
        {
            if (Argmax(a.AsSpan(f * width, width)) == Argmax(b.AsSpan(f * width, width))) same++;
        }

        return (double)same / frames;
    }

    private static int Argmax(ReadOnlySpan<float> row)
    {
        var best = 0;
        for (var i = 1; i < row.Length; i++)
        {
            if (row[i] > row[best]) best = i;
        }

        return best;
    }

    /// <summary>A voiced, syllable-paced tone: harmonics of a gliding pitch, gated at 4 Hz.</summary>
    private static float[] SyntheticVoice(double seconds)
    {
        const int rate = 16_000;
        var samples = new float[(int)(seconds * rate)];
        var phase = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            phase += 2 * Math.PI * (180 + 40 * Math.Sin(2 * Math.PI * 0.3 * t)) / rate;
            var gate = Math.Max(0, Math.Sin(2 * Math.PI * 4 * t));
            samples[i] = (float)(0.2 * gate * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase)));
        }

        return samples;
    }
}
