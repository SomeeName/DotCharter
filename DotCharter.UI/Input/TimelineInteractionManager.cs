using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.Input.Tools;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input;

public class TimelineInteractionManager : IDisposable
{
    private readonly ToolCoordinator _coordinator;
    private readonly ChartRenderer _renderer;
    private readonly MainWindowViewModel _vm;

    // This manager OWNS its tool set (the tools are transient, one fresh set
    // per manager) and must dispose the disposable ones —
    // HighwayInteractionTool subscribes to HistoryChanged in its ctor, and an
    // undisposed transient subscription leaks one tool per pane float/redock.
    private readonly List<IEditorTool> _tools;

    /// <summary>
    ///     The container scope the tool set was resolved from
    ///     (<c>TimelineInteractionFactory.CreateToolScope</c>), or null when a
    ///     caller built the tools itself (tests).
    ///     <para>
    ///         Disposing the TOOLS is not enough. A container tracks every
    ///         disposable it constructs, and a transient resolved from the ROOT
    ///         provider is tracked by the root for the life of the process —
    ///         disposing it does not untrack it. Dropping the scope is what
    ///         actually releases the tools, and a manager is created per
    ///         stacked-row attach.
    ///     </para>
    /// </summary>
    private readonly IDisposable? _toolScope;

    public TimelineInteractionManager(
        MainWindowViewModel vm,
        ChartRenderer renderer,
        ITrackContextRegistry trackContexts,
        IEnumerable<IEditorTool> tools,
        IHistoryService history,
        Arrangement? boundArrangement = null,
        IDisposable? toolScope = null)
    {
        _toolScope = toolScope;
        _vm = vm;
        _renderer = renderer;
        _tools = tools.ToList();
        _coordinator = new ToolCoordinator(vm, renderer, trackContexts, _tools, history, boundArrangement);

        _renderer.PointerPressed += OnPointerPressed;
        _renderer.PointerMoved += OnPointerMoved;
        _renderer.PointerReleased += OnPointerReleased;
        _renderer.PointerExited += OnPointerExited;
    }

    public void Dispose()
    {
        _renderer.PointerPressed -= OnPointerPressed;
        _renderer.PointerMoved -= OnPointerMoved;
        _renderer.PointerReleased -= OnPointerReleased;
        _renderer.PointerExited -= OnPointerExited;
        foreach (var tool in _tools)
            (tool as IDisposable)?.Dispose();
        // …and the scope they were resolved from, or the container goes on
        // holding them for the life of the process (see _toolScope).
        _toolScope?.Dispose();
    }

    private bool _continuousActive;

    // Middle-drag viewport pan (0.16 6.6): grab-and-drag the visible window
    // without seeking the transport. Anchored on the press position so the
    // content tracks the pointer 1:1 regardless of move-event coalescing.
    private bool _panning;
    private double _panStartX;
    private double _panStartAnchorMs;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pressProps = e.GetCurrentPoint(_renderer).Properties;
        if (TimelinePanGesture.IsPanStart(
                pressProps.IsMiddleButtonPressed, pressProps.IsLeftButtonPressed,
                e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            // Middle-drag, or Alt+left-drag (the discoverable alias — see
            // TimelinePanGesture for why Space+drag was rejected).
            // Unlock Follow first — the setter freezes the anchor at the
            // playhead, so the grab starts from exactly what is on screen.
            _vm.ProjectState.TimelineFollowsPlayhead = false;
            _panning = true;
            _panStartX = e.GetPosition(_renderer).X;
            _panStartAnchorMs = _vm.ProjectState.TimelineViewAnchorMs;
            e.Pointer.Capture(_renderer);
            _renderer.Cursor = TimelineCursorPolicy.Cached(Avalonia.Input.StandardCursorType.SizeAll);
            _renderer.BeginContinuousRender();
            e.Handled = true;
            return;
        }

        // Pin the renderer to its vsync-aligned continuous frame loop while
        // the user is interacting. Without this, while playback is paused
        // the timeline only redraws on demand from individual property
        // changes and that arrives at irregular intervals — visually it
        // looks like a low frame rate even though seek throttling is 60 Hz.
        if (e.GetCurrentPoint(_renderer).Properties.IsLeftButtonPressed && !_continuousActive)
        {
            _renderer.BeginContinuousRender();
            _continuousActive = true;
        }
        _coordinator.HandlePointerPressed(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            // Dragging right reveals earlier time (content follows the hand).
            var deltaPx = _panStartX - e.GetPosition(_renderer).X;
            _vm.ProjectState.TimelineViewAnchorMs = TimelineViewMath.PanByPixels(
                _panStartAnchorMs, deltaPx, _vm.ProjectState.PixelsPerMs,
                _vm.ProjectState.EffectiveTimelineLengthMs);
            e.Handled = true;
            return;
        }

        UpdateCursor(e.GetPosition(_renderer).Y);
        _coordinator.HandlePointerMoved(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
    }

    private Avalonia.Input.StandardCursorType _cursorType = Avalonia.Input.StandardCursorType.Arrow;

    /// <summary>Zone-following cursor (TimelineCursorPolicy): cross over the
    /// lanes, arrow over the event rows and the ruler, size-we over the two
    /// length-editing bands, size-all while panning. Only writes the control's
    /// Cursor when the TYPE changes — this runs at pointer-move rate.</summary>
    private void UpdateCursor(double y)
    {
        var type = TimelineCursorPolicy.For(
            y, _renderer.ViewportHeight, _renderer.TopMargin, _renderer.BottomMargin, _panning);
        if (type == _cursorType) return;
        _cursorType = type;
        _renderer.Cursor = TimelineCursorPolicy.Cached(type);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            // Ends on the initiating button's release — middle for the classic
            // pan, left for the Alt+left alias; a pan only ever holds one.
            EndPan(e.Pointer);
            UpdateCursor(e.GetPosition(_renderer).Y);
            e.Handled = true;
            return;
        }

        _coordinator.HandlePointerReleased(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
        if (_continuousActive)
        {
            _renderer.EndContinuousRender();
            _continuousActive = false;
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            // Exit events can fire while the button is still held (see the
            // marquee note in HighwayInteractionTool) — while we still hold
            // pointer capture the pan continues; only a real capture loss
            // ends it here, so the continuous-render pin can never leak.
            if (!ReferenceEquals(e.Pointer.Captured, _renderer)) EndPan(e.Pointer);
            return;
        }

        _coordinator.HandlePointerExited(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
        if (_continuousActive)
        {
            _renderer.EndContinuousRender();
            _continuousActive = false;
        }
    }

    private void EndPan(IPointer pointer)
    {
        _panning = false;
        if (ReferenceEquals(pointer.Captured, _renderer)) pointer.Capture(null);
        _renderer.EndContinuousRender();
    }
}
