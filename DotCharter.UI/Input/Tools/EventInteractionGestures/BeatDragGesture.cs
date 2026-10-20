using Avalonia;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Drag a beat marker horizontally to retime / promote it to an anchor.
///
/// Lifecycle: Start → many OnMoved calls → either Commit (drag) or
/// Cancel (no movement / pointer exited). Commit produces a single
/// <see cref="MutateBeatsAction"/> on the history stack with the
/// pre/post beat snapshots. Cancel restores the live track to its
/// pre-drag state without touching history.
/// </summary>
public sealed class BeatDragGesture : IEventGesture
{
    private readonly IConfigurationService _configService;
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;

    private readonly GestureDragThreshold _threshold = new();

    private List<BeatMarker> _originalBeats = [];
    private double _startMouseMs;
    private int _beatIndex = -1;

    public BeatDragGesture(IHistoryService historyManager, IConfigurationService configService, IAppLogger logger)
    {
        _historyManager = historyManager;
        _configService = configService;
        _logger = logger;
    }

    public bool IsActive { get; private set; }
    public bool HasDragged => _threshold.HasDragged;

    public void Start(TrackEditorContext track, BeatMarker hitBeat, int beatIndex, double startMouseMs,
        Point pressOrigin)
    {
        Track = track;
        _beatIndex = beatIndex;
        _startMouseMs = startMouseMs;
        _originalBeats = TempoMapRecalculator.Snapshot(track.Beats);
        IsActive = true;
        _threshold.Arm(pressOrigin);
        _logger.Info(AppLogCategory.Events,
            $"Started beat drag. BeatIndex={beatIndex}, originalTimeMs={hitBeat.Time:0.###}, " +
            $"bpm={hitBeat.Bpm:0.###}, isAnchor={hitBeat.IsAnchor}, isMeasure={hitBeat.IsMeasure}.");
    }

    public void OnMoved(ToolContext context)
    {
        if (!IsActive || Track == null) return;
        // A CLICK is not a drag — see GestureDragThreshold. Selecting a beat
        // must not retime it and recalculate the segment's BPM.
        if (!_threshold.Observe(context.Position)) return;

        var delta = context.ExactTimeMs - _startMouseMs;
        var newTime = _originalBeats[_beatIndex].Time + delta;
        var (lower, upper) = BeatDragBounds.ComputeBounds(_originalBeats, _beatIndex);
        newTime = BeatDragBounds.Clamp(newTime, lower, upper);

        TempoMapRecalculator.Recalculate(Track.Beats, _originalBeats, _beatIndex, newTime);
    }

    public void Commit(ToolContext context)
    {
        if (!IsActive || Track == null) { Reset(); return; }

        var vm = context.ViewModel;
        var beats = Track.Beats;

        var newBeats = TempoMapRecalculator.Snapshot(beats);
        for (var i = 0; i < beats.Count; i++)
        {
            beats[i].Time = _originalBeats[i].Time;
            beats[i].Bpm = _originalBeats[i].Bpm;
            beats[i].IsAnchor = _originalBeats[i].IsAnchor;
        }

        var oldSnapshot = _originalBeats.ToList();
        var action = new MutateBeatsAction(beats, oldSnapshot, newBeats,
            () =>
            {
                ChartSpacingEnforcer.EnforceProject(vm.ProjectState.ActiveProject,
                    _configService.LoadAppConfig());
                vm.ChartEditor.RequestRedraw();
            });

        if (_historyManager.Do(action))
        {
            _logger.Info(AppLogCategory.Events,
                $"Committed beat drag. BeatIndex={_beatIndex}, " +
                $"newTimeMs={newBeats[_beatIndex].Time:0.###}, " +
                $"oldTimeMs={oldSnapshot[_beatIndex].Time:0.###}, " +
                $"bpm={newBeats[_beatIndex].Bpm:0.###}.");
        }

        Reset();
    }

    public void Cancel(ToolContext context)
    {
        if (!IsActive || Track == null) { Reset(); return; }
        for (var i = 0; i < Track.Beats.Count && i < _originalBeats.Count; i++)
        {
            Track.Beats[i].Time = _originalBeats[i].Time;
            Track.Beats[i].Bpm = _originalBeats[i].Bpm;
            Track.Beats[i].IsAnchor = _originalBeats[i].IsAnchor;
        }
        Reset();
    }

    private void Reset()
    {
        _originalBeats = [];
        _beatIndex = -1;
        Track = null;
        IsActive = false;
        _threshold.Reset();
    }

    public TrackEditorContext? Track { get; private set; }
}
