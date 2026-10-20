using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;
using DotCharter.Services.Gameplay.Spectator;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The two performance-stake meters of a Live Play run — the rock meter
///     with its warn / danger / critical bands and its fail-out, and the crowd
///     meter — plus the per-run fail mark the recorded session carries. Each
///     meter is ARMED explicitly and each is off unless it was. Exposed as
///     <see cref="LivePlayViewModel.Meters" />; the gameplay HUD binds through
///     it (<c>LivePlay.Meters.RockEnergy</c>).
///     <para>
///         <b>Threading.</b> The meters are worker-confined state like the
///         session itself, and this type takes NO lock of its own: every member
///         below except the bound properties is called with
///         <see cref="LivePlayViewModel" />'s session gate held — from the
///         detection worker (<see cref="OnVerdict" />, <see cref="PumpRockMeter" />,
///         <see cref="TrySampleRockEnergy" />, <see cref="TrySampleCrowdValue" />)
///         or from the UI thread (arm, disarm, <see cref="Publish" />). The
///         bound properties are only ever written on the UI thread: directly by
///         arm/disarm/<see cref="Publish" />, and from the worker's coalesced tick
///         snapshot through <see cref="ApplyRockEnergy" /> /
///         <see cref="ApplyCrowdValue" />.
///     </para>
/// </summary>
public sealed partial class LivePlayMetersViewModel : ObservableObject
{
    private readonly IAppLogger _logger;

    // ---- Rock meter (0.24 performance stakes) --------------------------------
    //
    // The pure RockMeter is fed by LivePlayViewModel because it owns the single
    // judgment funnel (OnJudged) and the 30 Hz session tick: the meter consumes the SAME
    // verdict stream scoring consumes — silent-stretch misses included, since
    // HandleTick routes them through OnJudged too — and NEVER the render
    // thread's drained FeedbackEvents copy. Armed per run by the Play Mode
    // shell (PlayModeViewModel.BeginGameplay), which owns the exclusions:
    // disarmed when the Riff Repeater opens, and
    // editor practice / free play / listen-back never arm it at all.

    private RockMeter? _rockMeter;

    /// <summary>True while the current run plays with performance stakes —
    /// the gameplay HUD shows the energy bar only then.</summary>
    [ObservableProperty] private bool _isRockMeterActive;

    /// <summary>Stage energy 0..1 for the HUD bar. Published on the same
    /// 30 Hz coalesced session snapshot as <see cref="LivePlayViewModel.Score"/> — never from
    /// the raw 250 Hz position callback.</summary>
    [ObservableProperty] private double _rockEnergy = RockMeter.StartEnergy;

    /// <summary>HUD zone flags (Success → Warning → Danger, plus the critical
    /// emphasis), derived from the <see cref="RockMeter"/> zone constants on
    /// the same coalesced tick as <see cref="RockEnergy"/>.</summary>
    [ObservableProperty] private bool _isRockEnergyWarn;

    [ObservableProperty] private bool _isRockEnergyDanger;
    [ObservableProperty] private bool _isRockEnergyCritical;

    /// <summary>Set by the shell between the fail verdict and the session
    /// stop, so the persisted session record carries the fail-out mark.
    /// Cleared on every session start.</summary>
    private bool _runFailed;

    /// <summary>One-shot latch over the fail POST, so a failure seen by BOTH
    /// fail routes (the meter's own <see cref="RockMeter.Failed"/> event and
    /// <see cref="RockMeter.Tick"/>'s return value) ends the run once. Written
    /// under <c>_sessionGate</c> like the meter itself; reset per run in
    /// <see cref="BeginRun"/>.</summary>
    private bool _runFailPosted;

    // ---- Crowd meter HUD (0.28 Band night) -----------------------------------
    //
    // Same hosting rationale as the rock meter above: LivePlayViewModel owns the
    // single judgment funnel, so the pure CrowdMeter consumes the SAME verdict stream
    // scoring consumes. (Preview3DRenderDriver keeps its OWN render-thread
    // instance for the 3D crowd background — the two never share state.)
    // Purely cosmetic — no fail path, no config gate — so PlayModeViewModel
    // arms it for every gameplay run, and the editor's
    // practice/free-play surfaces never arm it.

    private CrowdMeter? _crowdMeter;

    /// <summary>True while a Play Mode gameplay run shows the crowd HUD.</summary>
    [ObservableProperty] private bool _isCrowdMeterActive;

    /// <summary>Room enthusiasm 0..1 for the HUD bar. Published on the same
    /// 30 Hz coalesced session snapshot as <see cref="LivePlayViewModel.Score"/>.</summary>
    [ObservableProperty] private double _crowdEnthusiasm = 0.4;

