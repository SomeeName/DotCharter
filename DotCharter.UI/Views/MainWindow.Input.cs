// MainWindow: keyboard and pointer routing - the timeline handlers, the
// window tunnel/bubble keybind routes (and the pop-out windows that must
// mirror them), focus anchoring and the minimap drag.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.UI.Views;

public partial class MainWindow
{
    // 2. Force focus whenever the user clicks anywhere in the timeline grid. A
    // click ON the chart focuses the chart itself: it is the element
    //    with the automation peer, and its keys still bubble to this grid.
    private void Timeline_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is ChartRenderer { Focusable: true } renderer && renderer.Focus()) return;
        if (sender is Control control) control.Focus();
    }

    private void Timeline_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _wheelAdjuster is null) return;

        // A notch with no VERTICAL delta is not a notch. A precision touchpad
        // reports a horizontal two-finger scroll — and a Shift+wheel, which the
        // router says must adjust exactly like a bare one — as (±x, 0), and
        // `Delta.Y > 0 ? 1 : -1` read every one of those as a notch DOWNWARD: a
        // sideways flick over the lanes shortened the selected sustain, and
        // Ctrl+sideways zoomed out. TimelineWheelRouter.NotchesFrom is the one
        // answer (UI/Input/TimelineWheelRouterTests).
        var notches = TimelineWheelRouter.NotchesFrom(e.Delta.Y);
        if (notches == 0) return;

        // ONE routing decision (TimelineWheelRouter): Ctrl = zoom, Alt = pan,
        // bare/Shift = the edit chain — the OWNER'S mapping: the
        // wheel-edits workflow is charting muscle memory and stays the bare
        // gesture; pan is the opt-in, on the same Alt that already pans by
        // drag. (Do not flip this without an explicit owner decision.)
        switch (TimelineWheelRouter.Classify(
                    e.KeyModifiers.HasFlag(KeyModifiers.Control),
                    e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            case TimelineWheelRoute.Zoom:
                // While the viewport is panned away from the playhead (Follow
                // off), zoom around the pointer instead of the far-away playhead
                // (0.16 6.6); the anchor falls back to the viewport center when
                // the pointer isn't over a renderer. Follow ON = historical
                // zoom-around-playhead, untouched.
                double? anchorScreenX = null;
                if (!vm.ProjectState.TimelineFollowsPlayhead &&
                    (e.Source as Avalonia.Visual)?.FindAncestorOfType<ChartRenderer>(true) is { } renderer)
                {
                    anchorScreenX = e.GetPosition(renderer).X;
                }

                TimelineNavigation.ApplyZoom(vm.ProjectState,
                    notches > 0 ? ChartEditorConstants.ZoomInFactor : ChartEditorConstants.ZoomOutFactor,
                    anchorScreenX);
                RequestTimelineRedraw(sender);
                break;

            case TimelineWheelRoute.Adjust:
                // Plain wheel: the priority-ordered edit chain. A notch nothing
                // claimed does NOTHING — see
                // TimelineWheelRouter.UnclaimedBareNotch for why the pan
                // fallback is absent (in EOF the wheel is length,
                // never view). Pan is Alt+wheel.
                if (_wheelAdjuster.Apply(vm, notches) == WheelAdjustmentResult.Changed)
                {
                    RequestTimelineRedraw(sender);
                }
                else if (TimelineWheelRouter.UnclaimedBareNotch == TimelineWheelFallback.Pan)
                {
                    TimelineNavigation.PanByWheel(vm.ProjectState, e.Delta.Y);
                    RequestTimelineRedraw(sender);
                }

                break;

            default:
                // Alt+wheel pans the viewport — NEVER a transport seek and
                // NEVER an edit. Fractional deltas (precision touchpads) pan
                // proportionally.
                TimelineNavigation.PanByWheel(vm.ProjectState, e.Delta.Y);
                RequestTimelineRedraw(sender);
                break;
        }

        e.Handled = true;
    }

    private void RequestTimelineRedraw(object? sender)
    {
        var renderer = sender as ChartRenderer ?? this.FindControl<ChartRenderer>("MainChartRenderer");
        renderer?.RequestNextFrameRendering();
        this.FindControl<MinimapOverviewControl>("MinimapOverview")?.InvalidateVisual();
    }

    /// <summary>What a key press that ran a verb does after it, for a verb a MIDI
    /// controller ran (<see cref="Input.MidiBindingDispatch.Dispatched" />).</summary>
    internal void RequestChartFrame() => RequestTimelineRedraw(null);

    private void Timeline_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (vm.KeybindHandler.HandleKeyPress(e.Key, ctrl, shift, alt))
        {
            var chartRenderer = this.FindControl<ChartRenderer>("MainChartRenderer");
            chartRenderer?.RequestNextFrameRendering();
            e.Handled = true;
        }
    }

    // Catches a small set of global hotkeys (currently just Ctrl+Space for
    // lyric tapping) before any focused control can swallow them. We keep
    // this list intentionally narrow — every other shortcut still routes
    // through Timeline_KeyDown so that text inputs etc. remain unaffected.
    /// <summary>
    /// Anchor keyboard focus on the FocusSink control. A Window is a focus
    /// SCOPE, not a focusable element — Window.Focus() does NOT make key
    /// events flow, which is why every earlier "fix" of the dead-Esc bug
    /// failed. The sink is a real zero-size focusable control in the tree.
    /// </summary>
    internal void AnchorFocus() => this.FindControl<Control>("FocusSink")?.Focus();

    internal void AnchorFocusIfLost()
    {
        if (FocusManager?.GetFocusedElement() == null) AnchorFocus();
    }

    // When the window tunnel claims a key as a global shortcut (play/pause,
    // Ctrl+Space) it marks the KeyDown handled — but a focused Button/ToggleButton
    // fires its click on the matching Space KEY-UP (Avalonia's default
    // ClickMode.Release), NOT the KeyDown. The KeyDown tunnel therefore only stops
    // half of the "Space = click": the paired KeyUp still reaches the focused
    // control and flips it, so a clicked ribbon-row toggle (e.g. Sections) untoggled
    // and retoggled on every play/pause (owner report, v0.32.x). This remembers the
    // key we consumed so GlobalKeyUpPreview can swallow its release once.
    // The stale-arm escape hatches live in ActivationKeyUpLatch (pure, tested).
    private readonly ActivationKeyUpLatch _activationKeyUp = new();

    private void GlobalKeyDownPreview(object? sender, KeyEventArgs e)
    {
        // A different key arriving while the latch is armed proves the release
        // it was waiting for went somewhere else (dialog, native child window,
        // alt-tab); drop it rather than let it swallow an unrelated later one.
        _activationKeyUp.NoteKeyDown(e.Key);

        if (e.Handled) return;
        if (DataContext is not MainWindowViewModel vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Every branch below is gated through GlobalChordGate rather than
        // claiming its key outright. Skipping the gate entirely would make F11/Esc/Ctrl+Shift+P/Ctrl+Space
        // impossible to rebind (a focused KeybindCaptureBox would never see them) and Ctrl+Space
        // fire while the user typed in the Lyrics inspector. The rules and
        // their deliberate exceptions live in that file, side by side.
        var gate = BuildChordGateContext(vm);

        // Command palette: Ctrl+Shift+P toggles it from anywhere, and while it
        // is open the navigation keys are handled here (window tunnel) so they
        // win regardless of which control holds focus. Typing still flows to the
        // search box — only these specific keys are intercepted.
        if (e.Key == Key.P && ctrl && shift && !alt && GlobalChordGate.AllowsPaletteToggle(gate))
        {
            vm.CommandPalette.Toggle();
            e.Handled = true;
            return;
        }

        if (GlobalChordGate.AllowsPaletteNavigation(gate))
        {
            switch (e.Key)
            {
                case Key.Escape:
                    vm.CommandPalette.Close();
                    e.Handled = true;
                    return;
                case Key.Enter when !ctrl && !shift && !alt:
                    vm.CommandPalette.ExecuteSelected();
                    e.Handled = true;
                    return;
                case Key.Down when !ctrl && !shift && !alt:
                    vm.CommandPalette.MoveSelection(1);
                    e.Handled = true;
                    return;
                case Key.Up when !ctrl && !shift && !alt:
                    vm.CommandPalette.MoveSelection(-1);
                    e.Handled = true;
                    return;
            }
        }

        // ALL Esc handling lives at the WINDOW tunnel level. The routed
        // handler in DialogContainerView only fires when focus happens to be
        // inside the dialog overlay — which it never is on the Play Mode
        // surfaces (song select, stats, results) or during a run (the native
        // host may even own Win32 focus). HandleEscapeKey has the complete
        // priority chain: open dialog → gameplay pause/resume → Play Mode
        // back-navigation → clear editor selection. The editor branch (clear
        // selection) is harmless to run from here too.
        if (e.Key == Key.Escape && !ctrl && !shift && !alt && GlobalChordGate.AllowsEscape(gate))
        {
            vm.HandleEscapeKey();
            e.Handled = true;
            return;
        }

        // ── The LAUNCH screen's two printed shortcuts ──────────────────────
        // Its footer prints "ENTER START · S SETTINGS · ESC QUIT", and all three
        // have to be true — a shortcut a screen advertises and does not honour
        // is worse than no shortcut at all. Esc is handled above, on the app's
        // ONE Esc route. These two are claimed here, at the window tunnel, for
        // the same reason play/pause is: the launch screen has no single focus
        // owner, so a bubble route would depend on where focus happened to land.
        // Both stand down while a text input holds the keyboard (the screen's
        // own new-profile and guest-name fields), and Enter additionally stands
        // down for a focused Button so the screen stays arrow-key navigable.
        if (e.Key == Key.Enter && !ctrl && !shift && !alt && GlobalChordGate.AllowsLaunchStart(gate))
        {
            vm.PlayMode.EnterHomeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && !ctrl && !shift && !alt && GlobalChordGate.AllowsLaunchShortcut(gate))
        {
            vm.PlayMode.Settings.OpenGameplaySettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // F11 / Alt+Enter = borderless fullscreen toggle (game conventions).
        if (((e.Key == Key.F11 && !ctrl && !shift && !alt) ||
             (e.Key == Key.Enter && alt && !ctrl && !shift)) &&
            GlobalChordGate.AllowsFullScreenToggle(gate))
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && ctrl && !shift && !alt && GlobalChordGate.AllowsLyricTap(gate))
        {
            if (vm.KeybindHandler.HandleKeyPress(e.Key, ctrl, shift, alt))
            {
                this.FindControl<ChartRenderer>("MainChartRenderer")?.RequestNextFrameRendering();
                e.Handled = true;
                _activationKeyUp.Arm(e.Key);
                return;
            }
        }

        // Transport must survive focus landing on a button: a focused Button
        // consumes plain Space as "click" during bubbling, so the play/pause
        // chord is routed at the window tunnel where it wins regardless of
        // focus. Text inputs (typing a space) and the keybind capture box
        // (rebinding Space) keep it; open dialogs are blocked inside
        // HandleKeyPress. Editor surface only — matches Timeline_KeyDown.
        var playPause = vm.ProjectState.Keybinds.PlayPause;
        if (playPause.IsBound && playPause.Matches(e.Key, ctrl, shift, alt) &&
            GlobalChordGate.AllowsPlayPause(gate))
        {
            if (vm.KeybindHandler.HandleKeyPress(e.Key, ctrl, shift, alt))
            {
                this.FindControl<ChartRenderer>("MainChartRenderer")?.RequestNextFrameRendering();
                e.Handled = true;
                _activationKeyUp.Arm(e.Key);
            }
        }
    }

    /// <summary>
    /// Tunnel companion to <see cref="GlobalKeyDownPreview"/>: swallows the KeyUp
    /// of a key the tunnel just consumed as a global shortcut, so a focused
    /// Button/ToggleButton cannot fire its click on the release (Space activates
    /// on KeyUp under the default ClickMode.Release). Runs at the window tunnel so
    /// it wins before the focused control's bubble-phase handler — the same reason
    /// the KeyDown route is a tunnel handler.
    /// </summary>
    private void GlobalKeyUpPreview(object? sender, KeyEventArgs e)
    {
        if (!_activationKeyUp.ShouldSwallow(e.Key)) return;
        e.Handled = true;
    }

    /// <summary>
    /// Bubble-phase fallback route into the keybind registry — fires only for
    /// key presses NO control handled, so controls that use a key (ListBox
    /// arrows, Button Space/Enter, TabControl navigation) keep winning. While
    /// a text-input control has focus nothing is routed: typing, arrows and
    /// the TextBox's own Ctrl+Z undo must stay native.
    /// </summary>
    private void GlobalKeybindFallback(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.IsChartEditorVisible || vm.CommandPalette.IsOpen) return;
        if (!CanRouteGlobalKeybinds()) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (vm.KeybindHandler.HandleKeyPress(e.Key, ctrl, shift, alt))
        {
            this.FindControl<ChartRenderer>("MainChartRenderer")?.RequestNextFrameRendering();
            e.Handled = true;
        }
    }

    /// <summary>Global keybind routes must stand down while a text-input
    /// control (TextBox and everything embedding one — NumericUpDown,
    /// AutoCompleteBox — focuses the inner TextBox) or the keybind capture
    /// box owns the keyboard.</summary>
    private bool CanRouteGlobalKeybinds() => FocusManager?.GetFocusedElement() is not (TextBox or KeybindCaptureBox);

    /// <summary>Snapshots the focus/shell state the window tunnel evaluates its
    /// global chords against. Read once per key press so every branch sees the
    /// same focus — and so the rules themselves stay pure and testable
    /// (<see cref="GlobalChordGate" />).</summary>
    private GlobalChordGate.Context BuildChordGateContext(MainWindowViewModel vm)
    {
        var focused = FocusManager?.GetFocusedElement();
        return new GlobalChordGate.Context(
            TextInputFocused: focused is TextBox,
            KeybindCaptureFocused: focused is KeybindCaptureBox,
            PaletteOpen: vm.CommandPalette.IsOpen,
            HasOpenDialog: vm.Dialogs.HasOpenDialog,
            ChartEditorVisible: vm.IsChartEditorVisible,
            LaunchScreenVisible: vm.IsPlayModeVisible && vm.PlayMode.IsStartScreen,
            // CheckBox : ToggleButton : RadioButton : Button in Avalonia, so one
            // type test covers every control that already treats Enter as its
            // own activation key.
            ActivatableFocused: focused is Button,
            // A song-search box with text in it empties itself on Esc instead of
            // leaving the screen. The rule lives in SearchBoxKeys; this tunnel is
            // the only place that can let the box have the key at all.
            SearchBoxClaimsEscape: SearchBoxKeys.ClaimsEscape(focused));
    }

    private void Minimap_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && DataContext is MainWindowViewModel vm)
        {
            // Lock the chart renderer into its continuous (vsync-aligned)
            // frame loop while the user is scrubbing. Without this, while
            // playback is paused the renderer only repaints on demand and
            // the playhead motion looks stuttery.
            this.FindControl<ChartRenderer>("MainChartRenderer")?.BeginContinuousRender();
            _minimap.OnPointerPressed(control, e, vm, () => RequestTimelineRedraw(sender));
        }
    }

    private void Minimap_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Control control && DataContext is MainWindowViewModel vm)
            _minimap.OnPointerMoved(control, e, vm, () => RequestTimelineRedraw(sender));
    }

    private void Minimap_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _minimap.IsDragging;
        // The release carries the drop position, and the controller needs it: the
        // 60 Hz throttle almost always discards the last move of a drag, so this
        // is where that position is flushed.
        if (sender is Control control && DataContext is MainWindowViewModel vm)
            _minimap.OnPointerReleased(control, e, vm, () => RequestTimelineRedraw(sender));
        else
            _minimap.OnPointerCaptureLost();
        if (wasDragging)
            this.FindControl<ChartRenderer>("MainChartRenderer")?.EndContinuousRender();
    }

    private void Minimap_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        var wasDragging = _minimap.IsDragging;
        _minimap.OnPointerCaptureLost();
        if (wasDragging)
            this.FindControl<ChartRenderer>("MainChartRenderer")?.EndContinuousRender();
    }

    /// <summary>
    /// Mirrors MainWindow's two tunnel key routes onto a pop-out window.
    /// EVERY top-level this shell creates needs both, or every window-level
    /// shortcut is dead while that window holds focus — the 3D preview pop-out
    /// shipped without them, so clicking into it (which is the entire point: it
    /// is a large preview surface) left Esc, F11/Alt+Enter, Ctrl+Shift+P and
    /// play/pause doing nothing until the user clicked back to the main window.
    ///
    /// Both, never just the KeyDown: a focused Button/ToggleButton activates on
    /// the Space KEY-UP (Avalonia's default ClickMode.Release), so claiming only
    /// the KeyDown leaves the paired KeyUp live and a focused toggle in the
    /// pop-out flips on every play/pause. Hence one helper, not two call sites
    /// that can drift.
    /// </summary>
    private void WireGlobalKeyRoutes(Window window)
    {
        window.AddHandler(KeyDownEvent, GlobalKeyDownPreview, RoutingStrategies.Tunnel, true);
        window.AddHandler(KeyUpEvent, GlobalKeyUpPreview, RoutingStrategies.Tunnel, true);
        // Third member of the pair: once focus leaves, the KeyUp the tunnel is
        // owed will be delivered to another top-level (or not at all), so a
        // pending arm can only ever swallow an unrelated later release.
        window.Deactivated += ClearActivationLatch;
    }

    private void ClearActivationLatch(object? sender, EventArgs e) => _activationKeyUp.Clear();
}
