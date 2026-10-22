using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     End to end through <see cref="JuceAudioPlaybackService" /> on the JUCE NULL
///     device: a loaded song is the clock, an uploaded chart plays through seat 0's
///     rig (its post-rig meter moves), the seat's capture ring — what live
///     detection, scoring and the tuner read — stays silent, and an engine REBUILD
///     (the path a device change, the NAM 48 kHz request and the watchdog take)
///     comes back with the synth on, the notes uploaded and the clock bound.
/// </summary>
[Collection("NativeEngine")]
public sealed class ChartSynthNullDeviceTests : IDisposable
{
    private const int Rate = 48_000;

    /// <summary>The null device wakes once per block, so a starved box slows the song by
    /// the number of wake-ups it needs: 2048 frames (43 ms) need a fifth of the wake-ups
    /// that 480 do for the same stretch of song.</summary>
    private const int BlockFrames = 2048;

    /// <summary>Notes the chart must start before the routing is judged. The first sounds
    /// at 0.5 s of song time, so the test asks for about a second of progress, not for a
    /// fixed stretch of the wall clock.</summary>
    private const int NotesToStart = 2;
    private readonly string _wav = Path.Combine(Path.GetTempPath(), $"chart-synth-{Guid.NewGuid():N}.wav");

    public ChartSynthNullDeviceTests()
    {
        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", "1");
        File.WriteAllBytes(_wav, SilentWav(8.0));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DCA_ENABLE_NULL_DEVICE", null);
        try { File.Delete(_wav); } catch (IOException) { /* best effort */ }
    }

    private static byte[] SilentWav(double seconds)
    {
        var frames = (int)(seconds * Rate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + frames * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 2); w.Write(new byte[frames * 2]);
        w.Flush();
        return ms.ToArray();
    }

    private static JuceEngineConfig NullConfig() => new()
    {
        PreferredDriver = AudioDriver.Wasapi,
        OutputTypeName = "Null",
        InputTypeName = "Null",
        SampleRate = Rate,
        BufferFrames = BlockFrames,
        OutputChannels = 2,
        InputChannels = 1,
    };

