using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #643: <see cref="GuitarInputAnalyzer" /> maps the analyzer's provisional onsets to
///     <see cref="LiveOnsetNotice" />s on the capture axis, and — because the analyzer's
///     <c>Reset</c> drops its pending onsets without a word — withdraws its outstanding ones
///     itself when a ring skip re-anchors it. Otherwise the matcher would keep a provisional onset
///     no event or withdrawal ever retires, free to cue a later hit.
/// </summary>
public sealed class GuitarInputAnalyzerOnsetNoticeTests
{
    private const int Sr = 44100;
    private const int Block = 1600;     // a 30 Hz pump's read

    private static float[] Pluck(double seconds, double f0 = 110.0)
    {
        var s = new float[(int)(seconds * Sr)];
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Sr;
            double v = 0;
            for (var h = 1; h <= 6; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            s[i] = (float)(0.4 * v * (1.0 - Math.Exp(-t * 400.0)) * Math.Exp(-t * 2.2));
        }

        return s;
    }

    [Fact]
    public void ADetectedOnset_IsResolvedByTheEventCarryingItsKey()
    {
        var audio = new float[Sr * 2];
        Pluck(1.5).CopyTo(audio, Sr / 10);
        var analyzer = new GuitarInputAnalyzer(Sr);
        var detected = new List<LiveOnsetNotice>();
        var events = new List<LiveDetectionEvent>();

        for (var off = 0; off + Block <= audio.Length; off += Block)
        {
            events.AddRange(analyzer.Push(audio.AsSpan(off, Block), (ulong)off));
            detected.AddRange(analyzer.OnsetNotices.Where(n => !n.IsWithdrawn));
        }

        var notice = Assert.Single(detected);
        var ev = Assert.Single(events, e => !e.IsLegato);
        Assert.Equal(notice.DetectorSamplePosition, ev.DetectorSamplePosition);
        Assert.InRange((long)notice.StreamSamplePosition - Sr / 10, -256, 256);   // the attack, within a hop
        Assert.Equal(notice.StreamSamplePosition / (double)Sr, notice.StreamSeconds, 9);
    }

    [Fact]
    public void ARingSkip_WithdrawsTheOnsetsTheAnalyzerDropped()
    {
        var audio = new float[Sr * 2];
        Pluck(1.5).CopyTo(audio, Sr / 10);
        var analyzer = new GuitarInputAnalyzer(Sr);

        // Push until the attack is detected — its event is still ~190 ms away.
        LiveOnsetNotice? pending = null;
        var off = 0;
        for (; off + Block <= audio.Length && pending is null; off += Block)
        {
            analyzer.Push(audio.AsSpan(off, Block), (ulong)off);
            pending = analyzer.OnsetNotices.FirstOrDefault(n => !n.IsWithdrawn) is { DetectorSamplePosition: > 0 } n
                ? n
                : null;
        }

        Assert.True(pending is not null, "the attack was never detected");
        Assert.Empty(analyzer.Push(audio.AsSpan(off, Block), (ulong)off));
        Assert.DoesNotContain(analyzer.OnsetNotices, n => n.IsWithdrawn);

        // The capture ring skips: the analyzer re-anchors and its pending onset is gone.
        off += Block;
        var skippedTo = (ulong)(off + Sr);
        analyzer.Push(new float[Block], skippedTo);

        Assert.All(analyzer.OnsetNotices, n => Assert.True(n.IsWithdrawn));
        Assert.Contains(analyzer.OnsetNotices, n => n.DetectorSamplePosition == pending.Value.DetectorSamplePosition);
    }
}
