using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     A project's stem set waited for its SONG to finish decoding before its own
///     opens started, because <c>LoadStemsCore</c> and <c>SwapMasterSource</c> shared
///     <c>_mediaLoadGate</c>. The owner's log showed "media gate at 450 ms" (821 ms on a
///     longer song) in front of a ~500 ms stem open, so the stems became playable 1-1.9 s
///     after an 18 ms chart load. The two publish into different fields under <c>_gate</c>,
///     so the stem set now has its own <c>_stemLoadGate</c>; <c>ReplaceEngine</c> still
///     excludes both, so no open runs against an engine being torn down.
/// </summary>
[Collection("NativeEngine")]
public sealed class StemLoadMediaGateTests : IDisposable
{
    private const int StemRate = 44_100;

    /// <summary>Declared first: armed before the engine below opens (#606).</summary>
    private readonly NullDeviceEnvScope _nullDevice = NullDeviceEnvScope.Enable();
    private readonly FakeAppLogger _logger = new();
    private readonly JuceAudioPlaybackService _audio;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-stem-gate-").FullName;

    public StemLoadMediaGateTests(ITestOutputHelper output)
    {
        _out = output;
        _audio = new JuceAudioPlaybackService(FakeLocalization.English(), _logger);
        _audio.ReplaceEngineForTesting(new JuceEngineConfig
        {
            PreferredDriver = AudioDriver.Wasapi, OutputTypeName = "Null", InputTypeName = "Null",
            SampleRate = 48_000, BufferFrames = 2048, OutputChannels = 2, InputChannels = 1
        });
    }

    public void Dispose()
    {
        _audio.Dispose();
        _nullDevice.Dispose();
        BestEffortDelete.Directory(_dir);
    }

    [Fact]
    public async Task A_stem_set_loads_while_a_song_decode_holds_the_media_gate()
    {
        var stems = WriteStems(2, seconds: 1);
        Task load;
        bool finishedUnderTheSongGate;
        // Stands in for a song decode in flight: SwapMasterSource holds this gate for the
        // whole native open.
        lock (Gate("_mediaLoadGate"))
        {
            load = _audio.LoadStemsAsync(stems);
            // The wait handle, not Task.Wait: Wait may inline the queued body onto THIS
            // thread, which already owns the gate and would pass a reverted change.
            finishedUnderTheSongGate = ((IAsyncResult)load).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(60));
        }
        await load;

        Assert.True(finishedUnderTheSongGate,
            "LoadStemsCore waited for _mediaLoadGate: a project's stems queue behind its song decode again (#960).");
        Assert.Equal(["s0", "s1"], _audio.LoadedStemNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_stem_set_published_during_the_master_activate_starts_where_the_master_is()
    {
        var song = Path.Combine(_dir, "song.wav");
        WriteWav(song, 4);
        await _audio.LoadAudioAsync(song);
        var stems = WriteStems(2, seconds: 4);
        using var opened = new ManualResetEventSlim();
        _audio.StemSetOpenedHook = opened.Set;
        Task load;
        // Stands in for the master's Activate (MediaSourceSwap.Run), which pauses, seeks
        // and plays the sources under this gate, not under _gate.
        lock (Gate("_mediaOperationGate"))
        {
            load = _audio.LoadStemsAsync(stems);
            Assert.True(opened.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), "The stem set never opened.");
            // Lets an unserialized publish run inside the activate; a serialized one just waits.
            ((IAsyncResult)load).AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
            // The activate moves the master, as its Pause/Seek/Play does.
            MasterSource().Seek(TimeSpan.FromSeconds(2));
        }
        await load;

        var master = MasterSource().Position;
        Assert.InRange(master.TotalMilliseconds, 1_980, 2_020);
        foreach (var stem in StemSources())
        {
            Assert.True(Math.Abs((stem.Position - master).TotalMilliseconds) < 20,
                $"A stem published during the master activate sits at {stem.Position.TotalMilliseconds:0} ms, the master at "
                + $"{master.TotalMilliseconds:0} ms: the stem publish no longer excludes the activate (#960).");
        }
    }