    /// <summary>The service's own engine (re)build — what a device change runs.</summary>
    private static void Rebuild(JuceAudioPlaybackService svc)
        => typeof(JuceAudioPlaybackService)
            .GetMethod("ReplaceEngine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(svc, [NullConfig()]);

    private static ChartNoteUpload Chart()
    {
        var notes = new List<GuitarNote>();
        for (var beat = 1; beat < 14; beat++)
            notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = 3 });
        return ChartNoteEventBuilder.Build(new Arrangement(), notes, [], ChartNoteEventBuilderTests.Beats(), null, 120);
    }

    /// <summary>Plays until <see cref="NotesToStart" /> notes started, the rig peaked and the
    /// ring delivered a quarter second; returns the seat's post-rig peak, what the capture
    /// ring carried and the engine's last status. The device paces the song in wall time,
    /// so what is awaited is the engine's own progress (notes started), never a window:
    /// the 60 s cap is a deadlock detector that cannot fire on a passing run. When the
    /// song runs out before the notes were counted (a starved read-ahead makes the clock
    /// jump, and a jump skips the notes it passes), the song restarts from 0.</summary>
    private static (float RigPeak, float RingPeak, long RingSamples, ChartSynthStatus Status) PlayAndListen(JuceAudioPlaybackService svc)
    {
        var ring = new float[8192];
        while (svc.ReadInputCapture(ring) > 0) { }
        svc.GetAndResetPlayerInputPeak(0);
        svc.Play();
        float rigPeak = 0, ringPeak = 0;
        long ringSamples = 0;
        ChartSynthStatus status = default;
        var seenPlaying = false;
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            rigPeak = MathF.Max(rigPeak, svc.GetAndResetPlayerInputPeak(0));
            int n;
            while ((n = svc.ReadInputCapture(ring)) > 0)
            {
                ringSamples += n;
                for (var i = 0; i < n; i++) ringPeak = MathF.Max(ringPeak, MathF.Abs(ring[i]));
            }
            svc.TryGetChartSynthStatus(out status);
            if (status.NotesStarted >= NotesToStart && rigPeak > 0.02f && ringSamples > Rate / 4) break;
            seenPlaying |= status.IsClockPlaying;
            if (seenPlaying && !status.IsClockPlaying)
            {
                svc.Pause();
                svc.SeekTo(0);
                svc.Play();
                seenPlaying = false;
            }

            Thread.Sleep(20);
        }
        svc.Pause();
        return (rigPeak, ringPeak, ringSamples, status);
    }

    /// <summary>#574: the editor's play-the-chart toggles re-read whether an engine is
    /// up on <see cref="IChartSynthEngine.ChartSynthAvailabilityChanged" />. Every
    /// rebuild raises it exactly once, AFTER the outcome is published — whether the
    /// null device opened or not, the handler already sees the final state.</summary>
    [Fact]
    public void ARebuild_RaisesAvailabilityChanged_AfterTheOutcome()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        IChartSynthEngine synth = svc;
        Assert.False(synth.IsChartSynthAvailable);
        var seen = new List<bool>();
        synth.ChartSynthAvailabilityChanged += (_, _) => seen.Add(synth.IsChartSynthAvailable);

        try { Rebuild(svc); }
        catch (TargetInvocationException ex) { Assert.Skip($"no null device here: {ex.InnerException?.Message}"); }

        Assert.Equal([synth.IsChartSynthAvailable], seen);
    }

    [Fact]
    public async Task TheChart_PlaysThroughTheRig_NeverTheCaptureRing_AndSurvivesARebuild()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try { Rebuild(svc); }
        catch (TargetInvocationException ex) { Assert.Skip($"no null device here: {ex.InnerException?.Message}"); }
        if (!svc.IsChartSynthAvailable) { Assert.Skip("the engine did not start on the null device"); return; }

        await svc.LoadAudioAsync(_wav);
        IChartSynthEngine synth = svc;
        var chart = Chart();
        // #50: a non-default instrument, which the rebuild must replay too.
        var lesPaul = new ChartSynthInstrument(ChartSynthModel.LesPaul, ChartSynthPickup.Neck, true);
        Assert.True(synth.TrySetChartSynthInstrument(lesPaul));
        synth.SetChartSynthEnabled(true);
        Assert.True(synth.UploadChartNotes(chart), "the engine refused a valid chart");

        var first = PlayAndListen(svc);
        Assert.True(first.Status.NotesStarted >= NotesToStart,
            $"only {first.Status.NotesStarted} notes started in 60 s: {first.Status}, ring {first.RingSamples} samples");
        Assert.True(first.RigPeak > 0.02f, $"the chart never reached seat 0's rig (post-rig peak {first.RigPeak})");
        Assert.True(first.RingSamples > Rate / 4, $"the capture ring delivered {first.RingSamples} samples — vacuous");
        Assert.Equal(0f, first.RingPeak);

        // The rebuild a device change runs. A fresh engine starts with the chart
        // source OFF and EMPTY; everything below is the replay.
        Rebuild(svc);
        Assert.True(synth.TryGetChartSynthStatus(out var after), "no engine after the rebuild");
        Assert.True(after.IsEnabled, "the rebuild dropped the chart synth's enabled state");
        Assert.Equal(chart.Notes.Length, after.NoteCount);
        Assert.True(after.IsClockBound, "the rebuild left the chart synth with no song clock");
        Assert.Equal(lesPaul, after.Instrument);
        Assert.True(after.IsVoiceLoaded, "the rebuilt engine has no voice although the synth is on");
        Assert.Equal(chart.Fingerprint, synth.UploadedChartFingerprint);

        svc.SeekTo(0);
        var second = PlayAndListen(svc);
        Assert.True(second.Status.NotesStarted >= NotesToStart && second.RigPeak > 0.02f,
            $"after the rebuild: {second.Status}, rig peak {second.RigPeak}");
        Assert.Equal(0f, second.RingPeak);

        synth.SetChartSynthEnabled(false);
        svc.WaitForChartSynthPushes();
        Assert.True(synth.TryGetChartSynthStatus(out var off));
        Assert.False(off.IsEnabled);
        // #61 part 2: OFF releases the voice (and with it its sample pack) natively.
        var deadline = Environment.TickCount64 + 60_000;   // a deadlock detector, not a budget
        while (synth.TryGetChartSynthStatus(out var s) && s.IsVoiceLoaded && Environment.TickCount64 < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(synth.TryGetChartSynthStatus(out off) && !off.IsVoiceLoaded,
            "the voice was still loaded 60 s after switching the synth off");
        Assert.True(synth.TryGetChartSynthStatus(out off));
        Assert.Equal(0, off.PackBytes);
        Assert.Equal(lesPaul, off.Instrument);   // the selection survives
    }

    /// <summary>
    ///     #66 on a real engine: what a rebuild replays is the EFFECTIVE instrument —
    ///     a playing song tone's override while it plays, the player profile's once
    ///     it stops — set by <see cref="ChartSynthInstrumentCoordinator" />.
    /// </summary>
    [Fact]
    public void ARebuild_ReplaysTheEffectiveInstrument_ToneOverrideThenProfile()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        try { Rebuild(svc); }
        catch (TargetInvocationException ex) { Assert.Skip($"no null device here: {ex.InnerException?.Message}"); }
        if (!svc.IsChartSynthAvailable) { Assert.Skip("the engine did not start on the null device"); return; }

        var root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new DotCharter.Services.Gameplay.Profiles.PlayerProfileService(FakeLocalization.English(),
                filePath: Path.Combine(root, "player-profiles.json"));
            using var instruments = new ChartSynthInstrumentCoordinator(svc, profiles,
                new ProfileChartSynthInstrumentStore(profiles), new FakeConfigurationService(),
                new DotCharter.Services.Audio.Dsp.Tones.SongToneEditStore(Path.Combine(root, "song-tones")));
            instruments.Start();
            IChartSynthEngine synth = svc;

            var mine = new ChartSynthInstrument(ChartSynthModel.LesPaul, ChartSynthPickup.Neck, true);
            var tones = new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.Bridge, false);
            Assert.True(instruments.SetInstrument(mine));
            instruments.OnSongTone("lib:CallMe", "clean", "Clean Tone");
            Assert.True(instruments.OverrideTone());
            Assert.True(instruments.SetInstrument(tones));
            synth.SetChartSynthEnabled(true);
            svc.WaitForChartSynthPushes();

            Rebuild(svc);
            Assert.True(synth.TryGetChartSynthStatus(out var during), "no engine after the rebuild");
            Assert.Equal(tones, during.Instrument);

            instruments.OnSongTone(null, null, null);   // the run ended
            svc.WaitForChartSynthPushes();
            Rebuild(svc);
            Assert.True(synth.TryGetChartSynthStatus(out var after), "no engine after the second rebuild");
            Assert.Equal(mine, after.Instrument);
            instruments.Flush();
            synth.SetChartSynthEnabled(false);
            svc.WaitForChartSynthPushes();
        }
        finally
        {
            DotCharter.Tone.Infrastructure.Storage.BestEffortDelete.Directory(root);
        }
    }
}
