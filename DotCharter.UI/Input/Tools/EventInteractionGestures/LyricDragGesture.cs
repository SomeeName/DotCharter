using Avalonia;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Drag a lyric pill horizontally. The pill is moved live during the drag
/// (so the user sees real placement); on Commit we restore the pre-drag
/// position and replay the move through a <see cref="MoveLyricAction"/>
/// so undo/redo are correct.
/// <para>
/// The drag is CLAMPED between the neighbouring syllables (which may live in
/// the adjacent phrase) with the note-style minimum gap — a lyric can no more
/// be dragged past its neighbour than a note can, and since no spacing pass
/// covers vocals on save, this gesture is the only enforcement. See
/// <see cref="LyricDragBounds"/>.
/// </para>
/// </summary>
public sealed class LyricDragGesture : IEventGesture
{
    private readonly IHistoryService _historyManager;
    private readonly IConfigurationService _configService;
    private readonly IAppLogger _logger;

    private readonly GestureDragThreshold _threshold = new();

    private LyricEvent? _target;
    private FractionalPosition _originalPos;
    private FractionalPosition _originalEnd;
    private double _startMouseMs;

    public LyricDragGesture(IHistoryService historyManager, IConfigurationService configService,
        IAppLogger logger)
    {
        _historyManager = historyManager;
        _configService = configService;
        _logger = logger;
    }

    public bool IsActive { get; private set; }
    public bool HasDragged => _threshold.HasDragged;

    public void Start(TrackEditorContext track, LyricEvent lyric, double startMouseMs, Point pressOrigin)
    {
        Track = track;
        _target = lyric;
        _originalPos = lyric.Position;
        _originalEnd = lyric.EndPosition;
        _startMouseMs = startMouseMs;
        IsActive = true;
        _threshold.Arm(pressOrigin);
    }

    public void OnMoved(ToolContext context)
    {
        if (!IsActive || _target == null) return;
        // A CLICK is not a drag — see GestureDragThreshold. Selecting a pill
        // must not snap it to the grid or to a waveform onset.
        if (!_threshold.Observe(context.Position)) return;

        var beats = context.ViewModel.ChartEditor.Beats;
        var deltaMs = context.ExactTimeMs - _startMouseMs;
        var newStartMs = _originalPos.GetPositionMs(beats) + deltaMs;
        if (newStartMs < 0) newStartMs = 0;
        var span = _originalEnd.GetPositionMs(beats) - _originalPos.GetPositionMs(beats);
        if (span <= 0) span = 200.0;

        // Snap the start to the editor grid (or to a nearby note / waveform
        // onset), then preserve the original pill width so the user is
        // repositioning, not resizing.
        var snappedStart = context.ViewModel.ChartEditor.SnapLyricStartMs(newStartMs);
        var snappedStartMs = snappedStart.GetPositionMs(beats);
        if (snappedStartMs < 0) snappedStartMs = 0;

        // Clamp AFTER the snap: the start stays between the neighbouring
        // syllables' starts with the note-style minimum gap, each gap evaluated
        // at the neighbour it bounds against. Recomputed from the ORIGINAL span
        // every move, so dragging against a wall and back never ratchets.
        var finalStart = snappedStart;
        double? nextStartMs = null;
        var upperGapMs = 0.0;
        var phrases = context.ViewModel.ChartEditor.ActiveVocalTrack?.Phrases;
        if (phrases != null)
        {
            var config = _configService.LoadAppConfig();
            var (prev, next) = LyricDragBounds.FindNeighbors(phrases, _target);
            var prevStartMs = prev?.Position.GetPositionMs(beats);
            nextStartMs = next?.Position.GetPositionMs(beats);
            var lowerGapMs = prev != null ? LyricDragBounds.MinGapMsAt(prev.Position, beats, config) : 0.0;
            upperGapMs = next != null ? LyricDragBounds.MinGapMsAt(next.Position, beats, config) : 0.0;

            var (lower, upper) = LyricDragBounds.ComputeStartBounds(
                prevStartMs, lowerGapMs, nextStartMs, upperGapMs);
            lower = Math.Max(lower, 0.0);
            if (upper < lower)
            {
                // The neighbours already sit closer than two gaps — there is
                // nowhere legal to go, so the pill stays where it started.
                snappedStartMs = _originalPos.GetPositionMs(beats);
                finalStart = _originalPos;
            }
            else
            {
                var clampedMs = LyricDragBounds.Clamp(snappedStartMs, lower, upper);
                if (clampedMs != snappedStartMs)
                {
                    snappedStartMs = clampedMs;
                    finalStart = TimelineMath.PositionFromMs(clampedMs, beats);
                }
            }
        }

        _target.Position = finalStart;
        TrimAndSetEnd(snappedStartMs, span, nextStartMs, upperGapMs, beats);
    }

    /// <summary>The pill keeps its span, but its END may never cross into the
    /// next syllable's minimum gap — mirrors how note sustains are capped at
    /// the next onset minus the minimum distance.</summary>
    private void TrimAndSetEnd(double startMs, double span, double? nextStartMs, double gapMs,
        IList<BeatMarker> beats)
    {
        var endMs = startMs + span;
        if (nextStartMs is { } next) endMs = Math.Min(endMs, next - gapMs);
        if (endMs < startMs) endMs = startMs;
        _target!.EndPosition = TimelineMath.PositionFromMs(endMs, beats);
    }

    public void Commit(ToolContext context)
    {
        if (!IsActive || _target == null) { Reset(); return; }

        var newPos = _target.Position;
        var newEnd = _target.EndPosition;
        _target.Position = _originalPos;
        _target.EndPosition = _originalEnd;
        var action = new MoveLyricAction(_target, _originalPos, _originalEnd, newPos, newEnd);
        if (_historyManager.Do(action))
            _logger.Info(AppLogCategory.Events, $"Committed lyric drag. Text='{_target.Text}'.");

        Reset();
    }

    public void Cancel(ToolContext context)
    {
        if (!IsActive || _target == null) { Reset(); return; }
        _target.Position = _originalPos;
        _target.EndPosition = _originalEnd;
        Reset();
    }

    private void Reset()
    {
        _target = null;
        Track = null;
        IsActive = false;
        _threshold.Reset();
    }

    public TrackEditorContext? Track { get; private set; }
}
