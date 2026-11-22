using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>End-watch ordering may not turn a deliberate seek into a playback stall.</summary>
public sealed class PendingSeekCompletionReviewTests
{
    public static TheoryData<string, bool> CompletionStates
    {
        get
        {
            var cases = new TheoryData<string, bool>();
            foreach (var state in new[] { "unanchored", "anchored", "synthetic-drain", "anchored-drain", "reanchored-drain", "evidence-ready", "stalled-ready", "reset" })
                foreach (var capture in new[] { false, true }) cases.Add(state, capture);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CompletionStates))]
    public void EndWatchBeforeWorker_PreservesPendingSeekInEveryCompletionState(string state, bool capture)
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 700);
        var project = h.State.ActiveProject!;
        for (var i = 4; i < 22; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            StringIndex = 0, Fret = 5, Position = new FractionalPosition(4, Fraction.Zero)
        });
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            StringIndex = 0, Fret = 7, Position = new FractionalPosition(10, Fraction.Zero)
        });
        h.State.TotalAudioLengthMs = 10000;
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        var completion = PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion");
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(completion, clock);
        h.Begin();
        var signal = PlayModeCompletionMatrixTests.Pluck(700);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var ms = 10; ms <= 1000; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(signal.AsSpan((ms / 10 - 1) * block, block));
            h.Audio.PublishPosition(ms);
            h.Ui.Flush();
            h.Tick();
        }
        Assert.Equal(1, PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Stats!.Hits);
        Prepare(completion, clock, state);
        var previous = PlayModeReviewHarness.Get<long>(h.Live, "_lastSeekGeneration");
        h.Audio.SeekTo(10000); // real callback; no worker tick before the end watch
        h.Audio.IsPlaying = false;
        if (!capture) h.Audio.InputSampleRate = 0;
        h.Ui.Flush();
        Assert.True(h.Audio.SeekGeneration > previous);
        h.EndWatch();
        if (h.Live.IsActive) Assert.Equal(0, PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Stats!.Misses);
        for (var i = 0; i < 100 && h.Live.IsActive; i++)
        {
            clock.Advance(10);
            if (capture) h.Audio.Feed(new float[PlayModeReviewHarness.SampleRate / 100]);
            h.Tick();
            h.EndWatch();
        }

        Assert.False(h.Live.IsActive);
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.Equal(0, saved.Misses);
        Assert.Equal(1, saved.Hits);
        Assert.Equal(h.Audio.SeekGeneration,
            PlayModeReviewHarness.Get<long>(h.Live, "_lastSeekGeneration"));
        h.EndWatch();
        Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
    }

    internal static void Prepare(LivePlayCompletion completion, PlayModeCompletionMatrixTests.CaptureClock clock, string state)
    {
        completion.Reset();
        if (state == "unanchored") return;
        if (state is "anchored" or "anchored-drain") completion.Observe(0, 1000, 44100, 1);
        if (state == "anchored") return;
        completion.Begin(10000, 0, 44100, 256, 250, 0, 1);
        if (state == "reanchored-drain") completion.Observe(0, 10000, 44100, 1, discontinuity: true);
        if (state == "evidence-ready") Assert.True(completion.HasEvidence(ulong.MaxValue));
        if (state == "stalled-ready")
        {
            clock.Advance(1000);
            Assert.True(completion.HasEvidence(0));
        }
        if (state == "reset") completion.Reset();
    }
}
