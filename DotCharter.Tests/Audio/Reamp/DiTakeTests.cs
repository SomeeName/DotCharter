using DotCharter.Tone.Audio.Reamp;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Audio.Reamp;

/// <summary>
///     The DI recorder follows a SYNTHETIC capture stream with its own cursor
///     and writes exactly the samples it saw — a WAV round trip, gaps written as
///     silence, a restarted stream re-anchored — plus the takes folder and the
///     offline resampler a re-amp loads a take through. All pure: no engine, no
///     device, no clock.
/// </summary>
public sealed class DiTakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-ditake-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TheRecorder_WritesExactlyTheCapturedStream_AsAMonoWavAtTheEngineRate()
    {
        var engine = new FakeReampEngine { ReampSampleRate = 44100 };
        engine.Capture(12345); // history before Start is NOT recorded
        var path = Path.Combine(_dir, "take.wav");
        using var rec = new DiTakeRecorder(engine);
        Assert.True(rec.Start(path));

        // Uneven polls, each inside the fake's ring.
        foreach (var n in new[] { 100, 0, 3000, 4000, 1 })
        {
            engine.Capture(n);
            rec.Poll();
        }
        engine.Capture(777); // arrives after the last poll: Stop drains it
        Assert.Equal(path, rec.Stop());
        Assert.False(File.Exists(path + ".recording"));

        var wav = WavIo.Read(path);
        Assert.Equal(44100, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(100 + 3000 + 4000 + 1 + 777, wav.FrameCount);
        for (var i = 0; i < wav.FrameCount; i++)
            Assert.Equal(FakeReampEngine.SampleAt(12345UL + (ulong)i), wav.Interleaved[i]);
        Assert.Equal(0, rec.GapFrames);
    }

    [Fact]
    public void AReaderThatFallsARingBehind_WritesTheHoleAsSilence_AndKeepsTheTiming()
    {
        var engine = new FakeReampEngine();
        var path = Path.Combine(_dir, "gap.wav");
        using var rec = new DiTakeRecorder(engine);
        Assert.True(rec.Start(path));
        engine.Capture(FakeReampEngine.RingSize + 500); // 500 samples overwritten before the first poll
        rec.Poll();
        rec.Stop();

        Assert.Equal(500, rec.GapFrames);
        var wav = WavIo.Read(path);
        Assert.Equal(FakeReampEngine.RingSize + 500, wav.FrameCount);
        Assert.All(wav.Interleaved.Take(500), s => Assert.Equal(0f, s));
        Assert.Equal(FakeReampEngine.SampleAt(500), wav.Interleaved[500]);
    }

    [Fact]
    public void AStreamThatRestarts_IsFollowed_NotLost()
    {
        var engine = new FakeReampEngine();
        using var rec = new DiTakeRecorder(engine);
        Assert.True(rec.Start(Path.Combine(_dir, "restart.wav")));
        engine.Capture(1000);
        rec.Poll();
        engine.RestartStream(); // an engine rebuild: the ring starts again at 0
        engine.Capture(300);
        rec.Poll();
        rec.Stop();
        Assert.Equal(1, rec.Restarts);
        Assert.Equal(1300, rec.Frames);
    }

    [Fact]
    public void NoEngine_MeansNoRecording_AndNothingOnDisk()
    {
        var engine = new FakeReampEngine { IsReampAvailable = false };
        var path = Path.Combine(_dir, "none.wav");
        using var rec = new DiTakeRecorder(engine);
        Assert.False(rec.Start(path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".recording"));
    }

    [Fact]
    public void AnEmptyRecording_LeavesNoFile()
    {
        var engine = new FakeReampEngine();
        var path = Path.Combine(_dir, "empty.wav");
        using var rec = new DiTakeRecorder(engine);
        Assert.True(rec.Start(path));
        Assert.Null(rec.Stop());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".recording"));
    }

    [Fact]
    public void TheLibrary_ListsRenamesImportsAndDeletes()
    {
        var lib = new DiTakeLibrary(_dir);
        Assert.Empty(lib.List()); // folder does not exist yet

        var first = lib.NewRecordingPath(new DateTime(2026, 9, 27, 14, 3, 12));
        Assert.Equal("DI 2026-09-27 14-03-12.wav", Path.GetFileName(first));
        Directory.CreateDirectory(_dir);
        WavIo.WriteFloat32(first, new AudioBuffer(new float[480], 48000, 1));
        File.WriteAllText(first + ".recording", "unfinished"); // never a take
        var second = lib.NewRecordingPath(new DateTime(2026, 9, 27, 14, 3, 12));
        Assert.Equal("DI 2026-09-27 14-03-12 (2).wav", Path.GetFileName(second));

        var take = Assert.Single(lib.List());
        var renamed = lib.Rename(take, "Verse riff: take 1");
        Assert.NotNull(renamed);
        Assert.Equal("Verse riff_ take 1", renamed!.Name); // ':' is not a file-name character
        Assert.Null(lib.Rename(renamed, "   "));

        var outside = Path.Combine(Path.GetTempPath(), "dc-import-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WavIo.WriteFloat32(outside, new AudioBuffer(new float[480], 48000, 2));
            var imported = lib.Import(outside);
            Assert.NotNull(imported);
            Assert.Equal(2, lib.List().Count);
            Assert.Null(lib.Import(outside + ".txt"));
            Assert.True(lib.Delete(imported!));
        }
        finally { File.Delete(outside); }
        Assert.Equal(["Verse riff_ take 1"], lib.List().Select(t => t.Name));
    }

    [Fact]
    public void MixToMono_AveragesTheChannels()
        => Assert.Equal([0.5f, 0.0f], DiTakeLibrary.MixToMono([1f, 0f, 0.25f, -0.25f], 2));

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 48000)]
    public void TheResampler_KeepsAnInBandToneAndItsLength(int from, int to)
    {
        const double hz = 1000;
        var n = from; // one second
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)Math.Sin(2 * Math.PI * hz * i / from);

        var y = DiTakeResampler.Resample(x, from, to);

        Assert.Equal((int)Math.Round(n * (double)to / from), y.Length);
        // Away from the edges the output IS the tone sampled at the new rate.
        var worst = 0.0;
        for (var j = 200; j < y.Length - 200; j++)
            worst = Math.Max(worst, Math.Abs(y[j] - Math.Sin(2 * Math.PI * hz * j / to)));
        Assert.True(worst < 2e-3, $"{from}->{to}: worst error {worst}");
    }

    [Fact]
    public void ADownsample_LowPassesFirst_SoTheTopOctaveDoesNotFoldBack()
    {
        // 30 kHz at 96 kHz is above the 24 kHz Nyquist of 48 kHz: linear
        // interpolation would fold it to 18 kHz at nearly full level.
        const int from = 96000, to = 48000;
        var x = new float[from];
        for (var i = 0; i < from; i++) x[i] = (float)Math.Sin(2 * Math.PI * 30000 * i / from);
        var y = DiTakeResampler.Resample(x, from, to);
        var peak = y.Skip(500).Take(y.Length - 1000).Max(v => Math.Abs(v));
        Assert.True(peak < 0.01, $"aliased residue {peak}");
    }

    [Fact]
    public void TheSameRate_IsACopy()
    {
        float[] x = [0.1f, 0.2f, 0.3f];
        var y = DiTakeResampler.Resample(x, 48000, 48000);
        Assert.Equal(x, y);
        Assert.NotSame(x, y);
    }
}