    /// <summary>HUD zone flags from the <see cref="CrowdMeter"/> constants,
    /// derived on the same coalesced tick as <see cref="CrowdEnthusiasm"/>.</summary>
    [ObservableProperty] private bool _isCrowdHot;

    [ObservableProperty] private bool _isCrowdCold;

    public LivePlayMetersViewModel(IAppLogger logger) => _logger = logger;

    /// <summary>Raised at most once per armed run when the meter fails.
    /// Posted (not invoked inline) so the session tick that latched it always
    /// finishes before the shell tears the run down.</summary>
    public event Action? RockMeterFailed;

    /// <summary>Whether the CURRENT run was ended by the rock meter's fail-out
    /// (<see cref="MarkRunFailed" />) — what the recorded session carries.</summary>
    public bool RunFailed => _runFailed;

    /// <summary>A new run may fail on its own account: clears the fail mark and
    /// the once-per-run fail latch. Called by <c>StartCore</c>.</summary>
    public void BeginRun()
    {
        _runFailed = false;
        _runFailPosted = false;
    }

    /// <summary>Marks the CURRENT run as ended by the rock meter's fail-out.</summary>
    public void MarkRunFailed() => _runFailed = true;

    /// <summary>Arms performance stakes: a fresh meter anchored at
    /// <paramref name="playheadMs" />.</summary>
    public void ArmRockMeter(double playheadMs)
    {
        _rockMeter = new RockMeter(playheadMs);
        // Both fail routes, one handler: the meter tells us the moment it
        // latches, whether that was a verdict or the session tick.
        _rockMeter.Failed += OnRockMeterLatchedFailure;
        PublishRockMeter(playheadMs);
        IsRockMeterActive = true;
    }

    /// <summary>Takes the stakes off — mid-run (Riff Repeater) or on any run
    /// end. Idempotent.</summary>
    public void DisarmRockMeter()
    {
        if (_rockMeter is { } meter)
        {
            meter.Failed -= OnRockMeterLatchedFailure;
        }

        _rockMeter = null;
        if (IsRockMeterActive)
        {
            IsRockMeterActive = false;
        }
    }

    /// <summary>Arms the crowd HUD: a fresh meter at neutral, anchored at
    /// <paramref name="playheadMs" />.</summary>
    public void ArmCrowdMeter(double playheadMs)
    {
        _crowdMeter = new CrowdMeter(startMs: playheadMs);
        PublishCrowdMeter(playheadMs);
        IsCrowdMeterActive = true;
    }

    /// <summary>Takes the crowd HUD off. Idempotent; called on every run exit.</summary>
    public void DisarmCrowdMeter()
    {
        _crowdMeter = null;
        if (IsCrowdMeterActive)
        {
            IsCrowdMeterActive = false;
        }
    }

    /// <summary>Feeds one judged sound to whichever meters are armed — the same
    /// verdict stream scoring consumes. <paramref name="streak" /> is read AFTER
    /// the verdict.</summary>
    public void OnVerdict(LivePlayVerdict verdict, int streak, double detectedAtMs)
    {
        _rockMeter?.OnVerdict(verdict, streak, detectedAtMs);
        _crowdMeter?.OnVerdict(verdict, streak, detectedAtMs);
    }

    /// <summary>A hit already fed as a full hit was downgraded by the contour judge:
    /// each armed meter takes back the hit's extra over a half hit, at the streak it was fed at.</summary>
    public void OnTechniqueDowngrade(int streakAtCredit, double nowMs)
    {
        _rockMeter?.OnTechniqueDowngrade(streakAtCredit, nowMs);
        _crowdMeter?.OnTechniqueDowngrade(streakAtCredit, nowMs);
    }

    /// <summary>Advances the meter's clock (recovery drift + fail debounce)
    /// once per session tick. Time only moves while the transport runs, so a
    /// paused run neither drifts nor progresses the debounce.
    /// <para>
    /// The FAILURE itself is driven by <see cref="OnRockMeterLatchedFailure" />,
    /// not by this return value — a latch crossed inside <c>OnVerdict</c> never
    /// reaches this call at all (an already-failed meter's <c>Tick</c> returns
    /// false forever). The return value only says whether THIS call was the one
    /// that latched, which is worth a playhead in the log and nothing else.
    /// </para></summary>
    public void PumpRockMeter(double playheadMs, bool isPlaying)
    {
        if (_rockMeter is not { } meter || !isPlaying)
        {
            return;
        }

        if (meter.Tick(playheadMs))
        {
            _logger.Debug(AppLogCategory.Audio, $"Rock meter fail latched on the session tick at {playheadMs:0} ms.");
        }
    }

