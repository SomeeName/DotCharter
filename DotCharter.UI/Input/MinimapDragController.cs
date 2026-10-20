using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Input;

/// <summary>
/// Owns the click-and-drag scrub-the-minimap state machine, including the
/// 60 Hz coalescing throttle. Lives outside <see cref="Views.MainWindow"/>
/// so the seek throttling and seek-on-press / no-seek-on-release contract
/// can be unit-tested without spinning up a real Window.
///
/// The throttle exists because pointer-move events fire at the mouse poll
/// rate (often 1000 Hz on gaming mice) and every move triggers a transport
/// seek (which flushes the BASS mixer queue, resets the MIDI scheduler,
/// kills active notes and re-anchors the wall clock). Firing those tens of
/// times per frame is wasted work and the audible jump is identical to a
/// single seek per frame, so we throttle to ~one seek per display frame.
/// PointerPressed always fires an immediate seek so the click position is
/// exact regardless of throttling.
///
/// PointerReleased re-seeks ONLY when the throttle actually dropped a move.
/// A skip would rest on the reasoning that "the last move was
/// already at the drop position" — which is false, and false in the common
/// case. Moves arrive at the mouse poll rate (1000 Hz on a gaming mouse), so
/// the move immediately before the button comes up is almost always inside the
/// 16 ms window and is therefore DISCARDED. The drag would then end wherever the
/// last move that happened to land on a slot boundary was, up to 16 ms of
/// pointer travel behind where the user let go — across a five-minute song at
/// full-strip speed that is seconds of chart, and it looks like the minimap
/// "snapping back" on release.
///
/// The re-seek is conditional rather than unconditional: a THIRD identical seek at the same position once lined up with
/// an ASIO callback and caused a 45 ms dropout. A release that dropped nothing
/// still issues nothing.
/// </summary>
public sealed class MinimapDragController
{
    /// <summary>~60 Hz coalescing for minimap drag.</summary>
    public const long MinimapSeekMinIntervalMs = 16;

    private readonly Func<long> _nowTicks;
    private long _lastSeekTicks;

    /// <summary>A move was thrown away by the throttle since the last seek, so the
    /// transport is NOT at the pointer. Cleared by every seek that happens.</summary>
    private bool _pendingSeek;

    public MinimapDragController() : this(() => Environment.TickCount64) { }

    /// <summary>Test ctor — substitute a deterministic clock.</summary>
    internal MinimapDragController(Func<long> nowTicks) => _nowTicks = nowTicks;

    public bool IsDragging { get; private set; }

    public void OnPointerPressed(Control control, PointerPressedEventArgs e,
        MainWindowViewModel vm, Action redraw)
    {
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        IsDragging = true;
        BeginDrag(_nowTicks());
        e.Pointer.Capture(control);
        Seek(control, e, vm, redraw);
        e.Handled = true;
    }

    public void OnPointerMoved(Control control, PointerEventArgs e,
        MainWindowViewModel vm, Action redraw)
    {
        if (!IsDragging) return;

        e.Handled = true;
        if (!TryTakeMoveSlot(_nowTicks())) return;

        Seek(control, e, vm, redraw);
    }

