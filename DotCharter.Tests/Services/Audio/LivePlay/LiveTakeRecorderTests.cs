using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class LiveTakeRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.wav");

    [Fact]
    public async Task Writes_ValidPcm16MonoWav()
    {
        const int sampleRate = 48000;
        var path = NewPath();
        using var recorder = new LiveTakeRecorder(path, sampleRate, TimeSpan.FromMinutes(1));

        // One second of a ramp, teed in pump-sized blocks.
        var block = new float[1600];
        var written = 0;
        while (written < sampleRate)
        {
            for (var i = 0; i < block.Length; i++)
                block[i] = (written + i) / (float)sampleRate - 0.5f;
            recorder.Write(block);
            written += block.Length;
        }

        var result = await recorder.StopAsync();

        Assert.Equal(sampleRate, result.SamplesWritten);
        Assert.Equal(0, result.DroppedSamples);
        Assert.False(result.WasCapped);
        Assert.Equal(1.0, result.DurationSeconds(sampleRate), 3);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(44 + sampleRate * 2, bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));            // PCM
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));            // mono
        Assert.Equal(sampleRate, BitConverter.ToInt32(bytes, 24));   // rate
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));           // bits
        Assert.Equal(sampleRate * 2, BitConverter.ToInt32(bytes, 40)); // data size

        // Sample 36000 of the ramp is 0.25 → ~8192 in PCM16.
        var sample = BitConverter.ToInt16(bytes, 44 + 36000 * 2);
        Assert.InRange(sample, 8100, 8300);
    }

    [Fact]
    public async Task DurationCap_StopsAcceptance_AndFlagsCapped()
    {
        const int sampleRate = 1000;
        var path = NewPath();
        using var recorder = new LiveTakeRecorder(path, sampleRate, TimeSpan.FromMilliseconds(100));

        recorder.Write(new float[250]); // 250 ms offered, only 100 ms fits

        var result = await recorder.StopAsync();
        Assert.True(result.WasCapped);
        Assert.Equal(100, result.SamplesWritten);
    }

    [Fact]
    public async Task FullRing_DropsAndCounts_InsteadOfBlocking()
    {
        var path = NewPath();
        // Writer deliberately not started: the ring fills and must DROP.
        using var recorder = new LiveTakeRecorder(path, 48000, TimeSpan.FromMinutes(1),
            ringCapacitySamples: 100, autoStartWriter: false);

        recorder.Write(new float[80]);
        Assert.Equal(0, recorder.DroppedSamples);

        recorder.Write(new float[80]); // only 20 fit
        Assert.Equal(60, recorder.DroppedSamples);

        var result = await recorder.StopAsync(); // final drain persists the ring
        Assert.Equal(100, result.SamplesWritten);
        Assert.Equal(60, result.DroppedSamples);
    }

    [Fact]
    public async Task EmptyTake_ProducesHeaderOnlyFile()
    {
        var path = NewPath();
        using var recorder = new LiveTakeRecorder(path, 44100, TimeSpan.FromMinutes(1));

        var result = await recorder.StopAsync();

        Assert.Equal(0, result.SamplesWritten);
        Assert.Equal(44, new FileInfo(path).Length);
    }

    [Fact]
    public async Task WriterFailure_SurfacesOnTheResult_InsteadOfATruncatedTakeThatLooksHealthy()
    {
        // The writer used to swallow the IOException and stop consuming: the
        // take simply came up short with no way for anyone to know. Now the
        // failure rides out on the result so the store can log it.
        var stream = new FailAfterStream(failAfterBytes: 44 + 50 * 2);
        using var recorder = new LiveTakeRecorder(stream, "unused.wav", 48000, TimeSpan.FromMinutes(1),
            ringCapacitySamples: 1000, autoStartWriter: false);

        recorder.Write(new float[200]);
        var result = await recorder.StopAsync();

        Assert.NotNull(result.WriteFailure);
        Assert.IsType<IOException>(result.WriteFailure);
        Assert.Equal(0, result.SamplesWritten); // the whole first chunk failed
        Assert.Same(result.WriteFailure, recorder.WriteFailure);
    }

    [Fact]
    public async Task HealthyTake_ReportsNoWriteFailure()
    {
        using var recorder = new LiveTakeRecorder(NewPath(), 48000, TimeSpan.FromMinutes(1));
        recorder.Write(new float[100]);
        var result = await recorder.StopAsync();
        Assert.Null(result.WriteFailure);
    }

    /// <summary>Accepts the first N bytes, then throws like a disk that went away.</summary>
    private sealed class FailAfterStream(int failAfterBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > failAfterBytes) throw new IOException("disk gone");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > failAfterBytes) throw new IOException("disk gone");
            base.Write(buffer);
        }
    }

    [Fact]
    public async Task StopAsync_DrainsBacklogLargerThanOneWriterChunk()
    {
        const int samples = 40000; // > the 16384-sample writer chunk
        var path = NewPath();
        using var recorder = new LiveTakeRecorder(path, 48000, TimeSpan.FromMinutes(1),
            ringCapacitySamples: samples, autoStartWriter: false);

        recorder.Write(new float[samples]);
        var result = await recorder.StopAsync();

        Assert.Equal(samples, result.SamplesWritten);
        Assert.Equal(0, result.DroppedSamples);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
