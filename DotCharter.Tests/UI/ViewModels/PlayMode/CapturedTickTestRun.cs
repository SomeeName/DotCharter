using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tests.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

internal readonly record struct CapturedFrame(double Position, bool Playing, long Seek, int Epoch, int At);
internal readonly record struct RequestedTransport(double Position, bool Playing, bool Active, long Seek, int Epoch, double Armed);

/// <summary>Real worker, clock and fake-audio gates; the reference never reads a refreshed judging clock.</summary>
internal sealed class CapturedTickTestRun : IDisposable
{
    private readonly PlayModeCompletionMatrixTests.CaptureClock _clock = new();
    private readonly List<string> _trace = [];
    private readonly bool _loop;
    private int _at;
    private int? _completedAt;
    public PlayModeReviewHarness H { get; } = new(finalNote: true, noteMs: 500);
    public LivePlaySessionService Session { get; }
    public CapturedTickReference Reference { get; }

    public CapturedTickTestRun(bool loop, bool noInput = false, Arrangement? chart = null)
    {
        _loop = loop;
        if (chart != null)
        {
            H.State.ActiveProject!.Arrangements.Clear();
            H.State.ActiveProject.Arrangements.Add(chart);
            H.State.ActiveArrangement = chart;
        }
        for (var i = 4; i < 10; i++) H.State.ActiveProject!.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        H.State.TotalAudioLengthMs = 4000;
        H.Config.AppConfig.GameplayResumeRewindMs = 0;
        H.Config.AppConfig.LivePlayCountInBeats = 0;
        H.Audio.Clock = _clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(H.Live, "_completion"), _clock);
        H.Begin();
        Session = PlayModeReviewHarness.Get<LivePlaySessionService>(H.Live, "_session");
        if (loop) Session.SetLoop(0, 2000);
        Reference = new CapturedTickReference(H, loop);
        if (noInput) H.Audio.InputSampleRate = 0;
        Tick(0);
    }

    private RequestedTransport Requested() => new(H.Audio.CurrentTimeMs, H.Audio.IsPlaying, H.Live.IsActive,
        H.Audio.SeekGeneration, PlayModeReviewHarness.Get<int>(H.Live, "_uiEpoch"),
        PlayModeReviewHarness.Get<double>(H.Live, "_songEndMs"));

    private CapturedFrame Capture(double time)
    {
        _clock.Advance(10);
        _at += 10;
        H.Audio.PublishPosition(time);
        var state = Requested();
        Reference.SetCurrent(state, _at);
        return new CapturedFrame(time, state.Playing, state.Seek, state.Epoch, _at);
    }

    public void Hit(int time) => Offer(time, [45]);

    public void Offer(int time, int[] pitches)
    {
        var extra = new List<LivePlayEvent>();
        if (Session.Offer(pitches.Select(p => new LivePitch(p, 0, 1)).ToArray(), [], time, LiveTechniqueFlags.None, 0, extra) is { } hit)
            _trace.Add(CapturedTickReference.Event(_at, hit));
        foreach (var ev in extra) _trace.Add(CapturedTickReference.Event(_at, ev));
        Reference.Hit(time, pitches, _at);
    }

    public void Tick(double time, bool check = true)
    {
        if (!H.Live.IsActive) return;
        var frame = Capture(time);
        var prepared = Reference.Prepare(frame);
        PlayModeReviewHarness.Invoke(H.Live, "WorkerTick");
        Collect(prepared);
        Reference.Commit(frame);
        if (!_loop && H.Live.IsActive) H.EndWatch();
        ObserveCompletion();
        if (check) Check();
    }

    public async Task Interleave(double time, string phase, string action, bool check = true)
    {
        var frame = Capture(time);
        var prepared = false;
        if (phase == "action") prepared = Reference.Prepare(frame);
        if (phase == "action" && !_loop)
        {
            PlayModeReviewHarness.Invoke(H.Live, "WorkerTick");
            Collect(prepared);
        }
        await Gate(phase, frame, () =>
        {
            Apply(action);
            if (phase == "snapshot") prepared = Reference.Prepare(frame);
        });
        if (phase == "snapshot" || _loop) Collect(prepared);
        Reference.Commit(frame);
        ObserveCompletion();
        if (check) Check();
    }

    private async Task Gate(string phase, CapturedFrame frame, Action action)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        void Interrupt()
        {
            H.Audio.BeforeCaptureRead = null;
            H.Audio.BeforeSeekGenerationRead = null;
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            action();
        }
        if (phase == "snapshot")
        {
            H.Audio.BeforeCaptureRead = Interrupt;
        }
        else if (!_loop)
        {
            H.Audio.BeforeSeekGenerationRead = Interrupt;
        }
        else
        {
            H.Audio.BeforeSeekGenerationRead = () =>
            {
                if (PlayModeReviewHarness.Get<double>(Session, "_lastPlayheadMs") == frame.Position &&
                    PlayModeReviewHarness.Get<LivePlayTickResult>(H.Live, "_tickResult").SeekToMs != null)
                {
                    Interrupt();
                }
            };
        }
        var worker = Task.Run(() =>
        {
            try
            {
                if (phase == "action" && !_loop) H.EndWatch();
                else PlayModeReviewHarness.Invoke(H.Live, "WorkerTick");
            }
            finally { finished.Set(); }
        }, TestContext.Current.CancellationToken);
        try
        {
            WaitHandle.WaitAny([entered.WaitHandle, finished.WaitHandle]);
            if (!entered.IsSet)
            {
                await worker;
                Assert.Fail($"{phase} gate was not reached at {frame.Position}");
            }
        }
        finally { release.Set(); }
        await worker;
    }

    public async Task PauseDuringCapture(double time)
    {
        var frame = Capture(time);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Task? worker = null;
        H.Audio.BeforeCaptureRead = () =>
        {
            H.Audio.BeforeCaptureRead = null;
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
        };
        H.Audio.BeforePause = () =>
        {
            worker = Task.Run(() =>
            {
                try { PlayModeReviewHarness.Invoke(H.Live, "WorkerTick"); }
                finally { finished.Set(); }
            }, TestContext.Current.CancellationToken);
            WaitHandle.WaitAny([entered.WaitHandle, finished.WaitHandle]);
            Assert.True(entered.IsSet);
        };
        H.Audio.AfterPause = () =>
        {
            Reference.SetCurrent(Requested(), _at);
            Reference.Prepare(frame);
            release.Set();
        };
        try { H.Vm.PauseGameplay(); }
        finally { release.Set(); }
        await worker!;
        H.Audio.BeforePause = null;
        H.Audio.AfterPause = null;
        Collect(prepared: true);
    }

    private void Apply(string action)
    {
        switch (action)
        {
            case "pause": H.Vm.PauseGameplay(); break;
            case "resume": H.Vm.ResumeGameplayCommand.Execute(null); break;
            case "seek": H.Audio.SeekTo(2500); break;
            case "stop": H.Live.IsActive = false; H.Transport.StopCommand.Execute(null); break;
            case "run-end": H.Vm.PauseGameplay(); H.Vm.EndRunFromPauseCommand.Execute(null); break;
        }
        Reference.SetCurrent(Requested(), _at);
        ObserveCompletion();
    }

    public void Resume()
    {
        if (H.Live.IsActive) Apply("resume");
    }

    public void End()
    {
        if (H.Live.IsActive)
        {
            _clock.Advance(10);
            _at += 10;
            Apply("run-end");
        }
        Check();
    }

    private void Collect(bool prepared)
    {
        if (!prepared) return;
        foreach (var ev in PlayModeReviewHarness.Get<LivePlayTickResult>(H.Live, "_tickResult").Events)
            _trace.Add(CapturedTickReference.Event(_at, ev));
    }

    private void ObserveCompletion()
    {
        if (!H.Live.IsActive) _completedAt ??= _at;
    }

    public void Check()
    {
        Assert.Equal(Reference.Trace, _trace);
        Assert.Equal(Reference.Active, H.Live.IsActive);
        Assert.Equal(Reference.Current.Playing, H.Audio.IsPlaying);
        if (H.Live.IsActive) Assert.Equal(Reference.Current.Position, H.Audio.CurrentTimeMs);
        Assert.Equal(Reference.PassCount, Session.PassCount);
        Assert.Equal(Reference.CompletedAt, _completedAt);
        if (Reference.Saved is { } saved)
        {
            var rows = H.Store.GetAll().Values.SelectMany(p => p.Sessions).ToArray();
            if (saved.Hits + saved.Misses == 0)
            {
                Assert.Empty(rows);
            }
            else
            {
                var row = Assert.Single(rows);
                Assert.Equal(saved.Hits, row.Hits);
                Assert.Equal(saved.Misses, row.Misses);
            }
        }
    }

    public void Dispose() => H.Dispose();
}

