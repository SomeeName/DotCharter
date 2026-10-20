using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input.Tools;

public class ToolCoordinator
{
    // Stopwatch timestamps, not DateTime.UtcNow: the wall clock advances in
    // ~15.6 ms jumps on Windows, so a 16 ms threshold against it admitted
    // either 0 or 2 clock steps per frame — the throttle randomly passed
    // back-to-back moves or ate a whole frame's worth (P2-5).
    private static readonly long ThrottleTimestamps = Stopwatch.Frequency * 16 / 1000;
    private readonly ToolContext _context;
    private readonly IHistoryService _history;
    private readonly ITrackContextRegistry _trackContexts;
    private readonly EventInteractionTool _eventTool;
    private readonly FretAnchorInteractionTool _fretAnchorTool;
    private readonly HandShapeInteractionTool _handShapeTool;
    private readonly HighwayInteractionTool _highwayTool;

    private IEditorTool? _activeTool; // Tool currently dragging
    private IEditorTool? _currentHoverTool; // Tool currently being hovered
    private long _lastMoveTicks;
    private bool _rulerScrubbing; // Left-drag on the time ruler = scrub-seek
    private readonly Arrangement? _boundArrangement;

    public ToolCoordinator(MainWindowViewModel vm, IChartViewport viewport,
        ITrackContextRegistry trackContexts, IEnumerable<IEditorTool> tools,
        IHistoryService history,
        Arrangement? boundArrangement = null)
    {
        _history = history;
        _trackContexts = trackContexts;
        _boundArrangement = boundArrangement;
        _context = new ToolContext(vm, viewport, trackContexts);
        var toolsList = tools.ToList();
        _eventTool = toolsList.OfType<EventInteractionTool>().FirstOrDefault() ??
                     throw new Exception("Missing EventInteractionTool");
        _handShapeTool = toolsList.OfType<HandShapeInteractionTool>().FirstOrDefault() ??
                         throw new Exception("Missing HandShapeInteractionTool");
        _fretAnchorTool = toolsList.OfType<FretAnchorInteractionTool>().FirstOrDefault() ??
                          throw new Exception("Missing FretAnchorInteractionTool");
        _highwayTool = toolsList.OfType<HighwayInteractionTool>().FirstOrDefault() ??
                       throw new Exception("Missing HighwayInteractionTool");
    }

    private TrackEditorContext? ResolveTrack()
    {
        if (_boundArrangement != null) return _trackContexts.GetOrCreate(_boundArrangement);
        // Main timeline always targets the active arrangement, not whatever
        // panel was last focused — otherwise after editing in a stacked row
        // the main highway would silently keep targeting the stacked one.
        var active = _context.ViewModel.ProjectState.ActiveArrangement;
        if (active != null) return _trackContexts.GetOrCreate(active);
        return _trackContexts.FocusedTrack;
    }

    private void UpdateContext(PointerEventArgs e, Point p, PointerPointProperties props)
    {
        // Caller must have already verified ResolveTrack returns non-null.
        _context.Track = ResolveTrack()!;

        // Tag the global selection service with the arrangement currently
        // owning the preview/hover state so the renderer can suppress
        // preview overlays in panels that don't own them.
        _context.ViewModel.ChartEditor.Selection.PreviewArrangement = _context.Track.Arrangement;

        _context.Position = p;
        _context.Modifiers = e.KeyModifiers;
        _context.Properties = props;

        // The SAME effective view anchor the renderer maps through — while the
        // viewport is panned away from the playhead (Follow off), pointer time
        // must be derived from the panned window, not the transport position.
        var bounds = ChartHitTester.GetViewportBounds(_context.ViewModel.ProjectState.TimelineViewAnchorEffectiveMs,
            _context.ViewModel.ProjectState.PixelsPerMs,
            _context.Viewport.ViewportWidth);
        _context.ExactTimeMs = bounds.StartMs + p.X / _context.ViewModel.ProjectState.PixelsPerMs;
        _context.ViewModel.ProjectState.LastPointerTimeMs = _context.ExactTimeMs;

        _context.ResetSnapCache();

        var stringCount = _context.Track.StringCount;
        _context.StringIndex = ChartHitTester.GetLaneIndexAtY(
            p.Y,
            _context.Viewport.ViewportHeight,
            _context.Viewport.TopMargin,
            _context.Viewport.BottomMargin,
            stringCount,
            _context.Viewport.FlipStrings);
    }