    [Fact]
    public void ReplaceEngine_still_takes_the_stem_gate_inside_the_media_gate()
    {
        // A source lint: blocking a rebuild on a held gate is only observable as "did not
        // finish yet", a wall-clock claim. Without the stem gate here a rebuild could
        // destroy the engine under an in-flight stem open.
        var lifecycle = PlaybackServiceSourceFiles.All()
            .Single(f => Path.GetFileName(f) == "JuceAudioPlaybackService.EngineLifecycle.cs");
        var text = File.ReadAllText(lifecycle).ReplaceLineEndings("\n");
        Assert.Contains("lock (_mediaLoadGate)\n                lock (_stemLoadGate) ReplaceEngineGated(cfg);", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The before/after harness for this time-to-playable: a song and a six-stem set opened the
    ///     way a project open does, the song first. Logs the service's own "Stem load:" line;
    ///     asserts only that the set published.
    /// </summary>
    [Fact]
    public async Task Six_stem_project_open_logs_its_time_to_playable()
    {
        // One second by default so the gate stays cheap (it asserts no timing anyway);
        // DOTCHARTER_BENCH_STEM_SECONDS=260 matches the owner's ~92 MB stems for the
        // before/after numbers.
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("DOTCHARTER_BENCH_STEM_SECONDS"), out var s) ? s : 1;
        var song = Path.Combine(_dir, "song.wav");
        WriteWav(song, seconds);
        var stems = WriteStems(6, seconds);

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var workingSetBefore = process.WorkingSet64;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var songLoad = _audio.LoadAudioAsync(song);
        // Start the stems once the song's decode owns its gate, as on a real project open.
        for (var i = 0; i < 2_000 && !songLoad.IsCompleted && IsFree(Gate("_mediaLoadGate")); i++) await Task.Delay(1, TestContext.Current.CancellationToken);
        var stemLoad = _audio.LoadStemsAsync(stems);
        await stemLoad;
        var stemsAt = clock.Elapsed;
        await songLoad;

        var line = _logger.Snapshot().Select(e => e.Message).Single(m => m.StartsWith("Stem load:", StringComparison.Ordinal));
        process.Refresh();
        // Process-wide and monotonic: run this case on its own for a clean peak.
        var peakExtraMb = (process.PeakWorkingSet64 - workingSetBefore) / (1024.0 * 1024.0);
        var settledExtraMb = (process.WorkingSet64 - workingSetBefore) / (1024.0 * 1024.0);
        _out.WriteLine($"stems playable at {stemsAt.TotalMilliseconds:0} ms, song at {clock.Elapsed.TotalMilliseconds:0} ms, "
            + $"working set peak +{peakExtraMb:0} MB, settled +{settledExtraMb:0} MB ({StemSetOpener.DefaultMaxOpensInFlight} open(s) in flight)");
        _out.WriteLine(line);
        Assert.Equal(6, _audio.LoadedStemNames.Count);
    }

    private object Gate(string name) =>
        typeof(JuceAudioPlaybackService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_audio)!;

    private JuceAudioSource MasterSource() =>
        (JuceAudioSource)typeof(JuceAudioPlaybackService).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_audio)!;

    private JuceAudioSource[] StemSources()
    {
        var stems = (System.Collections.IDictionary)typeof(JuceAudioPlaybackService)
            .GetField("_stems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_audio)!;
        var sources = stems.Values.Cast<object>()
            .Select(e => (JuceAudioSource)e.GetType().GetField("Source")!.GetValue(e)!).ToArray();
        Assert.Equal(2, sources.Length);
        return sources;
    }

    private static bool IsFree(object gate)
    {
        if (!Monitor.TryEnter(gate)) return false;
        Monitor.Exit(gate);
        return true;
    }

    private StemLoadRequest[] WriteStems(int count, int seconds)
    {
        var stems = new StemLoadRequest[count];
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_dir, $"s{i}.wav");
            WriteWav(path, seconds);
            stems[i] = new StemLoadRequest($"s{i}", path);
        }
        return stems;
    }

    // 44.1 kHz stereo against a 48 kHz engine: every open pays the resample, as the owner's stems do.
    private static void WriteWav(string path, int seconds)
    {
        using var writer = new WavWriter(path, StemRate, 2);
        var block = new float[StemRate * 2];
        for (var f = 0; f < StemRate; f++)
            block[2 * f] = block[2 * f + 1] = 0.1f * MathF.Sin(2 * MathF.PI * 220 * f / StemRate);
        for (var s = 0; s < seconds; s++) writer.Write(block);
    }
}
