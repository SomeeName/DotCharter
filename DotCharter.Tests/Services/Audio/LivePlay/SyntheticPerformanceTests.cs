using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     <b>The end-to-end note-detection harness.</b> Renders a chart to guitar-like audio,
///     "plays it perfectly", and drives the REAL pipeline — streaming CQT →
///     <see cref="LiveCqtVerifier"/> → <see cref="LiveNoteMatcher"/> →
///     <see cref="LivePlaySessionService"/> — asserting the game actually scores it.
///     <para>
///     This is the test class that should have existed. Every previous detection test
///     checked one component in isolation: the mapping arithmetic, or the verifier against
///     an offline CQT, or the matcher against hand-fed pitch events. Each passed. The
///     COMPOSITION was broken for months — the pump violated the CQT's push contract, so
///     in verify mode every pitched note was judged Miss at any practice speed. Nothing
///     caught it, because nothing ever ran the pieces together.
///     </para>
///     <para>
///     Faithful where it matters: the audio is WALL-CLOCK (a player plays at real pitch and
///     real tempo no matter what the transport does), it is delivered in the ~1600-sample
///     chunks the 30 Hz pump actually delivers, and the transport advances at speed×.
///     Deterministic, no hardware, no dataset — and slow enough to live in the release
///     lane (<c>tools/ci/release-lane.json</c>), not the per-change gate.
///     </para>
/// </summary>
public sealed class SyntheticPerformanceTests
{
    private readonly ITestOutputHelper _out;
    public SyntheticPerformanceTests(ITestOutputHelper output) => _out = output;

    private const int Sr = 44100;
    private const int TickSamples = 1600;   // what a 30 Hz pump reads per tick
    private const double Bpm = 120;         // 500 ms/beat
    private const double MsPerBeat = 60000.0 / Bpm;

    // Standard tuning, string 0 = low E (E2 = MIDI 40).
    private static readonly int[] OpenStringMidi = [40, 45, 50, 55, 59, 64];

