using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>Outgoing counts are compared with the frozen reference session source at every pass-reset boundary.</summary>
public sealed class PassBoundaryReviewTests
{
    public static TheoryData<string, int> Boundaries
    {
        get
        {
            var cases = new TheoryData<string, int>();
            foreach (var boundary in new[] { "stop", "seek", "sync", "resume", "section", "clear-loop", "ladder-reset", "loop", "speed-step", "phrase", "song-end" })
                foreach (var offset in new[] { 0, 100, -100 }) cases.Add(boundary, offset);
            return cases;
        }
    }

    [Fact]
    public void LoopWrap_WaitsForMatureSilentCaptureBeforeCountingAndResetting() => Compare("loop", 100);

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void BoundaryMatrix_PreservesMainTotalsAndCountsEachEligibleNoteOnce(string boundary, int offset)
        => Compare(boundary, offset);

    /// <summary>#629: a loop ending at 2000 ms with an unplayed note at 1960 ms. The note's late
    /// window is still open at the wrap; main's pass total left it out (0 judged), the wrap now
    /// closes it first, so the pass counts one miss.</summary>
    [Fact]
    public void OpenLoopTailWindow_IsClosedAtTheWrap_AsOneMiss()
    {
        var main = new Probe(true, "loop", tail: true);
        var branch = new Probe(false, "loop", tail: true);
        Assert.Equal(0, main.Run("loop", 0).Judged);
        var pass = branch.Run("loop", 0);
        Assert.Equal(1, pass.Judged);
        Assert.Equal(0, pass.Accuracy);
    }

    private static void Compare(string boundary, int offset)
    {
        var expected = new Probe(true, boundary).Run(boundary, offset);
        var actual = new Probe(false, boundary).Run(boundary, offset);
        Assert.Equal(2, expected.Judged);
        Assert.Equal(expected, actual);
    }

    private readonly record struct Totals(int Judged, double Accuracy, double Speed);
    private static int[] NoteTimes(bool tail) => tail ? [1960] : [1000, 1600];

    private sealed class Probe
    {
        private readonly object _session;
        private readonly Type _type;
        private readonly bool _main;
        private readonly GuitarInputAnalyzer _analyzer = new(44_100);
        private readonly float[] _silence = new float[441];
        private readonly SpeedLadder _ladder = new() { StartSpeed = 0.6 };
        private ulong _sample;

        public Probe(bool main, string boundary, bool tail = false)
        {
            _main = main;
            _type = main ? MainSessionSnapshot.SessionType : typeof(LivePlaySessionService);
            _session = Activator.CreateInstance(_type)!;
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            foreach (var ms in NoteTimes(tail))
                level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 3, Position = new FractionalPosition(ms / 500, new Fraction(ms % 500, 500)) });
            arrangement.Levels.Add(level);
            var upper = new ChartLevel { DifficultyIndex = 1 };
            foreach (var note in level.Notes)
                upper.Notes.Add(new GuitarNote { StringIndex = note.StringIndex, Fret = note.Fret, Position = note.Position });
            arrangement.Levels.Add(upper);
            arrangement.Phrases.Add("first");
            arrangement.Phrases.Add("next");
            arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
            arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(4), 1));
            var options = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlaySessionOptions")!)!;
            options.GetType().GetProperty("CountInBeats")!.SetValue(options, 0);
            options.GetType().GetProperty("DynamicDifficulty")!.SetValue(options, boundary is "ladder-reset" or "phrase");
            if (boundary == "phrase")
                options.GetType().GetProperty("InitialPhraseLevels")!.SetValue(options, new Dictionary<string, int> { ["first"] = 1, ["next"] = 1 });
            if (boundary == "speed-step") options.GetType().GetProperty("Ladder")!.SetValue(options, _ladder);
            var beats = Enumerable.Range(0, 12).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();
            Call("Start", arrangement, level, beats, options);
            Call("SyncTo", 0d);
            if (boundary is "loop" or "speed-step" or "clear-loop") Call("SetLoop", 0d, boundary == "clear-loop" ? 3000d : 2000d);
        }

        public Totals Run(string boundary, int offset)
        {
            object? pass = null;
            var endMs = boundary == "phrase" ? 2700 : boundary is "loop" or "speed-step" ? 2400 : 1990;
            for (var time = 0; time <= endMs; time += 10)
            {
                _analyzer.Push(_silence, _sample);
                _sample += (ulong)_silence.Length;
                if (boundary == "speed-step" && time == 1000)
                {
                    if (_main) MainSessionSnapshot.Hit(_session, 1000);
                    else ((LivePlaySessionService)_session).Offer([new LivePitch(43, 0f, 1f)], [], 1000, LiveTechniqueFlags.None, 0);
                }
                var result = Tick(time, offset);
                if (Get<bool>(result, "PassCompleted"))
                {
                    pass = result;
                    break;
                }
            }
            switch (boundary)
            {
                case "stop": break;
                case "seek": Tick(3000, offset, seek: true); break;
                case "sync": Call("SyncTo", 2500d); break;
                case "resume": Call("BeginResumeRewind", 1990d, 100d); break;
                case "section": Call("SetLoop", 2000d, 3000d); break;
                case "clear-loop": Call("ClearLoop"); break;
                case "ladder-reset": Assert.True((bool)Call("StartLadderOverAtBottom", 1990d)!); break;
                case "song-end":
                    if (_main) Tick(2251, offset);
                    else ((LivePlaySessionService)_session).CompleteAt(2000);
                    break;
            }
            if (boundary == "phrase")
            {
                var difficulty = (DynamicDifficultyEngine)_type.GetProperty("Difficulty")!.GetValue(_session)!;
                var spans = (IReadOnlyList<LivePhraseSpan>)_type.GetProperty("PhraseSpans")!.GetValue(_session)!;
                Assert.Equal(0, difficulty.LevelFor(spans[0]));
            }
            if (boundary is "loop" or "speed-step")
            {
                Assert.NotNull(pass);
                Assert.Equal(1, (int)_type.GetProperty("PassCount")!.GetValue(_session)!);
                return new Totals(Get<int>(pass, "PassJudged"), Get<double>(pass, "PassAccuracy"), _ladder.CurrentSpeed);
            }
            var stopped = Call("Stop", 1d)!;
            return new Totals(Get<int>(stopped, "Hits") + Get<int>(stopped, "Misses"), Get<double>(stopped, "Accuracy"), _ladder.CurrentSpeed);
        }

        private object Tick(double time, int offset, bool seek = false)
        {
            var result = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlayTickResult")!)!;
            return _main || _type.GetMethods().Any(m => m.Name == "HandleTick" && m.GetParameters().Length == 4)
                ? Call("HandleTick", time, true, result, seek)!
                : Call("HandleTick", time, true, result, seek, (double?)(time - offset - _analyzer.LatencySamples * 1000d / 44_100))!;
        }

        private object? Call(string name, params object?[] args)
            => _type.GetMethods().Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(_session, args);
        private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    }
}