    /// <summary>
    ///     A pointer press and a pointer release are the two moments a timeline
    ///     gesture ENDS deterministically, so both close the open history
    ///     gesture rather than leaving it to <c>HistoryService.MergeWindow</c>'s
    ///     900 ms guess.
    ///     <para>
    ///         <c>IHistoryService.EndGesture</c> is documented as the escape
    ///         hatch for "gestures that know their own end" and had <b>no
    ///         production caller at all</b> — every gesture in the editor was
    ///         left to the timer. The window is deliberately erring LONG (two
    ///         deliberate taps merging is cheaper than 111 nodes per wheel run),
    ///         and that is only safe while something closes a run the moment the
    ///         user visibly starts a different one. Without this, wheeling a
    ///         hand shape longer, clicking it, and wheeling again inside the
    ///         window is ONE undo node — same collection, same instances, same
    ///         verb, and nothing in between pushed a node to move the anchor.
    ///     </para>
    /// </summary>
    public void HandlePointerPressed(PointerPressedEventArgs e, Point p, PointerPointProperties props)
    {
        _history.EndGesture();
        var track = ResolveTrack();
        if (track == null) return;
        // Pressing in any panel makes that panel the focused track so global
        // commands (copy/paste/delete/select-all) target it. ActiveArrangement
        // is intentionally left alone — focus is a UI concept, active is the
        // export pivot.
        _trackContexts.Focus(track.Arrangement);
        UpdateContext(e, p, props);

        // A left press on the TIME RULER is a seek (drags scrub), never a tool
        // gesture. Otherwise presses fall through the band routing: the
        // highway tool arms the marquee and the release CLEARS the selection,
        // and a click a few pixels higher lands in the unlabeled FHP band and
        // silently places an anchor — the live audit's "clicking the ruler
        // created a persistent yellow region". DAW convention wins here.
        if (props.IsLeftButtonPressed && TimelineZones.IsTimeRuler(p.Y, _context.Viewport.ViewportHeight))
        {
            _rulerScrubbing = true;
            _context.ViewModel.ChartEditor.ExecuteSeek(_context.ExactTimeMs);
            return;
        }

        _activeTool ??= DetermineTool(_context.Position.Y);
        _activeTool?.OnPointerPressed(e, _context);
    }

    public void HandlePointerMoved(PointerEventArgs e, Point p, PointerPointProperties props)
    {
        if (ResolveTrack() == null) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastMoveTicks < ThrottleTimestamps) return;
        _lastMoveTicks = now;
        UpdateContext(e, p, props);

        if (_rulerScrubbing)
        {
            // Scrub: same ~60 Hz cadence as the minimap drag (the throttle
            // above), through the same deliberate-navigation funnel.
            if (props.IsLeftButtonPressed) _context.ViewModel.ChartEditor.ExecuteSeek(_context.ExactTimeMs);
            else _rulerScrubbing = false;
            return;
        }

        var routingTool = _activeTool ?? DetermineTool(_context.Position.Y);

        // FIX: If we crossed into a new tool's territory, tell the old tool we left so it wipes its hovers!
        if (_activeTool == null && _currentHoverTool != routingTool)
        {
            _currentHoverTool?.OnPointerExited(e, _context);
            _currentHoverTool = routingTool;
        }

        routingTool.OnPointerMoved(e, _context);
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e, Point p, PointerPointProperties props)
    {
        if (ResolveTrack() == null) return;
        UpdateContext(e, p, props);
        if (_rulerScrubbing)
        {
            // The last throttled move already sought the drop position; a
            // third identical seek is the minimap's documented dropout risk.
            _rulerScrubbing = false;
            _history.EndGesture();
            return;
        }
        _activeTool?.OnPointerReleased(e, _context);
        _activeTool = null;
        // The drag's own commit landed above; closing here is what stops the
        // NEXT gesture folding into it. See HandlePointerPressed.
        _history.EndGesture();
    }

    public void HandlePointerExited(PointerEventArgs e, Point p, PointerPointProperties props)
    {
        _rulerScrubbing = false;
        if (ResolveTrack() == null) return;
        UpdateContext(e, p, props);
        var routingTool = _activeTool ?? DetermineTool(_context.Position.Y);
        routingTool.OnPointerExited(e, _context);
        _currentHoverTool = null;
    }

    private IEditorTool DetermineTool(double yPosition)
    {
        // The band boundaries are the shared TimelineZones predicates — the
        // gutter's band captions and the cursor policy read the same ones, so
        // what a band is labeled, what the cursor promises and what a click
        // does can never disagree.
        if (yPosition < _context.Viewport.TopMargin) return _eventTool;

        if (TimelineZones.IsHandShapeBand(yPosition, _context.Viewport.ViewportHeight,
                _context.Viewport.BottomMargin))
        {
            return _handShapeTool;
        }

        if (TimelineZones.IsFhpBand(yPosition, _context.Viewport.ViewportHeight,
                _context.Viewport.BottomMargin))
        {
            return _fretAnchorTool;
        }

        return _highwayTool;
    }
}
