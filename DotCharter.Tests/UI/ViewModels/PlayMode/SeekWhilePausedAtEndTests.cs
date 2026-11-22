using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     A seek while the transport stands stopped at the song end, its completion pending: a seek
///     that stays at the end still completes the run once; a seek away from it lets the run go on,
///     unrecorded, until it reaches the end again.
/// </summary>
public sealed class SeekWhilePausedAtEndTests
{
    private const int Block = PlayModeReviewHarness.SampleRate / 100;

    [Fact]
    public void SeekThatStaysAtTheEnd_StillCompletesTheRunOnce()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 700);
        var (clock, completion) = PlayToTheEnd(h);
        Assert.True(completion.IsPending);

        h.Audio.SeekTo(1000);
        for (var i = 0; i < 100 && h.Live.IsActive; i++)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[Block]);
            h.Tick();
            h.EndWatch();
        }

        Assert.False(h.Live.IsActive, "a seek that stayed at the song end left the run without a completion");
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.Equal(1, saved.Hits);
        h.EndWatch();
        Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
    }

    [Fact]
    public void SeekAwayFromTheEnd_LetsTheRunGoOn_AndItCompletesAtTheEnd()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 700);
        var (clock, completion) = PlayToTheEnd(h);
        Assert.True(completion.IsPending);

        h.Audio.SeekTo(500);
        for (var i = 0; i < 30; i++)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[Block]);
            h.Tick();
            h.EndWatch();
        }

        Assert.True(h.Live.IsActive, "a seek away from the end completed the run");
        Assert.False(completion.IsPending);
        Assert.Empty(h.Store.GetAll().Values.SelectMany(p => p.Sessions));

        h.Audio.Play();
        h.Ui.Flush();
        for (var startMs = 500; startMs < 2000 && h.Live.IsActive; startMs += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[Block]);
            h.Audio.IsPlaying = startMs < 990;
            h.Audio.PublishPosition(Math.Min(startMs + 10, 1000));
            h.Ui.Flush();
            h.Tick();
            if (startMs >= 990) h.EndWatch();
        }

        Assert.False(h.Live.IsActive, "the resumed run never completed at the song end");
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.Equal(1, saved.Hits);
    }

    // Plays the one-note song to its end with the note hit; the transport stops at the end and
    // the worker's tick opens the completion (no end-watch tick yet).
    private static (PlayModeCompletionMatrixTests.CaptureClock Clock, LivePlayCompletion Completion) PlayToTheEnd(PlayModeReviewHarness h)
    {
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        var completion = PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion");
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(completion, clock);
        h.Begin();
        var signal = PlayModeCompletionMatrixTests.Pluck(700);
        for (var startMs = 0; startMs < 1000; startMs += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(signal.AsSpan(startMs / 10 * Block, Block));
            h.Audio.IsPlaying = startMs < 990;
            h.Audio.PublishPosition(Math.Min(startMs + 10, 1000));
            h.Ui.Flush();
            h.Tick();
        }
        Assert.Equal(1, PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Stats!.Hits);
        return (clock, completion);
    }
}