/// <summary>
///     Frozen reference judging plus the wrap rule it lacks (late windows open at B close at the
///     wrap), with a reserved loop action committed only after user input.
/// </summary>
internal sealed class CapturedTickReference
{
    private readonly Type _type = MainSessionSnapshot.SessionType;
    private readonly object _session;
    private readonly bool _loop;
    private long _lastSeek;
    private bool _reserved;
    public RequestedTransport Current { get; private set; }
    public List<string> Trace { get; } = [];
    public int? CompletedAt { get; private set; }
    public (int Hits, int Misses)? Saved { get; private set; }
    public bool Active => (bool)_type.GetProperty("IsActive")!.GetValue(_session)!;
    public int PassCount => (int)_type.GetProperty("PassCount")!.GetValue(_session)!;

    public CapturedTickReference(PlayModeReviewHarness h, bool loop)
    {
        _loop = loop;
        _session = Activator.CreateInstance(_type)!;
        var options = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlaySessionOptions")!)!;
        options.GetType().GetProperty("CountInBeats")!.SetValue(options, 0);
        Call("Start", h.State.ActiveArrangement!, h.State.ActiveArrangement!.Levels[0], h.State.ActiveProject!.Beats, options);
        Call("SyncTo", 0d);
        if (loop) Call("SetLoop", 0d, 2000d);
        _lastSeek = PlayModeReviewHarness.Get<long>(h.Live, "_lastSeekGeneration");
    }

