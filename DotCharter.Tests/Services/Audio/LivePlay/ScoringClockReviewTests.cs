using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>Compares scoring order/content to the frozen reference session, not only the final hit count.</summary>
public sealed class ScoringClockReviewTests
{
    public static TheoryData<int> Seeds
    {
        get
        {
            var seeds = new TheoryData<int>();
            for (var i = 0; i < 200; i++) seeds.Add(0x5eed + i);
            return seeds;
        }
    }

    [Fact]
    public void ZeroOffset_MixedJudgmentsKeepMainCurrentAndBestStreak()
    {
        var chart = Chart(minimal: true, seed: 0);
        var main = new Probe(true, chart.Arrangement);
        var branch = new Probe(false, chart.Arrangement);
        Drive(main, chart.Attacks, 1390);
        Drive(branch, chart.Attacks, 1390);
        Assert.Equal(1, main.Stats.Streak);
        Assert.Equal(1, main.Stats.BestStreak);
        Assert.Equal(main.Stats.Streak, branch.Stats.Streak);
        Assert.Equal(main.Stats.BestStreak, branch.Stats.BestStreak);
        Assert.Equal(main.Trace, branch.Trace);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ZeroOffset_SeededScoringOrderMatchesMain(int seed)
    {
        var chart = Chart(minimal: false, seed);
        var main = new Probe(true, chart.Arrangement);
        var branch = new Probe(false, chart.Arrangement);
        Drive(main, chart.Attacks, seed % 7 == 0 ? 2600 : 3400, seed);
        Drive(branch, chart.Attacks, seed % 7 == 0 ? 2600 : 3400, seed);
        Assert.Equal(main.Trace, branch.Trace);
    }

    internal sealed record Attack(int At, int[] Pitches);

    internal static (Arrangement Arrangement, List<Attack> Attacks) Chart(bool minimal, int seed)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        AddNote(level, 700, 5);
        AddNote(level, 1000, 7); // intentionally unplayed, between two distinct hits
        AddNote(level, 1100, 9);
        var attacks = new List<Attack> { new(700, [45]), new(1100, [49]) };
        if (!minimal)
        {
            arrangement.ChordTemplates.Add(new ChordTemplate { Id = 7, Name = "G5", Frets = new Dictionary<int, int> { [0] = 3, [1] = 5 } });
            level.Chords.Add(new Chord { Position = Position(1400), TemplateId = 7 });
            attacks.Add(new Attack(1400, [43, 50]));
            AddNote(level, 1800, 2);
            attacks.Add(new Attack(1800, [11])); // wrong pitch
            var random = new Random(seed);
            for (var at = 2000; at <= 3000; at += 200)
            {
                var fret = random.Next(1, 13);
                AddNote(level, at, fret);
                var kind = random.Next(3);
                if (kind != 0) attacks.Add(new Attack(at + random.Next(-5, 21) * 10, kind == 1 ? [40 + fret] : [11]));
            }
        }
        arrangement.Levels.Add(level);
        return (arrangement, attacks);
    }