    private static List<BeatMarker> Beats(int count = 64) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = Bpm })];

    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = new FractionalPosition(beat, Fraction.Zero),
        Fret = fret,
        StringIndex = stringIndex
    };

    private static int MidiOf(int stringIndex, int fret) => OpenStringMidi[stringIndex] + fret;

    // ── the "player" ────────────────────────────────────────────────────────────

    /// <summary>
    ///     A plucked string: a few harmonics with 1/n rolloff and an exponential decay.
    ///     Not a physical model — but it is what the CQT actually looks for (energy at the
    ///     fundamental and its partials), which is the property under test.
    /// </summary>
    private static void MixPluck(float[] buf, int atSample, int midi, double durSec, float gain = 0.5f)
    {
        var f0 = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        var len = (int)(durSec * Sr);
        for (var i = 0; i < len; i++)
        {
            var n = atSample + i;
            if (n < 0 || n >= buf.Length) continue;
            var t = i / (double)Sr;
            double s = 0;
            for (var h = 1; h <= 6; h++)
            {
                var fh = f0 * h;
                if (fh > Sr / 2.0) break;
                s += Math.Sin(2 * Math.PI * fh * t) / h;
            }
            // Fast attack so the onset is where we say it is, then a natural decay.
            var env = (1.0 - Math.Exp(-t * 400.0)) * Math.Exp(-t * 2.2);
            buf[n] += (float)(s * env) * gain;
        }
    }

    // ── the harness ─────────────────────────────────────────────────────────────

    private sealed record Perf(int Hits, int Misses, int Total)
    {
        public double HitRate => Total == 0 ? 0 : Hits / (double)Total;
    }

    /// <summary>
    ///     Play <paramref name="chart"/> perfectly at <paramref name="speed"/> and return
    ///     what the game scored.
    /// </summary>
    private static Perf PlayPerfectly(List<(int beat, int str, int fret)> chart, double speed, bool useCqt = true)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        foreach (var (beat, str, fret) in chart) level.Notes.Add(Note(beat, str, fret));
        arr.Levels.Add(level);
        var beats = Beats();

        // The player's audio, on the WALL-CLOCK axis. A note at chart time T is played at
        // real time T/speed — slowing the song down gives the player MORE real time.
        var lastChartMs = chart.Max(c => c.beat) * MsPerBeat;
        var totalWallSec = (lastChartMs / speed / 1000.0) + 3.0;
        var audio = new float[(int)(totalWallSec * Sr)];
        foreach (var (beat, str, fret) in chart)
        {
            var chartMs = beat * MsPerBeat;
            var wallSec = chartMs / speed / 1000.0;
            MixPluck(audio, (int)(wallSec * Sr), MidiOf(str, fret), durSec: 1.4);
        }

        var cqt = useCqt
            ? new LiveCqtVerifier(new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples))
            : null;

        var session = new LivePlaySessionService();
        session.Start(arr, level, beats, new LivePlaySessionOptions
        {
            ChartVerify = cqt is null ? null : cqt.Verify
        });
        session.SyncTo(0);

        // The pump, exactly as LivePlayViewModel.Tick runs it.
        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            var transportMs = (off / (double)Sr) * 1000.0 * speed;

            if (cqt is not null)
            {
                cqt.Push(audio.AsSpan(off, n), off);          // RAW capture axis
                cqt.SetAnchor(off, transportMs, speed);       // chart axis mapped in
            }

            // The matcher pull-verifies each sound as its hit window closes.
            session.HandleTick(transportMs, isPlaying: true);
        }

        var stats = session.Stats!;
        return new Perf(stats.Hits, stats.Misses, stats.TotalSounds);
    }

    // ── the tests ───────────────────────────────────────────────────────────────

    /// <summary>A run of single notes across the neck, played perfectly.</summary>
    private static List<(int beat, int str, int fret)> SingleNoteRun() =>
    [
        (4, 2, 2), (6, 2, 4), (8, 3, 2), (10, 3, 4),
        (12, 4, 1), (14, 4, 3), (16, 2, 5), (18, 3, 5),
    ];

    /// <summary>
    ///     <b>THE test.</b> "Note detection works when the song is slowed down."
    ///     <para>
    ///     Before the capture-axis fix this scored ZERO hits at every speed below 1.0 — the
    ///     CQT ring was reset on every push and never emitted a frame, so the verifier
    ///     confirmed nothing and the matcher missed everything. That is precisely the bug
    ///     the owner reported ("Fear of the Dark at 50%, detection didn't work at all"),
    ///     and this is the assertion that encodes it.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(0.6)]   // the speed ladder's default first pass
    [InlineData(0.5)]   // the reported case
    [InlineData(0.25)]  // the ladder's floor
    public void A_perfect_performance_is_scored_as_such_at_every_practice_speed(double speed)
    {
        var perf = PlayPerfectly(SingleNoteRun(), speed);

        _out.WriteLine($"speed {speed:0.00}: {perf.Hits}/{perf.Total} hits, {perf.Misses} misses " +
                       $"({perf.HitRate:P0})");

        Assert.Equal(8, perf.Total);
        Assert.True(perf.HitRate >= 0.75,
            $"speed {speed:0.00}: a perfect performance scored only {perf.HitRate:P0} " +
            $"({perf.Hits}/{perf.Total}) — detection is broken at this speed");
    }

    /// <summary>
    ///     The blind (non-CQT) path must also survive slow speeds. It has always had correct
    ///     speed math, so this is a guard against regressing it while working on the CQT.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void The_matcher_still_reports_the_full_chart_at_every_speed_without_the_cqt(double speed)
    {
        var perf = PlayPerfectly(SingleNoteRun(), speed, useCqt: false);
        Assert.Equal(8, perf.Total);
    }

    /// <summary>
    ///     Silence must score nothing. A detector that credits notes nobody played is worse
    ///     than one that misses them — the idle-phantom failure the noise gate exists to
    ///     stop. This must hold at slow speeds too, where a broken axis could look at the
    ///     wrong part of the buffer.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void Playing_nothing_scores_nothing(double speed)
    {
        var chart = SingleNoteRun();

        // Same pump, but the "player" never plays: the capture is silence.
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        foreach (var (beat, str, fret) in chart) level.Notes.Add(Note(beat, str, fret));
        arr.Levels.Add(level);

        var lastChartMs = chart.Max(c => c.beat) * MsPerBeat;
        var audio = new float[(int)(((lastChartMs / speed / 1000.0) + 3.0) * Sr)];

        var cqt = new LiveCqtVerifier(new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples));
        var session = new LivePlaySessionService();
        session.Start(arr, level, Beats(), new LivePlaySessionOptions { ChartVerify = cqt.Verify });
        session.SyncTo(0);

        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            var transportMs = (off / (double)Sr) * 1000.0 * speed;
            cqt.Push(audio.AsSpan(off, n), off);
            cqt.SetAnchor(off, transportMs, speed);
            session.HandleTick(transportMs, isPlaying: true);
        }

        var stats = session.Stats!;
        _out.WriteLine($"speed {speed:0.00}: silence scored {stats.Hits} hits (want 0)");
        Assert.Equal(0, stats.Hits);
    }

    /// <summary>
    ///     Diagnostic, not a gate: where does detection actually lose notes? Prints a
    ///     per-string hit rate so a regression has a shape, not just a number. The low E is
    ///     the known-hard case (82 Hz needs a ~24 ms window; the kernel is capped at 93 ms).
    /// </summary>
    [Fact]
    public void Diagnostic_per_string_hit_rate()
    {
        _out.WriteLine("string  midi(open)  hits/total   rate");
        for (var s = 0; s < 6; s++)
        {
            var chart = new List<(int beat, int str, int fret)>();
            for (var i = 0; i < 6; i++) chart.Add((4 + i * 2, s, 2 + i));
            var perf = PlayPerfectly(chart, 1.0);
            _out.WriteLine($"  {s}        {OpenStringMidi[s],3}       {perf.Hits}/{perf.Total}      {perf.HitRate:P0}");
        }
    }
}
