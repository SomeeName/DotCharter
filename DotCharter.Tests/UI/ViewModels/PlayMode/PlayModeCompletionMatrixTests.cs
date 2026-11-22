using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Real capture and classic detection across EOF, offsets and unavailable capture; time is injected.</summary>
public sealed class PlayModeCompletionMatrixTests
{
    public static TheoryData<string, int, int> CompletionCases
    {
        get
        {
            var cases = new TheoryData<string, int, int>();
            foreach (var capture in new[] { "flowing", "silent", "no-input", "stopped", "rebuilt" })
            {
                foreach (var distance in new[] { 700, 120, 20 })
                    foreach (var offset in new[] { 0, 100, -100 }) cases.Add(capture, distance, offset);
            }
            return cases;
        }
    }

    [Fact]
    public void OutputOnlyDevice_SettlesExactlyOnceWithoutCaptureProgress()
        => Run("no-input", 20, 0);

    /// <summary>#629: song end 1000 ms, the last note at 740 ms, input offset +100 ms. Its finalised
    /// capture arrives near 1030 ms, after the playhead stood at the end; the stopped playhead
    /// expired the note first and a played last note was a miss. The end now settles it from the
    /// capture, so it is a hit.</summary>
    [Fact]
    public void PositiveOffset_TheLastNoteIsSettledFromItsCapture()
        => Run("flowing", 260, 100);

    /// <summary>The same last note, never played: the settlement still expires it as one miss.</summary>
    [Fact]
    public void PositiveOffset_AnUnplayedLastNoteIsStillAMiss()
        => Run("silent", 260, 100);

    [Fact]
    public void CaptureRebuildDuringPendingCompletion_ReanchorsTheFollowingAttack()
        => Run("rebuilt", 20, 100, rebuildAt: 1010);

    [Theory]
    [MemberData(nameof(CompletionCases))]
    public void CompletionMatrix_RecordsExactlyOnceUsingOnlyAvailableEvidence(string capture, int distance, int offset)
        => Run(capture, distance, offset);

    private static void Run(string capture, int distance, int offset, int rebuildAt = 760)
    {
        var noteMs = 1000 - distance;
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: noteMs, inputOffsetMs: offset);
        var clock = new CaptureClock();
        h.Audio.Clock = clock;
        // HEAD predates clock injection; that baseline must still execute the failing sequence.
        var completion = PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion");
        typeof(LivePlayCompletion).GetProperty("Clock")?.SetValue(completion, clock);
        h.Begin();
        var matcher = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Matcher!;
        var sound = Assert.Single(matcher.Expected);
        var signal = Pluck(noteMs + offset);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var step = 0; step < 250; step++)
        {
            var startMs = step * 10;
            clock.Advance(10);
            if (capture == "rebuilt" && startMs == rebuildAt) h.Audio.RebuildCapture();
            if (h.Live.IsActive && capture != "no-input" && (capture != "stopped" || startMs < 700))
                h.Audio.Feed(capture == "silent" ? new float[block] : signal.AsSpan(step * block, block));
            h.Audio.IsPlaying = startMs < 990;
            h.Audio.PublishPosition(Math.Min(startMs + 10, 1000));
            h.Ui.Flush();
            h.Tick();
            if (startMs >= 990) h.EndWatch();
        }

        Assert.False(h.Live.IsActive, "capture absence or a stream rebuild wedged EOF completion");
        var hasInput = capture is "flowing" or "rebuilt" || capture == "stopped" && distance == 700;
        // A positive offset still loses a capture whose window a PLAYING playhead closed first (the
        // expiry clock during play is main's, kept on purpose); a window still open when the
        // playhead stops at the end is settled from the capture.
        var expiresWhilePlaying = offset == 100 && noteMs + 250 < 990;
        Assert.Equal(hasInput && !expiresWhilePlaying ? LivePlayVerdict.Hit : LivePlayVerdict.Miss, sound.Verdict);
        var profile = Assert.Single(h.Store.GetAll().Values);
        Assert.Single(profile.Sessions);
        h.EndWatch();
        h.EndWatch();
        Assert.Single(profile.Sessions);
    }

    internal static float[] Pluck(int attackMs)
    {
        var sampleRate = PlayModeReviewHarness.SampleRate;
        var samples = new float[sampleRate * 3];
        var attack = attackMs * sampleRate / 1000;
        for (var i = attack; i < samples.Length; i++)
        {
            var time = (i - attack) / (double)sampleRate;
            var value = 0.0;
            for (var harmonic = 1; harmonic <= 6; harmonic++)
                value += Math.Sin(2 * Math.PI * 110 * harmonic * time) / harmonic;
            samples[i] = (float)(0.4 * value * (1 - Math.Exp(-time * 400)) * Math.Exp(-time * 2.2));
        }
        return samples;
    }

    internal sealed class CaptureClock : TimeProvider
    {
        private long _ticks = TimeProvider.System.GetTimestamp();
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += milliseconds * TimestampFrequency / 1000;
    }
}
