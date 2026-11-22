using Avalonia;
using DotCharter.Services.Editing.Lyrics;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>Previews and commits a snapped lyric move with its in-phrase neighbour adjustment.</summary>
public sealed class LyricDragGesture : IEventGesture
{
    private readonly IHistoryService _historyManager;
    private readonly IConfigurationService _configService;
    private readonly IAppLogger _logger;

    private readonly GestureDragThreshold _threshold = new();

    private LyricEvent? _target;
    private LyricEvent? _previousInPhrase;
    private FractionalPosition _previousEnd;
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
        if (!_threshold.Observe(context.Position)) return;

        RestoreTiming(_target);
        _previousInPhrase = null;
        var editor = context.ViewModel.ChartEditor;
        var beats = editor.Beats;
        var phrases = editor.ActiveVocalTrack?.Phrases;
        if (beats.Count == 0 || phrases == null) return;

        var deltaMs = context.ExactTimeMs - _startMouseMs;
        var startMs = Math.Max(0.0, _originalPos.GetPositionMs(beats) + deltaMs);
        var snapped = editor.SnapLyricStartMs(startMs);
        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, _target, out var prevInPhrase, out var nextInPhrase);
        var config = _configService.LoadAppConfig();
        var minimumLength = LyricPhraseSpacing.MinimumLength(context.ViewModel.Transport.GridDivisor);
        var lower = FractionalPosition.Zero;
        if (prev != null)
        {
            var previousFloor = prevInPhrase
                ? prev.Position + minimumLength
                : FractionalPosition.Max(prev.Position, prev.EndPosition);
            lower = FractionalPosition.Max(lower,
                previousFloor + ChartSpacingEnforcer.GetMinDistance(prev.Position, beats, config));
        }

        var start = FractionalPosition.Max(snapped, lower);
        FractionalPosition? limit = null;
        if (next != null)
        {
            // Recheck after each retreat because crossing a tempo or meter boundary changes the gap.
            start = FractionalPosition.Min(start, next.Position);
            while (true)
            {
                if (start < lower) return;
                limit = LyricPhraseSpacing.EndLimit(start, next.Position, beats, config);
                var latest = limit.Value - minimumLength;
                if (start <= latest) break;
                start = latest;
            }
        }

        if (start == _originalPos) return;
        _target.Position = start;
        var durationMs = _originalEnd.GetPositionMs(beats) - _originalPos.GetPositionMs(beats);
        var preservedEnd = TimelineMath.PositionFromMs(
            start.GetPositionMs(beats) + (durationMs > 0 ? durationMs : 200.0), beats);
        var end = nextInPhrase && !_target.IsSustainShortened
            ? limit!.Value
            : FractionalPosition.Max(start + minimumLength, preservedEnd);
        _target.EndPosition = limit is { } cap ? FractionalPosition.Min(end, cap) : end;
        if (prevInPhrase && prev != null)
        {
            _previousInPhrase = prev;
            _previousEnd = prev.EndPosition;
            prev.EndPosition = LyricPhraseSpacing.EndLimit(prev.Position, start, beats, config);
        }
    }

    public void Commit(ToolContext context)
    {
        if (!IsActive || _target == null) { Reset(); return; }

        var newPos = _target.Position;
        var newEnd = _target.EndPosition;
        var previousNewEnd = _previousInPhrase?.EndPosition;
        RestoreTiming(_target);
        IChartAction action = new MoveLyricAction(_target, _originalPos, _originalEnd, newPos, newEnd);
        if (_previousInPhrase is { } previous && previousNewEnd is { } end)
            action = new CompositeAction([action, new ResizeLyricAction(previous, _previousEnd, end, shortened: false)]);
        if (_historyManager.Do(action))
            _logger.Info(AppLogCategory.Events, $"Committed lyric drag. Text='{_target.Text}'.");

        Reset();
    }

    public void Cancel(ToolContext context)
    {
        if (IsActive && _target != null) RestoreTiming(_target);
        Reset();
    }

    private void RestoreTiming(LyricEvent lyric)
    {
        lyric.Position = _originalPos;
        lyric.EndPosition = _originalEnd;
        _previousInPhrase?.EndPosition = _previousEnd;
    }

    private void Reset()
    {
        _target = null;
        _previousInPhrase = null;
        Track = null;
        IsActive = false;
        _threshold.Reset();
    }

    public TrackEditorContext? Track { get; private set; }
}