    public void SetCurrent(RequestedTransport state, int at)
    {
        if (Active && !state.Active) Stop(at);
        Current = state;
    }

    public void Hit(int time, int[] pitches, int at)
    {
        var extra = new List<LivePlayEvent>();
        if (MainSessionSnapshot.Attack(_session, time, pitches, extra) is { } ev) Trace.Add(Event(at, ev));
        foreach (var other in extra) Trace.Add(Event(at, other));
    }

    public bool Prepare(CapturedFrame frame)
    {
        _reserved = false;
        if (!Active) return false;
        var seek = frame.Seek != _lastSeek;
        _lastSeek = frame.Seek;
        var pending = _type.GetField("_pendingSeekTargetMs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_session);
        _reserved = _loop && frame.Playing && frame.Position >= 2000 && pending == null;
        // At the boundary main returns before AdvanceTo. Reserve without CompletePass's rewind;
        // rejected completion therefore keeps all already judged sounds and arms no pending seek.
        var tick = Tick(frame.Position, frame.Playing && !_reserved, seek);
        foreach (var ev in (IEnumerable<LivePlayEvent>)Value(tick, "Events")!) Trace.Add(Event(frame.At, ev));
        return true;
    }

    public void Commit(CapturedFrame frame)
    {
        if (!Active || !Current.Active || frame.Epoch != Current.Epoch || frame.Seek != Current.Seek) return;
        if (_reserved && (Current.Armed > 0 || Current.Playing))
        {
            ClosePassWindows(frame.At);
            var tick = Tick(frame.Position, playing: true, seek: false);
            Assert.True((bool)Value(tick, "PassCompleted")!);
            Current = Current with { Position = (double)Value(tick, "SeekToMs")!, Seek = Current.Seek + 1, Playing = true };
        }
        else if (!_loop && frame.Position >= 4000 && Current.Position >= 4000 && Current.Armed > 0)
        {
            var matcher = (LiveNoteMatcher)_type.GetProperty("Matcher")!.GetValue(_session)!;
            foreach (var ev in matcher.AdvanceTo(4000 + matcher.HitWindowLateMs)) Trace.Add(Event(frame.At, ev));
            Stop(frame.At);
            Current = Current with { Active = false, Playing = false, Position = 0 };
        }
    }

    /// <summary>
    ///     The one rule main lacks (#629): a loop note whose late window is still open at B is judged
    ///     at the wrap, as a miss of the pass that ends, before that pass is totalled; main re-armed
    ///     it unjudged. A verdict reached past B belongs to the next pass and is not kept.
    /// </summary>
    private void ClosePassWindows(int at)
    {
        var matcher = (LiveNoteMatcher)_type.GetProperty("Matcher")!.GetValue(_session)!;
        var loop = _type.GetProperty("Loop")!.GetValue(_session)!;
        var (startMs, endMs) = ((double)Value(loop, "StartMs")!, (double)Value(loop, "EndMs")!);
        var open = matcher.Expected.Where(s => s.TimeMs >= startMs && s.TimeMs < endMs && s.Verdict == null).ToArray();
        if (open.Length == 0) return;
        var tally = _type.GetMethod("ApplyTally", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var ev in matcher.AdvanceTo(Math.BitIncrement(open.Max(s => s.TimeMs) + matcher.HitWindowLateMs)))
        {
            if (ev.Sound.TimeMs >= endMs) continue;
            tally.Invoke(_session, [ev]);
            Trace.Add(Event(at, ev));
        }
    }

    private object Tick(double time, bool playing, bool seek)
    {
        var result = Activator.CreateInstance(_type.Assembly.GetType(_type.Namespace + ".LivePlayTickResult")!)!;
        Call("HandleTick", time, playing, result, seek);
        return result;
    }

    private void Stop(int at)
    {
        var result = Call("Stop", 1d)!;
        Saved = ((int)Value(result, "Hits")!, (int)Value(result, "Misses")!);
        CompletedAt ??= at;
    }

    private object? Call(string name, params object?[] args)
        => _type.GetMethods().Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(_session, args);
    private static object? Value(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    public static string Event(int at, LivePlayEvent ev) => $"{at}:{ev.Verdict}:{ev.Sound.TimeMs:R}";
}
