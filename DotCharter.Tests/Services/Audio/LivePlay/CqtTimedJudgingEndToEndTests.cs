using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #329 end to end: a synthetic performance through the REAL pipeline — the attack analyzer
///     (<see cref="GuitarInputAnalyzer" />), the streaming CQT (<see cref="LiveCqtVerifier" />,
///     with its early check) and <see cref="LivePlaySessionService" /> — pumped in 30 Hz chunks the
///     way <c>LivePlayViewModel.Tick</c> pumps them: analyzer events offered, then the tick.
///     <para>
///         <b>This is the test the matcher's own tests cannot be.</b> They feed onsets by hand,
///         in whatever order the test writes. Here the order is the real one: the CQT confirms a
///         note ~100 ms after its chart time and the analyzer REPORTS its onset ~180–250 ms after
///         the attack (measured on this harness; <c>JuceGuitarAnalyzer.LatencySamples</c> is 8,392
///         samples at 44.1 kHz). A matcher that paired only at confirmation found no onset for
///         any on-time note, so every hit stayed untimed — the bug, unchanged. And one that held
///         the hit for its onset showed every hit ~100 ms later than the CQT confirmed it — the
///         second test, which compares each hit's display tick with the CQT's own.
///     </para>
/// </summary>
public sealed class CqtTimedJudgingEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public CqtTimedJudgingEndToEndTests(ITestOutputHelper output) => _out = output;

    private const int Sr = 44100;
    private const int TickSamples = 1600;   // what a 30 Hz pump reads per tick
    private const double Bpm = 120;
    private const double MsPerBeat = 60000.0 / Bpm;
    private static readonly int[] OpenStringMidi = [40, 45, 50, 55, 59, 64];

    // One note a beat across five strings: (beat, string, fret).
    private static readonly (int Beat, int Str, int Fret)[] Run =
        [(2, 2, 2), (3, 2, 4), (4, 3, 2), (5, 3, 4), (6, 4, 1), (7, 4, 3), (8, 1, 5), (9, 0, 3)];

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = Bpm })];

    /// <summary>The same plucked-string synth as <see cref="SyntheticPerformanceTests" />.</summary>
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

            var env = (1.0 - Math.Exp(-t * 400.0)) * Math.Exp(-t * 2.2);
            buf[n] += (float)(s * env) * gain;
        }
    }

    /// <summary>One credited hit: its judgment and the pump time it was SHOWN at (the end of the
    /// tick that produced it); <see cref="Grade" /> and <see cref="GradedAtMs" /> when an onset
    /// graded it later.</summary>
    private sealed class Credited(LivePlayEvent credit, double shownAtMs)
    {
        public LivePlayEvent Credit { get; } = credit;
        public double ShownAtMs { get; } = shownAtMs;
        public LivePlayGrade? Grade { get; set; }
        public double GradedAtMs { get; set; } = double.NaN;

        /// <summary>The provisional cue (#643) and the pump time it was shown at.</summary>
        public LivePlayGrade? Cue { get; set; }
        public double CuedAtMs { get; set; } = double.NaN;

        public LiveTimingTier Tier => Credit.Tier != LiveTimingTier.None ? Credit.Tier : Grade?.Tier ?? LiveTimingTier.None;

        public LiveTimingDirection Direction =>
            Credit.Tier != LiveTimingTier.None ? Credit.Direction : Grade?.Direction ?? LiveTimingDirection.None;

        public double AttackAtMs => Credit.Tier != LiveTimingTier.None ? Credit.DetectedAtMs : Grade!.Value.AttackAtMs;
    }

    private sealed record Played(List<Credited> Hits, LivePlaySessionResult Result, double MedianReportLatencyMs);

    /// <summary><see cref="Run" /> as a chart, and as audio with every attack
    /// <paramref name="offsetMs" /> off it.</summary>
    private static (Arrangement Arr, ChartLevel Level, float[] Audio) Performance(double offsetMs)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        foreach (var (beat, str, fret) in Run)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = str, Fret = fret });
        arr.Levels.Add(level);

        var audio = new float[Sr * 7];
        foreach (var (beat, str, fret) in Run)
            MixPluck(audio, (int)((beat * MsPerBeat + offsetMs) / 1000.0 * Sr), OpenStringMidi[str] + fret, 1.4);
        return (arr, level, audio);
    }

    private static LiveCqtVerifier NewCqt() =>
        new(new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples));

    /// <summary>
    ///     When the CQT itself CONFIRMS each note, asked straight — no matcher, no onsets: the
    ///     first tick, from <see cref="LiveCqtConfig.EarlyCreditAfterMs" /> on, at which the early
    ///     check says yes. This is the moment a CQT hit was shown before #329, and the moment it
    ///     must still be shown. Keyed by chart time.
    /// </summary>
    private static Dictionary<double, double> CqtConfirmations(double offsetMs)
    {
        var (arr, level, audio) = Performance(offsetMs);
        var cqt = NewCqt();
        var chart = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal);
        chart.Load(arr, level, Beats());
        var confirmed = new Dictionary<double, double>();
        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            var nowMs = (off + n) / (double)Sr * 1000.0;
            var block = audio.AsSpan(off, n);
            cqt.Push(block, off);
            cqt.SetAnchor(off, off / (double)Sr * 1000.0, 1.0);
            foreach (var sound in chart.Expected)
            {
                if (confirmed.ContainsKey(sound.TimeMs) || sound.TimeMs + LiveCqtConfig.EarlyCreditAfterMs > nowMs)
                    continue;
                if (cqt.VerifyEarly(sound).Confirmed) confirmed[sound.TimeMs] = nowMs;
            }
        }

        return confirmed;
    }

    /// <summary>Plays <see cref="Run" /> with every attack <paramref name="offsetMs" /> off the
    /// chart and returns its hits. <paramref name="provisionalOnsets" /> pumps the analyzer's
    /// provisional onsets too, FIRST, as <c>LivePlayViewModel.TickCore</c> does (#643).</summary>
    private static Played Play(double offsetMs, bool provisionalOnsets = false)
    {
        var (arr, level, audio) = Performance(offsetMs);
        var cqt = NewCqt();
        var analyzer = new GuitarInputAnalyzer(Sr);
        var session = new LivePlaySessionService();
        session.Start(arr, level, Beats(), new LivePlaySessionOptions
        {
            ChartVerify = cqt.Verify,
            ChartVerifyEarly = cqt.VerifyEarly
        });
        session.SyncTo(0);

        var hits = new Dictionary<ExpectedSound, Credited>();
        var reportLatencies = new List<double>();
        var also = new List<LivePlayEvent>();
        var graded = new List<LivePlayGrade>();
        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            var nowMs = (off + n) / (double)Sr * 1000.0;               // what this tick has heard
            var block = audio.AsSpan(off, n);
            var events = analyzer.Push(block, (ulong)off);
            cqt.Push(block, off);
            cqt.SetAnchor(off, off / (double)Sr * 1000.0, 1.0);

            void Shown(LivePlayEvent ev)
            {
                if (ev.Verdict != LivePlayVerdict.Miss) Assert.True(hits.TryAdd(ev.Sound, new Credited(ev, nowMs)));
            }

            void Cued(LivePlayGrade cue)
            {
                Assert.Equal(LivePlayGradeStage.Provisional, cue.Stage);
                var credited = hits[cue.Sound];                            // a cue only for a hit shown
                Assert.Null(credited.Cue);                                 // ONE cue per hit
                Assert.Null(credited.Grade);                               // and never after its grade
                credited.Cue = cue;
                credited.CuedAtMs = nowMs;
            }

            foreach (var notice in analyzer.OnsetNotices)
            {
                if (!provisionalOnsets) break;
                session.OfferOnsetNotice(notice, notice.StreamSamplePosition / (double)Sr * 1000.0, graded);
                foreach (var cue in graded) Cued(cue);
            }

            // Speed 1, no latency: an event's transport time is its stream position.
            foreach (var ev in events)
            {
                var timeMs = ev.StreamSamplePosition / (double)Sr * 1000.0;
                reportLatencies.Add(nowMs - timeMs);
                if (session.Offer(ev.Pitches, ev.Chroma, timeMs, ev.Techniques, ev.SpectralCentroidHz, also, graded,
                        provisionalOnsets ? ev.DetectorSamplePosition : null) is { } hit)
                {
                    Shown(hit);
                }

                foreach (var extra in also) Shown(extra);
                foreach (var grade in graded)
                {
                    var credited = hits[grade.Sound];                      // graded only AFTER it was credited
                    Assert.Null(credited.Grade);
                    // A cued hit is confirmed or corrected; one nothing cued is graded as before #643.
                    if (credited.Cue is null) Assert.Equal(LivePlayGradeStage.Final, grade.Stage);
                    else Assert.Contains(grade.Stage, (LivePlayGradeStage[])[LivePlayGradeStage.Confirmed, LivePlayGradeStage.Corrected]);
                    credited.Grade = grade;
                    credited.GradedAtMs = nowMs;
                }
            }

            var tick = session.HandleTick(nowMs, isPlaying: true);
            foreach (var ev in tick.Events) Shown(ev);
            foreach (var cue in tick.Grades) Cued(cue);
        }

        var result = session.Stop(transportSpeed: 1.0);
        reportLatencies.Sort();
        return new Played([.. hits.Values.OrderBy(h => h.Credit.Sound.TimeMs)], result,
            reportLatencies.Count == 0 ? double.NaN : reportLatencies[reportLatencies.Count / 2]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(60.0)]
    [InlineData(-60.0)]
    public void ARealPerformance_IsTimedFromItsAttacks_InCqtMode(double offsetMs)
    {
        var played = Play(offsetMs);
        var timed = played.Hits.Where(h => h.Tier != LiveTimingTier.None).ToList();
        var deltas = timed.Select(h => h.AttackAtMs - h.Credit.Sound.TimeMs).Order().ToArray();
        var median = deltas.Length == 0 ? double.NaN : deltas[deltas.Length / 2];

        _out.WriteLine($"offset {offsetMs:+0;-0;0} ms: {played.Hits.Count}/{Run.Length} hits, {timed.Count} timed, " +
                       $"deltas [{string.Join(", ", deltas.Select(d => d.ToString("0")))}], median {median:0}");

        Assert.Equal(LiveDetectorMode.Cqt, played.Result.Detector);
        Assert.True(played.Hits.Count >= 6, $"only {played.Hits.Count}/{Run.Length} hits — detection broke");
        Assert.True(timed.Count >= played.Hits.Count - 1,
            $"only {timed.Count} of {played.Hits.Count} hits were timed — the onsets are not reaching the CQT hits");
        Assert.InRange(median, offsetMs - 20, offsetMs + 20);
        Assert.Equal(timed.Count, played.Result.PerfectCount + played.Result.GreatCount + played.Result.GoodCount);
        Assert.Equal(played.Hits.Count, played.Result.Hits);
        if (offsetMs != 0)
        {
            var expected = offsetMs > 0 ? LiveTimingDirection.Late : LiveTimingDirection.Early;
            Assert.True(timed.Count(h => h.Direction == expected) >= timed.Count - 2,
                $"directions: {string.Join(", ", timed.Select(h => h.Direction))}");
        }
    }

    /// <summary>
    ///     The owner's rule for #329: <b>timing must not make hit feedback later.</b> Every hit is
    ///     credited — shown, streak, combo — no later than the tick the CQT itself confirms it
    ///     (<see cref="CqtConfirmations" />, which is when every CQT hit was shown before #329),
    ///     and the grade follows when the onset is reported. Holding the credit for the onset
    ///     showed each hit ~200 ms after the attack instead of ~110 ms.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(60.0)]
    [InlineData(-60.0)]
    public void AHit_IsShownWhenTheCqtConfirmsIt_NotWhenItsOnsetIsReported(double offsetMs)
    {
        var played = Play(offsetMs);
        var confirmedAt = CqtConfirmations(offsetMs);
        var graded = played.Hits.Where(h => h.Grade != null).ToList();

        static double Median(IEnumerable<double> xs)
        {
            var a = xs.Order().ToArray();
            return a.Length == 0 ? double.NaN : a[a.Length / 2];
        }

        var creditLatency = Median(played.Hits.Select(h => h.ShownAtMs - (h.Credit.Sound.TimeMs + offsetMs)));
        var gradeLatency = Median(graded.Select(h => h.GradedAtMs - h.AttackAtMs));
        _out.WriteLine($"offset {offsetMs:+0;-0;0} ms: shown {creditLatency:0} ms after the attack (median), " +
                       $"graded {gradeLatency:0} ms after it ({graded.Count} graded later, " +
                       $"{played.Hits.Count - graded.Count} timed at credit); onsets reported " +
                       $"{played.MedianReportLatencyMs:0} ms after the attack");

        Assert.True(played.Hits.Count >= 6, $"only {played.Hits.Count}/{Run.Length} hits — detection broke");
        foreach (var hit in played.Hits)
        {
            Assert.True(confirmedAt.TryGetValue(hit.Credit.Sound.TimeMs, out var confirmed),
                $"the CQT never confirmed the note at {hit.Credit.Sound.TimeMs} ms, yet it was credited");
            Assert.True(hit.ShownAtMs <= confirmed,
                $"the note at {hit.Credit.Sound.TimeMs} ms was shown at {hit.ShownAtMs:0} ms — later than the " +
                $"{confirmed:0} ms the CQT confirmed it at: the credit waited for the onset");
        }

        foreach (var hit in graded) Assert.True(hit.ShownAtMs < hit.GradedAtMs);
        Assert.True(graded.Count >= played.Hits.Count / 2,
            $"only {graded.Count} hits were graded after their credit — this run no longer exercises the late grade");
        Assert.True(creditLatency < played.MedianReportLatencyMs,
            $"hits show {creditLatency:0} ms after the attack, onsets are reported {played.MedianReportLatencyMs:0} ms " +
            "after it: hit feedback is waiting for the onset");
    }

    /// <summary>
    ///     #643: with the analyzer's PROVISIONAL onsets pumped, a CQT hit's tier and EARLY/LATE cue
    ///     land with the "Hit" — at the CQT's confirmation — instead of when the finalized onset is
    ///     reported ~100 ms later; that finalized onset then confirms or corrects the cue. And not
    ///     one stat moves: the score, the hit count and the tier counters are the run's without
    ///     them, hit for hit.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(60.0)]
    [InlineData(-60.0)]
    public void ProvisionalOnsets_PutTheTimingCueOnTheHit_AndMoveNoStat(double offsetMs)
    {
        var without = Play(offsetMs);
        var with = Play(offsetMs, provisionalOnsets: true);

        static double Median(IEnumerable<double> xs)
        {
            var a = xs.Order().ToArray();
            return a.Length == 0 ? double.NaN : a[a.Length / 2];
        }

        var graded = with.Hits.Where(h => h.Grade != null).ToList();
        var cued = graded.Where(h => h.Cue != null).ToList();
        var corrected = cued.Count(h => h.Grade!.Value.Stage == LivePlayGradeStage.Corrected);
        var cueLatency = Median(cued.Select(h => h.CuedAtMs - (h.Credit.Sound.TimeMs + offsetMs)));
        var gradeLatency = Median(graded.Select(h => h.GradedAtMs - (h.Credit.Sound.TimeMs + offsetMs)));
        _out.WriteLine($"offset {offsetMs:+0;-0;0} ms: {cued.Count}/{graded.Count} late-graded hits cued early " +
                       $"({corrected} corrected); cue {cueLatency:0} ms after the attack, finalized grade " +
                       $"{gradeLatency:0} ms; provisional - finalized attack " +
                       $"[{string.Join(", ", cued.Select(h => (h.Cue!.Value.AttackAtMs - h.Grade!.Value.AttackAtMs).ToString("0")))}] ms");

        // No stat moves.
        Assert.Equal(without.Result.Score, with.Result.Score);
        Assert.Equal(without.Result.Hits, with.Result.Hits);
        Assert.Equal(without.Result.Misses, with.Result.Misses);
        Assert.Equal(without.Result.PerfectCount, with.Result.PerfectCount);
        Assert.Equal(without.Result.GreatCount, with.Result.GreatCount);
        Assert.Equal(without.Result.GoodCount, with.Result.GoodCount);
        Assert.Equal(without.Hits.Select(h => (h.Credit.Sound.TimeMs, h.Tier)), with.Hits.Select(h => (h.Credit.Sound.TimeMs, h.Tier)));

        // The cue lands with the hit, long before the finalized grade.
        Assert.True(graded.Count >= with.Hits.Count / 2, $"only {graded.Count} hits were graded after their credit");
        // Not every one: the analyzer publishes a provisional onset only where the audio heard so
        // far already shows a fresh attack, and these notes ring on over each other (5–7 of 8 here).
        Assert.True(cued.Count >= graded.Count / 2,
            $"only {cued.Count} of {graded.Count} late-graded hits got a provisional cue — the provisional onsets are not reaching them");
        foreach (var hit in cued)
        {
            Assert.True(hit.CuedAtMs <= hit.ShownAtMs + 1000.0 * TickSamples / Sr,
                $"the note at {hit.Credit.Sound.TimeMs} ms was cued at {hit.CuedAtMs:0} ms, shown at {hit.ShownAtMs:0} ms");
            Assert.True(hit.CuedAtMs < hit.GradedAtMs);
        }

        // The CQT's confirmation is now what the cue waits for, not the onset's report (when the
        // player is early the two are close: the CQT confirms from the CHART time).
        Assert.True(cueLatency < gradeLatency,
            $"the cue shows {cueLatency:0} ms after the attack, the finalized grade {gradeLatency:0} ms — no earlier");
        // A provisional onset usually lands ON the refined attack: most cues stand.
        Assert.True(corrected <= cued.Count / 3, $"{corrected} of {cued.Count} cues were corrected");
    }
}
