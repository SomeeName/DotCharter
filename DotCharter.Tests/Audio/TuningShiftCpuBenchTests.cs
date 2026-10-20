using System.Diagnostics;
using System.Globalization;
using System.Text;
using JuceSharp;

namespace DotCharter.Tests.Audio;

/// <summary>
///     #627: what the live "Play in my tuning" shift (#312) costs per audio block, shift off versus
///     +1 and +2 semitones, on a dense mix. A LOCAL measurement, never a gate test: it skips unless
///     <c>DOTCHARTER_BENCH_TUNING_SHIFT</c> is set to a file path, asserts no timing, and writes its
///     table there. It runs the real native engine on the headless "Null" device (the engine's own
///     callback, paced by a thread, silence in) -- no sound card is opened and no app is launched.
///     <para>
///         The load is the engine's own <c>dca_engine_get_callback_load</c>: the callback time as a
///         share of the block budget, an exponentially weighted average (0.5 s) sampled every 100 ms.
///         Two paths, because the app uses both: the six-stem group (summed, then stretched ONCE by
///         the shared stretcher) and a single full-mix source (its own stretcher).
///     </para>
/// </summary>
[Collection("NativeEngine")]
public sealed class TuningShiftCpuBenchTests
{
    private const string OutVar = "DOTCHARTER_BENCH_TUNING_SHIFT";
    private const int SourceRate = 44_100;
    private const int SettleMs = 2_500;
    private const int WindowMs = 12_000;

    [Fact]
    public void MeasureTheLiveShift_OffAndAtPlusOneAndTwo()
    {
        var outPath = Environment.GetEnvironmentVariable(OutVar);
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Assert.Skip($"local measurement: set {OutVar} to the file the table is written to");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "dc-bench627-wav");
        Directory.CreateDirectory(dir);
        var stems = Enumerable.Range(0, 6).Select(i => DenseWav(dir, $"stem{i}.wav", 100 + i)).ToArray();
        var mix = DenseWav(dir, "fullmix.wav", 7);
        var table = new StringBuilder();

        foreach (var frames in new[] { 480, 128 })
        {
            foreach (var scenario in new[] { "6 stems, group stretcher", "full mix, one source" })
            {
                using var engine = new JuceAudioEngine(new JuceEngineConfig
                {
                    OutputTypeName = "Null",
                    SampleRate = 48_000,
                    BufferFrames = frames,
                    EnableNullDevice = true,
                    EnableConsoleLog = false
                });
                var sources = new List<JuceAudioSource>();
                Action<float> setPitch;
                if (scenario.StartsWith('6'))
                {
                    foreach (var path in stems)
                    {
                        var source = engine.OpenFile(path);
                        source.StemGrouped = true;
                        sources.Add(source);
                    }

                    setPitch = v => engine.StemGroupPitchSemitones = v;
                }
                else
                {
                    var source = engine.OpenFile(mix);
                    sources.Add(source);
                    setPitch = v => source.PitchSemitones = v;
                }

                foreach (var source in sources) source.Play();
                Thread.Sleep(1_500);
                table.AppendLine(CultureInfo.InvariantCulture,
                    $"{scenario}, {frames} frames @48k (budget {frames / 48.0:F2} ms)");
                foreach (var semitones in new float[] { 0, 1, 2, 0 })
                {
                    var (median, peak) = Measure(engine, setPitch, semitones);
                    table.AppendLine(CultureInfo.InvariantCulture,
                        $"  shift {semitones:+0;-0;0}: median load {median * 100:F2} %, peak {peak * 100:F1} %");
                }

                foreach (var source in sources) source.Dispose();
            }
        }

        File.WriteAllText(outPath, table.ToString());
    }

    private static (float Median, float Peak) Measure(JuceAudioEngine engine, Action<float> setPitch, float semitones)
    {
        setPitch(semitones);
        Thread.Sleep(SettleMs);
        var samples = new List<float>();
        var peak = 0f;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < WindowMs)
        {
            if (engine.TryGetCallbackLoad(out var load))
            {
                samples.Add(load.Average);
                peak = Math.Max(peak, load.Peak);
            }

            Thread.Sleep(100);
        }

        samples.Sort();
        return (samples.Count == 0 ? 0f : samples[samples.Count / 2], peak);
    }

    /// <summary>Ninety seconds of stereo 16-bit with 24 partials and a noise floor: a dense, non-silent
    /// signal for the stretcher to chew on. Cached by name between runs.</summary>
    private static string DenseWav(string dir, string name, int seed)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        var random = new Random(seed);
        var frames = SourceRate * 90;
        var freqs = Enumerable.Range(0, 24).Select(_ => 80 + random.NextDouble() * 3000).ToArray();
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        var dataSize = frames * 4;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(SourceRate);
        writer.Write(SourceRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var i = 0; i < frames; i++)
        {
            double left = 0, right = 0;
            for (var k = 0; k < freqs.Length; k++)
            {
                var v = Math.Sin(2 * Math.PI * freqs[k] * i / SourceRate) / freqs[k] * 60;
                if ((k & 1) == 0) left += v;
                else right += v;
            }

            left += (random.NextDouble() - 0.5) * 0.05;
            right += (random.NextDouble() - 0.5) * 0.05;
            writer.Write((short)(Math.Clamp(left * 0.5, -1, 1) * 30000));
            writer.Write((short)(Math.Clamp(right * 0.5, -1, 1) * 30000));
        }

        return path;
    }
}