    private static FractionalPosition Position(int ms) => new(ms / 500, new Fraction(ms % 500, 500));
    private static void AddNote(ChartLevel level, int ms, int fret)
        => level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = fret, Position = Position(ms) });

    private static void Drive(Probe probe, List<Attack> attacks, int endMs, int? seed = null)
    {
        var delivered = new HashSet<int>();
        var shift = 0;
        // The loop ends where no note's late window is still open (the last loop note is 1400 ms,
        // its window closes at 1650 ms): a window open at the wrap is closed there now (#629,
        // PassBoundaryReviewTests), which main never did, so a straddling note is not a
        // comparison of the scoring clock.
        if (seed is { } start && start % 2 == 0) probe.Call("SetLoop", 0d, 1700d);
        for (var wall = 0; wall <= endMs; wall += 10)
        {
            if (wall == 1900 && seed is { } clear && clear % 4 == 0) probe.Call("ClearLoop");
            var seek = wall == 2100 && seed is { } jump && jump % 5 == 0;
            if (seek)
            {
                shift = wall - 500;
                delivered.Clear();
            }
            var playing = !(seed is { } pause && pause % 3 == 0 && wall is >= 1500 and < 1660);
            var time = wall - shift;
            for (var i = 0; i < attacks.Count; i++)
            {
                var attack = attacks[i];
                if (attack.At + 190 > time || !delivered.Add(i)) continue;
                if (playing) probe.Attack(attack);
            }
            var target = probe.Tick(time, playing, seek);
            if (target is { } loopStart)
            {
                shift = wall + 10 - (int)loopStart;
                delivered.Clear();
            }
        }
        if (seed != null) probe.Stop();
    }

    private sealed class Probe
    {
        private readonly object _session;
        private readonly Type _type;
        private readonly bool _main;
        private readonly SpeedLadder _ladder = new() { StartSpeed = 0.6 };
        public List<string> Trace { get; } = [];
        public LivePlayStats Stats => (LivePlayStats)_type.GetProperty("Stats")!.GetValue(_session)!;

        public Probe(bool main, Arrangement chart)
        {
            _main = main;
            _type = main ? MainSessionSnapshot.SessionType : typeof(LivePlaySessionService);
            _session = Activator.CreateInstance(_type)!;
            var options = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlaySessionOptions")!)!;
            options.GetType().GetProperty("CountInBeats")!.SetValue(options, 0);
            options.GetType().GetProperty("Ladder")!.SetValue(options, _ladder);
            Call("Start", chart, chart.Levels[0], Enumerable.Range(0, 14).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList(), options);
            Call("SyncTo", 0d);
        }

        public void Attack(Attack attack)
        {
            var extra = new List<LivePlayEvent>();
            var first = _main ? MainSessionSnapshot.Attack(_session, attack.At, attack.Pitches, extra)
                : ((LivePlaySessionService)_session).Offer(attack.Pitches.Select(p => new LivePitch(p, 0, 1)).ToArray(), [], attack.At, LiveTechniqueFlags.None, 0, extra);
            if (first is { } hit) Record(hit);
            foreach (var additional in extra) Record(additional);
            Snapshot("attack");
        }

        public double? Tick(double time, bool playing, bool seek)
        {
            var result = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlayTickResult")!)!;
            if (_main || _type.GetMethods().Any(m => m.Name == "HandleTick" && m.GetParameters().Length == 4))
                Call("HandleTick", time, playing, result, seek);
            else Call("HandleTick", time, playing, result, seek, (double?)(time - 190));
            foreach (var ev in (IEnumerable<LivePlayEvent>)result.GetType().GetProperty("Events")!.GetValue(result)!) Record(ev);
            Snapshot($"tick:{time:R}:{playing}:{seek}:{Value(result, "PassCompleted")}:{Value(result, "PassJudged")}:{Value(result, "PassAccuracy")}:{Value(result, "SpeedChange")}");
            return (double?)result.GetType().GetProperty("SeekToMs")!.GetValue(result);
        }

        public void Stop()
        {
            var result = Call("Stop", 1d)!;
            Trace.Add($"stop:{Value(result, "Hits")}:{Value(result, "Misses")}:{Value(result, "BestStreak")}:{Value(result, "Score")}");
        }

        public object? Call(string name, params object?[] args)
            => _type.GetMethods().Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(_session, args);
        private static object? Value(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
        private void Record(LivePlayEvent ev)
            => Trace.Add($"event:{ev.Verdict}:{ev.Sound.TimeMs:R}:{ev.Sound.Label}:{ev.DetectedAtMs:R}:{ev.Confidence:R}:{ev.Tier}:{ev.Direction}");
        private void Snapshot(string action)
            => Trace.Add($"{action}|{Stats.Hits}:{Stats.Misses}:{Stats.Streak}:{Stats.BestStreak}:{Stats.Score}:{Value(_session, "PassCount")}:{_ladder.CurrentSpeed:R}");
    }
}
