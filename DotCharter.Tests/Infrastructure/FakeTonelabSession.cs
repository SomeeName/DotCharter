using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Audio.Tonelab;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Infrastructure;

/// <summary>An in-memory tonelab session: a state the test sets and commands that answer ok.</summary>
internal sealed class FakeTonelabSession : ITonelabSession
{
    /// <summary>The Tone designer's preview targets over a clone on an empty fake chain and this fake session (disconnected unless told).</summary>
    public static TonePreviewTargets Targets(TonePreviewSession clone, ITonelabSession? session = null) =>
        For(clone, session ?? new FakeTonelabSession(), new FakeConfigurationService(), new ManualTonelabScheduler(),
            Path.Combine(Path.GetTempPath(), "dotcharter-tonelab-tests"));

    /// <summary>The same with every collaborator named: the debounce clock and where tone files go.</summary>
    public static TonePreviewTargets For(TonePreviewSession clone, ITonelabSession session, IConfigurationService config,
        ITonelabScheduler scheduler, string toneFileDirectory)
    {
        var logger = new FakeAppLogger();
        var inGame = new TonelabTonePreview(session, TonePreviewTargets.Validate, new TonelabToneFiles(toneFileDirectory), scheduler, logger);
        return new TonePreviewTargets(clone, FakeLocalization.English(), logger, session, new TestUiDispatcher(), config, inGame);
    }

    public TonelabSessionState State { get; set; }

    public TonelabSessionState? ProbeTo { get; set; }

    public List<string> AppliedFiles { get; } = [];

    public int Restores { get; private set; }

    public int? ProcessId => State == TonelabSessionState.Disconnected ? null : Pid;

    /// <summary>The game process the session names while it is not disconnected.</summary>
    public int Pid { get; set; } = 1;

    /// <summary>Runs after each accepted <c>apply-file</c>: a test's hook for "a preview claims the route mid-render".</summary>
    public Action? Applied { get; set; }

    public TonelabStatus? LastStatus { get; set; }

    public int Reconnects { get; private set; }

    public bool CanLaunch { get; set; }

    public bool GameHoldsAudioDevice { get; set; } = true;

    public Task<TonelabResult> SetFrameCapAsync(bool whileHidden, int framesPerSecond, CancellationToken ct = default) => Ok();

    public int Launches { get; private set; }

    public GameToneLaunchFailure LastLaunchFailure { get; set; }

    public Task<TonelabSessionState> LaunchAsync(CancellationToken ct = default)
    {
        Launches++;
        return ProbeAsync(ct);
    }

    public ExternalInsertStats? AudioStats => null;

    public event EventHandler? StateChanged;

    public void Change(TonelabSessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<TonelabSessionState> ProbeAsync(CancellationToken ct = default)
    {
        if (ProbeTo is { } to) Change(to);
        return Task.FromResult(State);
    }

    public Task<TonelabSessionState> ReconnectAsync(CancellationToken ct = default)
    {
        Reconnects++;
        return ProbeAsync(ct);
    }

    public Task<TonelabResult> DumpToneAsync(string path, CancellationToken ct = default) => Ok();

    public Task<TonelabSessionState> AttachAsync(int processId, CancellationToken ct = default) => ProbeAsync(ct);

    public Task<TonelabResult> ApplyToneFileAsync(string path, CancellationToken ct = default)
    {
        AppliedFiles.Add(path);
        Applied?.Invoke();
        return Ok("tl");
    }

    public Task<TonelabResult> SetKnobAsync(string gearSlot, string knobKey, float value, CancellationToken ct = default) => Ok();

    public Task<TonelabResult> RestoreToneAsync(CancellationToken ct = default)
    {
        Restores++;
        return Ok();
    }

    public Task<TonelabResult> MuteMixAsync(bool on, CancellationToken ct = default) => Ok();

    public HashSet<object> RouteOwners { get; } = new(ReferenceEqualityComparer.Instance);

    public bool AudioRouted => RouteOwners.Count > 0;

    public void RouteAudio(object owner, bool on)
    {
        if (on) RouteOwners.Add(owner);
        else RouteOwners.Remove(owner);
        if (on && Lease is { Ended: false } lease) lease.Interrupted = true;
    }

    public bool IsRoutingAudio => AudioRouted;

    /// <summary>The lease handed out last, for a test to read; null before the first.</summary>
    public FakeLease? Lease { get; private set; }

    public ITonelabAudioLease? TryLeaseAudio()
    {
        if (AudioRouted || Lease is { Ended: false }) return null;
        return Lease = new FakeLease();
    }

    internal sealed class FakeLease : ITonelabAudioLease
    {
        public bool Interrupted { get; set; }

        public bool Ended { get; private set; }

        public void Dispose() => Ended = true;
    }

    public Task<TonelabResult> ShowWindowAsync(bool show, CancellationToken ct = default) => Ok();

    public int Quits { get; private set; }

    public Task<TonelabResult> QuitAsync(CancellationToken ct = default)
    {
        Quits++;
        return Ok();
    }

    private static Task<TonelabResult> Ok(string detail = "") => Task.FromResult(new TonelabResult(TonelabOutcome.Ok, detail));
}
