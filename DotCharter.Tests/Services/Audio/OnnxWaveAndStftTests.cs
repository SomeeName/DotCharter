using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.AudioIo;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio;

public sealed class OnnxWaveAndStftTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "dotcharter_onnx_wave_tests_" + Guid.NewGuid().ToString("N"));

    public OnnxWaveAndStftTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Fact]
    public void Write_then_read_float32_wave_round_trips()
    {
        var samples = new float[256];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / 44100.0);
        var buf = new AudioBuffer(samples, SampleRate: 44100, Channels: 2);

        var path = Path.Combine(_tempDir, "roundtrip.wav");
        WavIo.WriteFloat32(path, buf);
        var read = WavIo.Read(path);

        Assert.Equal(buf.SampleRate, read.SampleRate);
        Assert.Equal(buf.Channels, read.Channels);
        Assert.Equal(buf.FrameCount, read.FrameCount);
        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], read.Interleaved[i], 5);
    }

    [Fact]
    public void Reader_rejects_non_riff_files()
    {
        var path = Path.Combine(_tempDir, "garbage.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Throws<InvalidDataException>(() => WavIo.Read(path));
    }

    [Fact]
    public void Split_channels_yields_per_channel_arrays()
    {
        // 4 frames, 2 channels: [L0,R0,L1,R1,L2,R2,L3,R3]
        var interleaved = new float[] { 1, -1, 2, -2, 3, -3, 4, -4 };
        var buf = new AudioBuffer(interleaved, 48000, 2);
        var split = buf.SplitChannels();
        Assert.Equal(2, split.Length);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, split[0]);
        Assert.Equal(new float[] { -1, -2, -3, -4 }, split[1]);
    }

    [Fact]
    public void Stft_round_trip_reconstructs_signal_within_tolerance()
    {
        const int n = 4096;
        var input = new float[n];
        var rng = new Random(42);
        for (var i = 0; i < n; i++)
            input[i] = (float)(Math.Sin(2.0 * Math.PI * 5.0 * i / n) + 0.3 * (rng.NextDouble() - 0.5));

        var spec = Stft.Forward(input, nFft: 1024, hop: 256);
        var reconstructed = Stft.Inverse(spec);

        Assert.Equal(input.Length, reconstructed.Length);
        // COLA-normalised inverse should be near-perfect away from the edges.
        var error = 0.0;
        for (var i = 512; i < n - 512; i++)
            error = Math.Max(error, Math.Abs(input[i] - reconstructed[i]));
        Assert.True(error < 1e-4, $"STFT round-trip max error too large: {error}");
    }

    [Fact]
    public void Stft_forward_produces_expected_frame_and_bin_counts()
    {
        var signal = new float[8000];
        var spec = Stft.Forward(signal, nFft: 1024, hop: 256);
        Assert.Equal(1024 / 2 + 1, spec.BinCount);
        // Centred padding adds nFft/2 on each side → (8000 + 1024 - 1024) / 256 + 1
        // = 8000 / 256 + 1 = 32 frames.
        Assert.Equal(32, spec.FrameCount);
        Assert.Equal(8000, spec.OriginalLength);
    }
}