    /// <summary>Worker side of the HUD: the rock meter's energy at
    /// <paramref name="playheadMs" />, for the tick snapshot. False while unarmed.</summary>
    public bool TrySampleRockEnergy(double playheadMs, out double energy)
    {
        if (_rockMeter is not { } meter)
        {
            energy = 0;
            return false;
        }

        // Rounded so the 30 Hz snapshot only moves the bar when it would
        // visibly move (and not at all while parked at the cap).
        energy = Math.Round(meter.Energy(playheadMs), 3);
        return true;
    }

    /// <summary>Worker side of the crowd HUD: the crowd meter's value at
    /// <paramref name="playheadMs" />, for the tick snapshot. False while unarmed.</summary>
    public bool TrySampleCrowdValue(double playheadMs, out double value)
    {
        if (_crowdMeter is not { } crowd)
        {
            value = 0;
            return false;
        }

        value = Math.Round(crowd.Value(playheadMs), 3);
        return true;
    }

    /// <summary>UI-thread publish of both armed meters at <paramref name="playheadMs" />,
    /// for the UI-side session operations that change stats synchronously.</summary>
    public void Publish(double playheadMs)
    {
        PublishRockMeter(playheadMs);
        PublishCrowdMeter(playheadMs);
    }

    /// <summary>UI-thread apply of a rock-meter energy (from the tick snapshot
    /// or a direct publish): the bar and its zone flags.</summary>
    public void ApplyRockEnergy(double energy)
    {
        RockEnergy = energy;
        IsRockEnergyWarn = energy < RockMeter.WarnBelow;
        IsRockEnergyDanger = energy < RockMeter.DangerBelow;
        IsRockEnergyCritical = energy < RockMeter.CriticalBelow;
    }

    /// <summary>UI-thread apply of a crowd value (from the tick snapshot or a
    /// direct publish): the bar and its zone flags.</summary>
    public void ApplyCrowdValue(double value)
    {
        CrowdEnthusiasm = value;
        IsCrowdHot = value >= CrowdMeter.HotAbove;
        IsCrowdCold = value < CrowdMeter.ColdBelow;
    }

    /// <summary>Ends the CURRENT run through the shell's fail path, at most
    /// once per run. Posted, never invoked inline: the handler ends the session
    /// (<c>IsActive = false</c>), which must not reenter the tick that is still
    /// unwinding.</summary>
    private void PostRunFailed(string reason)
    {
        if (_runFailPosted)
        {
            return;
        }

        _runFailPosted = true;
        _logger.Info(AppLogCategory.Audio, reason);
        Dispatcher.UIThread.Post(() => RockMeterFailed?.Invoke());
    }

    /// <summary>
    ///     The meter's own fail latch, from WHICHEVER of <see cref="RockMeter.OnVerdict" />
    ///     / <see cref="RockMeter.Tick" /> crossed the debounce. Subscribed in
    ///     <see cref="ArmRockMeter" />; raised synchronously on the detection
    ///     worker, under <c>_sessionGate</c>.
    ///     <para>
    ///         <b>Listening to the EVENT is the fix.</b> The <c>Tick</c> return value of
    ///         <see cref="PumpRockMeter" /> alone cannot detect a fail,
    ///         because <c>LivePlayViewModel.OnJudged</c> discards <c>OnVerdict</c>'s
    ///         effect entirely — so a debounce crossed INSIDE a verdict latched
    ///         <see cref="RockMeter.HasFailed" />, raised <c>Failed</c> into the
    ///         void and made every later <c>Tick</c> return false FOREVER (a
    ///         failed meter is inert). The run then hung UNFAILABLE: zero
    ///         energy, no fail overlay, no way to reach
    ///         <c>PlayModeScreen.Failed</c>. It is reachable because a verdict's
    ///         <c>DetectedAtMs</c> is a RAW detection timestamp while the pump's
    ///         playhead is LATENCY-COMPENSATED and therefore trails it, so the
    ///         verdict's clock crosses the debounce first.
    ///     </para>
    /// </summary>
    private void OnRockMeterLatchedFailure() => PostRunFailed("Rock meter failed the run (energy stayed at zero past the debounce).");

    private void PublishRockMeter(double playheadMs)
    {
        if (TrySampleRockEnergy(playheadMs, out var energy))
        {
            ApplyRockEnergy(energy);
        }
    }

    private void PublishCrowdMeter(double playheadMs)
    {
        if (TrySampleCrowdValue(playheadMs, out var value))
        {
            ApplyCrowdValue(value);
        }
    }
}
