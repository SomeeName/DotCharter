using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Playback;

/// <inheritdoc />
public sealed class EditorLoopService : IEditorLoopService
{
    private readonly IAppLogger _logger;
    private readonly ProjectState _projectState;
    private readonly IEditingScope _scope;
    private readonly IEditTargetService _targets;

    /// <summary>Tick-thread only (the audio position timer). Not observable —
    /// it is a seek-in-flight latch, not user-visible state.</summary>
    private EditorLoopTickState _tick = EditorLoopTickState.Idle;

    public EditorLoopService(
        ProjectState projectState,
        IEditTargetService targets,
        IEditingScope scope,
        IAppLogger logger)
    {
        _projectState = projectState;
        _targets = targets;
        _scope = scope;
        _logger = logger;
        _projectState.PropertyChanged += OnProjectStateChanged;
    }

    /// <summary>
    ///     A loop belongs to the project it was punched in, and nothing else
    ///     forgot it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This service, <c>ProjectState</c> and the selection service are all
    ///         app-wide singletons.
    ///         <c>ChartEditorViewModel.ApplyProject</c> clears the SELECTION on a
    ///         swap for exactly this reason — "the selection service is an
    ///         app-wide singleton, so any swap leaves it pointing at a project no
    ///         longer on screen" — and stops there. The loop region is the same
    ///         shape one property along: open a song, punch 1:00–1:20, open a
    ///         different song, and the timeline drew a loop band over the new
    ///         chart while playback wrapped at a time that means nothing in it.
    ///         An armed but unfinished punch-IN was worse — the next "loop end"
    ///         pressed in the NEW song built a region reaching back to a moment
    ///         of the old one.
    ///     </para>
    ///     <para>
    ///         Done HERE rather than in <c>ApplyProject</c> so it covers every
    ///         project-replace path (open, import, crash recovery, the library's
    ///         in-place swap) instead of the one that happened to be edited.
    ///         Reassigning the SAME project instance raises nothing and so keeps
    ///         the loop — an undo of a whole-project tool restores in place and
    ///         must not throw the user's practice region away.
    ///     </para>
    /// </remarks>
    private void OnProjectStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectState.ActiveProject)) return;
        if (_projectState.LoopRegion is null && _projectState.LoopPendingStartMs is null &&
            !_projectState.LoopEnabled && !_projectState.LoopIsAudition)
        {
            return;
        }

        Clear();
    }

    public LivePlayLoop? Region => _projectState.LoopRegion;

    public bool IsEnabled => _projectState.LoopEnabled && _projectState.LoopRegion.HasValue;

    public void SetStart(double timeMs)
    {
        _projectState.LoopPendingStartMs = Math.Max(0, timeMs);
        _logger.Info(AppLogCategory.Editing, $"Editor loop: start armed at {timeMs:0} ms.");
    }

    public bool SetEnd(double timeMs)
    {
        if (_projectState.LoopPendingStartMs is not { } start)
        {
            _logger.Trace(AppLogCategory.Editing, "Editor loop: end pressed with no armed start.");
            return false;
        }

        if (!LivePlayLoop.TryCreate(start, Math.Max(0, timeMs), out var loop))
        {
            _logger.Trace(AppLogCategory.Editing,
                $"Editor loop: region {start:0}..{timeMs:0} ms is shorter than {LivePlayLoop.MinLengthMs} ms — refused.");
            return false;
        }

        Apply(loop, isAudition: false);
        _logger.Info(AppLogCategory.Editing,
            $"Editor loop: {loop.StartMs:0}..{loop.EndMs:0} ms ({loop.LengthMs:0} ms) armed and enabled.");
        return true;
    }

    public bool Toggle()
    {
        if (!_projectState.LoopRegion.HasValue) return false;

        _projectState.LoopEnabled = !_projectState.LoopEnabled;
        // Toggling off ends an audition too — a one-shot pass that cannot
        // complete must not leave the flag set for the next loop the user marks.
        if (!_projectState.LoopEnabled) _projectState.LoopIsAudition = false;
        _tick = EditorLoopTickState.Idle;
        _logger.Info(AppLogCategory.Editing,
            $"Editor loop: {(_projectState.LoopEnabled ? "enabled" : "disabled")}.");
        return true;
    }

    public void Clear()
    {
        _projectState.LoopRegion = null;
        _projectState.LoopEnabled = false;
        _projectState.LoopIsAudition = false;
        _projectState.LoopPendingStartMs = null;
        _tick = EditorLoopTickState.Idle;
        _logger.Info(AppLogCategory.Editing, "Editor loop: cleared.");
    }

    public bool BeginAudition(double startMs, double endMs)
    {
        if (!LivePlayLoop.TryCreate(Math.Max(0, startMs), Math.Max(0, endMs), out var loop))
        {
            _logger.Trace(AppLogCategory.Editing,
                $"Audition: span {startMs:0}..{endMs:0} ms is shorter than {LivePlayLoop.MinLengthMs} ms — refused.");
            return false;
        }

        Apply(loop, isAudition: true);
        _logger.Info(AppLogCategory.Editing, $"Audition: {loop.StartMs:0}..{loop.EndMs:0} ms (one pass).");
        return true;
    }

    public double? BeginAuditionOfTarget()
    {
        // The SAME ladder every keyboard verb uses, promoted so the sounds being
        // auditioned are visibly selected while they play.
        var target = _targets.ResolveAndPromote();
        if (target.HasNoSounds)
        {
            _logger.Trace(AppLogCategory.Editing, "Audition: nothing resolved.");
            return null;
        }

        if (!AuditionSpan.TryCompute(target.Notes, target.Chords, _scope.Beats,
                _projectState.TotalAudioLengthMs, out var startMs, out var endMs))
        {
            _logger.Trace(AppLogCategory.Editing, "Audition: the resolved target has no usable span.");
            return null;
        }

        return BeginAudition(startMs, endMs) ? startMs : null;
    }

    public void EndAudition()
    {
        if (!_projectState.LoopIsAudition) return;
        _projectState.LoopIsAudition = false;
        _projectState.LoopEnabled = false;
        _logger.Trace(AppLogCategory.Editing, "Audition: pass complete.");
    }

    public EditorLoopStep Tick(double rawPlayheadMs, bool isPlaying)
    {
        // Read the observable state ONCE per tick: this runs on the audio
        // position timer at up to 250 Hz while the UI thread owns the writes.
        var (step, next) = EditorLoopDecision.Advance(
            _tick,
            _projectState.LoopRegion,
            _projectState.LoopEnabled,
            _projectState.LoopIsAudition,
            rawPlayheadMs,
            isPlaying);

        _tick = next;
        return step;
    }

    /// <summary>Both entry points land here: one region, one enabled flag, one
    /// cleared latch, and the pending punch-in consumed.</summary>
    private void Apply(LivePlayLoop loop, bool isAudition)
    {
        _projectState.LoopRegion = loop;
        _projectState.LoopIsAudition = isAudition;
        _projectState.LoopEnabled = true;
        _projectState.LoopPendingStartMs = null;
        _tick = EditorLoopTickState.Idle;
    }
}
