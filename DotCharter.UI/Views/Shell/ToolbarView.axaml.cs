using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views.Shell;

public partial class ToolbarView : UserControl
{
    /// <summary>How long the pointer rests on undo/redo before its history preview opens.</summary>
    internal static readonly TimeSpan HistoryPreviewDwell = TimeSpan.FromMilliseconds(350);

    // Hover-opened undo/redo history previews. Both timers run ONLY between a
    // pointer event and the open/hide it delays (or its cancellation) — they
    // provably stop, per the UI timer rule.
    private readonly DispatcherTimer _historyCloseTimer;
    private readonly DispatcherTimer _historyOpenTimer;
    private readonly HoverDwell<Control> _historyDwell = new();
    private Control? _openHistoryAnchor;

    private MainWindowViewModel? _subscribedShell;

    public ToolbarView()
    {
        InitializeComponent();
        _historyCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _historyCloseTimer.Tick += (_, _) =>
        {
            _historyCloseTimer.Stop();
            CloseOpenHistoryFlyout();
        };
        _historyOpenTimer = new DispatcherTimer { Interval = HistoryPreviewDwell };
        _historyOpenTimer.Tick += (_, _) =>
        {
            _historyOpenTimer.Stop();
            if (_historyDwell.Elapsed() is { } anchor) OpenHistoryPreview(anchor);
        };

        DataContextChanged += (_, _) => ResubscribePopoverRequests();
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Overflow affordance (P2-16): the » button exists exactly while the
        // core rail is narrower than its content. ScrollChanged fires on every
        // extent/viewport change (including resizes), so no extra size hook.
        CoreRailScroller.ScrollChanged += (_, _) => UpdateOverflowVisibility();
        CoreRailScroller.SizeChanged += (_, _) => UpdateOverflowVisibility();
    }

    /// <summary>Pure so the rule is testable without a layout pass: the rail
    /// overflows when its content wants more width than it was given.</summary>
    internal static bool RailOverflows(double extentWidth, double viewportWidth)
        => extentWidth > viewportWidth + 0.5;

    private void UpdateOverflowVisibility()
        => ToolbarOverflowButton.IsVisible =
            RailOverflows(CoreRailScroller.Extent.Width, CoreRailScroller.Viewport.Width);

    // ── the keybinds that open Mix / Tools / Tracks ─────────────────────────

    /// <summary>
    ///     `Play Menus.dc.html` prints <c>ALT+M</c>, <c>ALT+T</c> and
    ///     <c>ALT+A</c> in those three footers, and they are real
    ///     rebindable keybinds rather than blank caps. The shell raises the
    ///     request; this view owns the buttons the flyouts hang off, so the open
    ///     happens here — the same split as Reset Layout and the timeline
    ///     pop-out.
    /// </summary>
    private void OnPopoverRequested(TransportPopover which)
    {
        // A hover preview left open would sit over whatever opens next — and so
        // would the overflow panel, whose group rows route through this method.
        CloseOpenHistoryFlyout();
        if (ToolbarOverflowButton.Flyout is { IsOpen: true } overflow) overflow.Hide();

        var button = which switch
        {
            TransportPopover.Mix => MixFlyoutButton,
            TransportPopover.Tempo => TempoFlyoutButton,
            TransportPopover.Tools => ToolsFlyoutButton,
            TransportPopover.Tracks => TracksFlyoutButton,
            _ => null
        };
        if (button?.Flyout is not { } flyout) return;

        // Pressing the chord again closes it — that is what a menu key does, and
        // re-showing an open flyout re-runs placement and blinks.
        if (flyout.IsOpen) flyout.Hide();
        else flyout.ShowAt(button);
    }

    /// <summary>
    ///     Re-points the subscription at whatever view model is current, and
    ///     drops the previous one. The shell is a DI singleton and this view is
    ///     rebuilt whenever the editor chrome is re-hosted, so subscribing once
    ///     in the constructor would leave a dead view listening for the rest of
    ///     the session — the leak `ViewSubscriptionLeakGuardTests` exists for.
    /// </summary>
    private void ResubscribePopoverRequests()
    {
        Unsubscribe();
        if (DataContext is not MainWindowViewModel shell) return;
        shell.PopoverRequested += OnPopoverRequested;
        _subscribedShell = shell;
    }

    private void Unsubscribe()
    {
        _historyOpenTimer.Stop();
        _historyCloseTimer.Stop();
        if (_subscribedShell is null) return;
        _subscribedShell.PopoverRequested -= OnPopoverRequested;
        _subscribedShell = null;
    }

    private void CloseOpenHistoryFlyout()
    {
        if (_openHistoryAnchor != null) FlyoutBase.GetAttachedFlyout(_openHistoryAnchor)?.Hide();
        _openHistoryAnchor = null;
    }

    private void HistoryButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control anchor) return;
        _historyCloseTimer.Stop();
        _historyOpenTimer.Stop();

        // A pass over the toolbar must open nothing: arm the dwell instead, unless
        // a preview is already open (moving undo -> redo switches at once).
        var aPreviewIsOpen = _openHistoryAnchor != null &&
                             FlyoutBase.GetAttachedFlyout(_openHistoryAnchor) is { IsOpen: true };
        if (_historyDwell.Enter(anchor, aPreviewIsOpen)) OpenHistoryPreview(anchor);
        else _historyOpenTimer.Start();
    }

    private void OpenHistoryPreview(Control anchor)
    {
        if (DataContext is MainWindowViewModel vm) vm.ChartEditor.RefreshHistoryPreviews();

        if (!ReferenceEquals(_openHistoryAnchor, anchor)) CloseOpenHistoryFlyout();
        _openHistoryAnchor = anchor;
        // Re-showing an already-open flyout re-runs placement and can blink;
        // entering the anchor with its own flyout open just keeps it open.
        if (FlyoutBase.GetAttachedFlyout(anchor) is { IsOpen: true }) return;
        FlyoutBase.ShowAttachedFlyout(anchor);
    }

    private void HistoryButton_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control anchor) _historyDwell.Exit(anchor);
        if (_historyDwell.Pending is null) _historyOpenTimer.Stop();

        // Delayed close so the pointer can travel INTO the flyout to scroll;
        // entering the flyout content cancels the timer.
        _historyCloseTimer.Stop();
        _historyCloseTimer.Start();
    }

    private void HistoryFlyout_PointerEntered(object? sender, PointerEventArgs e) => _historyCloseTimer.Stop();

    private void HistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        // The undo/redo just ran — the open preview is stale. Refresh after
        // the command completes so a hovering pointer sees the new chain.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm) vm.ChartEditor.RefreshHistoryPreviews();
        }, DispatcherPriority.Background);
    }
}