    /// <summary>
    ///     Ends the drag, and flushes the position the throttle dropped so the
    ///     transport finishes where the user let go rather than up to one frame
    ///     behind it.
    /// </summary>
    public void OnPointerReleased(Control control, PointerReleasedEventArgs e,
        MainWindowViewModel vm, Action redraw)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var wasDragging = IsDragging;
        IsDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        // Only when a move was actually discarded — a release that dropped
        // nothing must not re-issue a seek the transport already performed.
        if (wasDragging && TakePendingSeek()) Seek(control, e, vm, redraw);
    }

    /// <summary>
    ///     Capture loss ends the drag with no position to flush — the args carry
    ///     none, and the pointer may be off the control entirely. The pending flag
    ///     is dropped so it cannot leak into the next drag.
    /// </summary>
    public void OnPointerCaptureLost()
    {
        IsDragging = false;
        _pendingSeek = false;
    }

    /// <summary>Visible-for-testing: the press half of the throttle. A new drag
    /// starts with the transport AT the pointer (the press seeks), so nothing is
    /// pending.</summary>
    internal void BeginDrag(long nowMs)
    {
        _lastSeekTicks = nowMs;
        _pendingSeek = false;
    }

    /// <summary>
    ///     Visible-for-testing: the pure throttle decision. True when this move
    ///     gets a seek; false records that a move was DROPPED, which is what the
    ///     release then flushes.
    /// </summary>
    internal bool TryTakeMoveSlot(long nowMs)
    {
        if (nowMs - _lastSeekTicks < MinimapSeekMinIntervalMs)
        {
            _pendingSeek = true;
            return false;
        }

        _lastSeekTicks = nowMs;
        _pendingSeek = false;
        return true;
    }

    /// <summary>Visible-for-testing: whether the drag ended on a position the
    /// throttle threw away. Consuming, so a release can only ever flush once.</summary>
    internal bool TakePendingSeek()
    {
        var pending = _pendingSeek;
        _pendingSeek = false;
        return pending;
    }

    /// <summary>Visible-for-testing: pure fraction → ms math used by Seek.</summary>
    internal static double ComputeTargetMs(double pointerX, double width, double totalAudioLengthMs)
    {
        if (width <= 0 || totalAudioLengthMs <= 0) return -1;
        var fraction = Math.Max(0, Math.Min(1, pointerX / width));
        return totalAudioLengthMs * fraction;
    }

    /// <summary>
    ///     Visible-for-testing: a point within <see cref="SongMapMarks.SnapPx" /> of a
    /// bulk-edit preview mark seeks to the MARK — its chord or hand shape's
    ///     exact start — so a click on a mark lands on the sound it stands for rather
    ///     than up to a few pixels' worth of song beside it. Every seek of a drag
    ///     snaps the same way, or the release that flushes a dropped move would
    ///     undo the press's snap. Anything farther passes through unchanged.
    /// </summary>
    internal static double SnapToMark(double targetMs, double width, double totalMs, ChordUseHighlight highlight,
        ObservableCollection<Chord> levelChords, IList<BeatMarker> beats)
    {
        if (targetMs < 0 || highlight.IsEmpty || width <= 0) return targetMs;
        var toleranceMs = SongMapMarks.SnapPx * totalMs / width;
        return highlight.Nearest(levelChords, beats, targetMs, toleranceMs) ?? targetMs;
    }

    private static void Seek(Control control, PointerEventArgs e, MainWindowViewModel vm, Action redraw)
    {
        // Effective length, not the audio length: a chart-only project (GP
        // import, empty-project flow) has TotalAudioLengthMs == 0 and this
        // gate made the whole song-map strip a dead surface there.
        var totalMs = vm.ProjectState.EffectiveTimelineLengthMs;
        if (control.Bounds.Width <= 0 || totalMs <= 0) return;

        var p = e.GetPosition(control);
        var editor = vm.ChartEditor;
        var targetMs = SnapToMark(ComputeTargetMs(p.X, control.Bounds.Width, totalMs), control.Bounds.Width, totalMs,
            editor.UseHighlight, editor.PlacedChords, editor.Beats);
        if (targetMs < 0) return;

        vm.ChartEditor.Selection.ClearPreviews();
        vm.ChartEditor.Selection.ClearHovers();
        vm.ChartEditor.ExecuteSeek(targetMs);
        redraw();

        // NOTE: deliberately Trace, not Info. This handler runs ~60x/sec
        // during a minimap drag; at Info level it floods the log file and
        // the synchronous string-formatting + file write stalls the UI
        // thread enough to make the playhead/timeline visibly stutter
        // while paused. Serilog filters Trace below the configured min
        // level so this is a no-op in normal runs.
        var logger = vm.Logger;
        if (logger.IsEnabled(AppLogCategory.Input))
        {
            var fraction = Math.Max(0, Math.Min(1, p.X / control.Bounds.Width));
            logger.Trace(AppLogCategory.Input,
                $"Minimap seek. PointerX={p.X:0.###}, width={control.Bounds.Width:0.###}, fraction={fraction:0.######}, targetMs={targetMs:0.###}.");
        }
    }
}
