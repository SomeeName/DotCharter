using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using DotCharter.UI.Controls;
using DotCharter.UI.Controls.Layers;
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

    private readonly PinnedLabelHitRegistry? _pinnedRegistry;

    public TimelineInteractionManager(
        MainWindowViewModel vm,
        ChartRenderer renderer,
        ITrackContextRegistry trackContexts,
        IEnumerable<IEditorTool> tools,
        IHistoryService history,
        Arrangement? boundArrangement = null,
        IDisposable? toolScope = null,
        PinnedLabelHitRegistry? pinnedRegistry = null)
    {
        _toolScope = toolScope;
        _pinnedRegistry = pinnedRegistry;
        _vm = vm;
        _renderer = renderer;
        _tools = tools.ToList();
        _coordinator = new ToolCoordinator(vm, renderer, trackContexts, _tools, history, boundArrangement);

        _renderer.PointerPressed += OnPointerPressed;
        _renderer.PointerMoved += OnPointerMoved;
        _renderer.PointerReleased += OnPointerReleased;
        _renderer.PointerExited += OnPointerExited;
        _renderer.PointerCaptureLost += OnPointerCaptureLost;
    }

    public void Dispose()
    {
        _renderer.PointerPressed -= OnPointerPressed;
        _renderer.PointerMoved -= OnPointerMoved;
        _renderer.PointerReleased -= OnPointerReleased;
        _renderer.PointerExited -= OnPointerExited;
        _renderer.PointerCaptureLost -= OnPointerCaptureLost;
        EndPan(_panPointer);
        EndToolGesture(cancel: true);
        foreach (var tool in _tools)
            (tool as IDisposable)?.Dispose();
        // …and the scope they were resolved from, or the container goes on
        // holding them for the life of the process (see _toolScope).
        _toolScope?.Dispose();
    }

    private bool _continuousActive;
    private IPointer? _toolPointer;

    // Middle-drag viewport pan (0.16 6.6): grab-and-drag the visible window
    // without seeking the transport. Anchored on the press position so the
    // content tracks the pointer 1:1 regardless of move-event coalescing.
    private bool _panning;
    private IPointer? _panPointer;
    private double _panStartX;
    private double _panStartAnchorMs;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pressProps = e.GetCurrentPoint(_renderer).Properties;
        EndToolGesture(cancel: true);
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
            _panPointer = e.Pointer;
            e.Pointer.Capture(_renderer);
            _cursorType = Avalonia.Input.StandardCursorType.SizeAll;
            _renderer.Cursor = TimelineCursorPolicy.Cached(_cursorType);
            AttachPanEscape();
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
        if (pressProps.IsLeftButtonPressed || pressProps.IsRightButtonPressed)
        {
            _toolPointer = e.Pointer;
            _toolPointer.Capture(_renderer);
            AttachToolEscape();
        }
        _coordinator.HandlePointerPressed(e, e.GetPosition(_renderer), pressProps);
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

        UpdateCursor(e.GetPosition(_renderer));
        _coordinator.HandlePointerMoved(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
    }

    private Avalonia.Input.StandardCursorType _cursorType = Avalonia.Input.StandardCursorType.Arrow;

    /// <summary>Zone-following cursor (TimelineCursorPolicy): cross over the
    /// lanes, arrow over the event rows and the ruler, size-we over the two
    /// length-editing bands, size-all while panning. Only writes the control's
    /// Cursor when the TYPE changes — this runs at pointer-move rate.</summary>
    private void UpdateCursor(Point position)
    {
        var registry = _pinnedRegistry;
        var overBadge = false;
        if (registry != null)
        {
            overBadge = registry.TryHit(_renderer, position, out var badge);
            if (registry.SetHover(_renderer, overBadge ? badge.Kind : null)) _renderer.RequestNextFrameRendering();
        }

        var type = TimelineCursorPolicy.For(
            position.Y, _renderer.ViewportHeight, _renderer.TopMargin, _renderer.BottomMargin, _panning, overBadge);
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
            UpdateCursor(e.GetPosition(_renderer));
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(_toolPointer, e.Pointer)) return;
        _coordinator.HandlePointerReleased(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
        EndToolGesture(cancel: false);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_pinnedRegistry?.SetHover(_renderer, null) == true)
            _renderer.RequestNextFrameRendering();
        if (_panning)
        {
            // Exit events can fire while the button is still held (see the
            // marquee note in HighwayInteractionTool) — while we still hold
            // pointer capture the pan continues; only a real capture loss
            // ends it here, so the continuous-render pin can never leak.
            if (!ReferenceEquals(e.Pointer.Captured, _renderer)) EndPan(e.Pointer);
            return;
        }

        if (ReferenceEquals(e.Pointer.Captured, _renderer)) return;
        EndToolGesture(cancel: true);
        _coordinator.HandlePointerExited(e, e.GetPosition(_renderer), e.GetCurrentPoint(_renderer).Properties);
    }

    // A lost capture (a popup, a context menu or a window taking the pointer) never
    // delivers the release: end a pan or cancel a tool gesture without committing previews.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_panning) EndPan(e.Pointer);
        if (ReferenceEquals(_toolPointer, e.Pointer)) EndToolGesture(cancel: true);
    }

    private void EndToolGesture(bool cancel)
    {
        var pointer = _toolPointer;
        if (pointer == null && !_continuousActive) return;
        _toolPointer = null;
        DetachToolEscape();
        if (cancel) _coordinator.CancelGesture();
        if (pointer != null && ReferenceEquals(pointer.Captured, _renderer)) pointer.Capture(null);
        if (!_continuousActive) return;
        _continuousActive = false;
        _renderer.EndContinuousRender();
    }

    private TopLevel? _toolEscapeHost;

    // Esc while a tool gesture holds the pointer cancels it the way a capture loss does: every
    // tool rolls its live edit back (a dragged tone change, beat or lyric returns) and nothing
    // is committed on the later release.
    private void AttachToolEscape()
    {
        DetachToolEscape();
        _toolEscapeHost = TopLevel.GetTopLevel(_renderer);
        _toolEscapeHost?.AddHandler(InputElement.KeyDownEvent, OnToolKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void DetachToolEscape()
    {
        _toolEscapeHost?.RemoveHandler(InputElement.KeyDownEvent, OnToolKeyDown);
        _toolEscapeHost = null;
    }

    private void OnToolKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _toolPointer == null) return;
        EndToolGesture(cancel: true);
        _renderer.RequestNextFrameRendering();
        e.Handled = true;
    }

    private void OnPanKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_panning) return;
        EndPan(_panPointer);
        e.Handled = true;
    }

    private TopLevel? _escapeHost;

    private void AttachPanEscape()
    {
        DetachPanEscape();
        _escapeHost = TopLevel.GetTopLevel(_renderer);
        _escapeHost?.AddHandler(InputElement.KeyDownEvent, OnPanKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void DetachPanEscape()
    {
        _escapeHost?.RemoveHandler(InputElement.KeyDownEvent, OnPanKeyDown);
        _escapeHost = null;
    }

    /// <summary>Ends the pan exactly once; the cursor goes back to the arrow and the next move re-derives the zone.</summary>
    private void EndPan(IPointer? pointer)
    {
        if (!_panning) return;
        _panning = false;
        DetachPanEscape();
        _panPointer = null;
        if (pointer != null && ReferenceEquals(pointer.Captured, _renderer)) pointer.Capture(null);
        _cursorType = Avalonia.Input.StandardCursorType.Arrow;
        _renderer.Cursor = TimelineCursorPolicy.Cached(_cursorType);
        _renderer.EndContinuousRender();
    }
}
