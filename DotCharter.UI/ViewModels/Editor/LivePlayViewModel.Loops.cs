// LivePlayViewModel: where a run PLAYS - the resume rewind, the first-note count-in clicks, the punch-in/out and riff-repeater loops, the drill range and the speed-ladder arm.

using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Everything that decides WHERE a run plays: the resume rewind a pause
///     gives back, the first-note count-in clicks, the punch-in/punch-out loop,
///     the riff-repeater's section pick, the smart drill's explicit range, and
///     the one-shot speed-ladder arm.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>
    ///     Rocksmith-style resume: rewinds the transport by <paramref name="leadInMs"/>
    ///     so the player re-orients before notes arrive again. The seek is armed as the
    ///     SESSION's own (<see cref="LivePlaySessionService.BeginResumeRewind"/>), so
    ///     nothing already judged is rewound — the replayed stretch cannot be scored or
    ///     missed a second time and renders dimmed. Returns the seek target, or null
    ///     when nothing was rewound (no session, lead-in off, song start).
    /// </summary>
    public double? BeginResumeRewind(double leadInMs)
    {
        lock (_sessionGate) // serialized against the worker tick
        {
            if (!_session.IsActive) return null;
            if (_session.BeginResumeRewind(_projectState.CurrentTimeMs, leadInMs) is not { } target) return null;

            _audio.SeekTo(target);
            // No CQT bookkeeping: it is indexed on the raw capture-stream axis, which a
            // transport seek does not disturb, and its chart anchor is re-taken from the
            // playhead on the next playing tick. (No _cqtOffsetSet flag: projecting
            // the offset is what broke detection at every practice speed.)
            PublishStats();        // republishes the dead-note horizon for the highway
            return target;
        }
    }

    // ---- Pre-first-note count-in (0.24.x) ---------------------------------------
    //
    // The run-start count-in (CountInPlan) plays a pre-roll and then starts the
    // transport; on a long noteless intro that leaves the player in silence until
    // notes arrive unannounced. This schedules the pure FirstNoteCountInPlan's
    // clicks — the N beats immediately before the first note — sample-accurately
    // over the render clock (TryScheduleClick), so they land ON those beats.
    //
    // WHY SCHEDULED HERE, NOT ALL AT RUN START: the native engine drops any click
    // more than ~5 s ahead of its render clock (AudioEngine.cpp futureFrames), and
    // the whole point of the feature is a first note many seconds out. So the plan
    // is ARMED at run start and each click is scheduled as the playhead comes
    // within reach, on the same 30 Hz tick that drives judging.

    /// <summary>Arms the pre-first-note count-in for this run. Empty disarms.
    /// Called by <c>PlayModeViewModel.BeginGameplay</c> with the pure
    /// <see cref="FirstNoteCountInPlan"/> schedule (skipped when the first note is within the run-start count-in's reach).</summary>
    public void ArmFirstNoteCountIn(FirstNoteCountInPlan.Click[] clicks)
    {
        lock (_sessionGate) // the worker walks these arrays every tick
        {
            _firstNoteCountIn.Arm(clicks);
        }
    }

    /// <summary>Hands the engine any count-in click that has come within the
    /// lookahead (<see cref="FirstNoteCountInSchedule" /> decides which). On a seek
    /// (e.g. the resume rewind, which clears the engine's scheduled clicks) any
    /// click still ahead of the new playhead is re-armed.</summary>
    private void ScheduleFirstNoteCountIn(bool sawSeek)
    {
        if (!_firstNoteCountIn.IsArmed)
        {
            return;
        }

        var rawMs = _audio.RawPlayheadMs;
        if (sawSeek)
        {
            _firstNoteCountIn.RearmAfterSeek(rawMs);
        }

        while (_firstNoteCountIn.TryTakeDue(rawMs, out var click))
        {
            _audio.TryScheduleClick(click.Accent ? 1 : 0, click.TimeMs);
        }
    }

    // ---- A/B loop + riff repeater -------------------------------------------

    /// <summary>Whether THIS session runs a speed ladder (the config toggle at
    /// session start). Stop restores the pre-session speed off this, not the
    /// live toggle — a mid-session toggle flip must not strand the transport at
    /// a ladder speed.</summary>
    private bool _sessionHadLadder;

    /// <summary>Punch-in: arms the loop start at the current playhead.</summary>
    [RelayCommand]
    private void SetLoopStart()
    {
        lock (_sessionGate)
        {
            if (!_session.IsActive) return;
            _pendingLoopStartMs = _projectState.CurrentTimeMs;
            LoopStartArmed = true;
            StatusHint = _localization["LivePlay_LoopArmed"];
        }
    }

    /// <summary>Punch-out: closes the loop at the current playhead and starts
    /// the first pass (with count-in).</summary>
    [RelayCommand]
    private void SetLoopEnd()
    {
        lock (_sessionGate)
        {
            if (!_session.IsActive || _pendingLoopStartMs is not { } start) return;
            ApplyLoop(_session.SetLoop(start, _projectState.CurrentTimeMs));
        }
    }

    [RelayCommand]
    private void ClearLoop()
    {
        lock (_sessionGate)
        {
            _session.ClearLoop();
            _pendingLoopStartMs = null;
            LoopActive = false;
            LoopStartArmed = false;
            LoopLabel = string.Empty;
            CountInRemaining = 0;
            StatusHint = string.Empty;
        }
    }

    /// <summary>Riff-repeater pick: loops the chart section at the given time
    /// (wired to clicks on the section strip).</summary>
    public void LoopSectionAt(double timeMs)
    {
        lock (_sessionGate)
        {
            if (!_session.IsActive) return;
            var section = _session.SectionAt(timeMs);
            if (section == null) return;
            ApplyLoop(_session.SetLoop(section));
        }
    }

    private void ApplyLoop(double? seekTarget)
    {
        if (seekTarget is not { } target || _session.Loop is not { } loop) return;
        _pendingLoopStartMs = null;
        LoopStartArmed = false;
        LoopActive = true;
        LoopLabel = $"{TimeLabel(loop.StartMs)} - {TimeLabel(loop.EndMs)}";
        StatusHint = string.Empty;
        _audio.SeekTo(target);
        if (!_audio.IsPlaying) _audio.Play();
        PublishStats();
    }
}
