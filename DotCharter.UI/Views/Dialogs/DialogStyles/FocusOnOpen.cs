using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Dialogs;

/// <summary>
///     The dialog grammar's focus-on-open behaviour: mark a dialog's FIRST
///     input with <c>FocusOnOpen.IsTarget="True"</c> and it receives keyboard
///     focus when the dialog opens, so typing can start without a click.
/// </summary>
/// <remarks>
///     <para>
///         Overlay dialogs are always-attached hidden subtrees — "opening" is
///         the scrim's <c>IsVisible</c> flipping true, not an attach. The
///         behaviour finds the ancestor <c>Grid.DialogScrim</c> when the target
///         enters the logical tree, watches that one property, and posts the
///         focus at <see cref="DispatcherPriority.Loaded" /> — after the layout
///         pass that realizes the hidden subtree — a copy-pasted
///         <c>await Task.Delay(50)</c> handler per dialog only approximates that.
///     </para>
///     <para>
///         Lint: <c>UI/DialogFocusOnOpenTests</c> (a target outside a scrim, or
///         a Task.Delay focus handler creeping back, fails the gate). Runtime:
///         <c>Perf/DialogConstructionUiPerfSmokeTests.EveryFocusOnOpenTarget_ReceivesFocusWhenItsDialogOpens</c>.
///     </para>
/// </remarks>
public static class FocusOnOpen
{
    /// <summary>Marks the control that takes focus when its dialog opens.</summary>
    public static readonly AttachedProperty<bool> IsTargetProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsTarget", typeof(FocusOnOpen));

    static FocusOnOpen() => IsTargetProperty.Changed.AddClassHandler<Control>(OnIsTargetChanged);

    public static bool GetIsTarget(Control control) => control.GetValue(IsTargetProperty);

    /// <summary>
    ///     Focuses the marked target inside <paramref name="dialog" />, if it is on
    ///     screen. <c>Shell/DialogHost</c> calls this when it opens a dialog: there
    ///     the view itself is what becomes visible, so there is no scrim to watch.
    /// </summary>
    public static bool FocusTargetIn(Control dialog)
    {
        if (!dialog.IsEffectivelyVisible) return false;
        var target = dialog.GetLogicalDescendants().OfType<Control>().FirstOrDefault(GetIsTarget);
        return target is { IsEffectivelyVisible: true } && target.Focus();
    }

    public static void SetIsTarget(Control control, bool value) => control.SetValue(IsTargetProperty, value);

    private static void OnIsTargetChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // The scrim is only reachable once the target is in the tree; a dialog
        // view is constructed detached (prewarm, tests), so hook the lifecycle
        // rather than walking now.
        target.AttachedToLogicalTree += (_, _) => Hook(target);
        target.DetachedFromLogicalTree += (_, _) => Unhook(target);
        Hook(target);
    }

    private static readonly AttachedProperty<IDisposable?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("Subscription", typeof(FocusOnOpen));

    private static void Hook(Control target)
    {
        if (target.GetValue(SubscriptionProperty) is not null) return;

        var scrim = FindScrim(target);
        if (scrim is null) return;

        void OnScrimPropertyChanged(object? _, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && args.NewValue is true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Re-check: the dialog may have closed again before the
                    // posted turn ran (rapid Esc), and focus-stealing from
                    // whatever the user moved on to is worse than no focus.
                    if (target.IsEffectivelyVisible) target.Focus();
                }, DispatcherPriority.Loaded);
            }
        }

        scrim.PropertyChanged += OnScrimPropertyChanged;
        target.SetValue(SubscriptionProperty,
            new Unsubscriber(() => scrim.PropertyChanged -= OnScrimPropertyChanged));
    }

    private static void Unhook(Control target)
    {
        target.GetValue(SubscriptionProperty)?.Dispose();
        target.SetValue(SubscriptionProperty, null);
    }

    private static Control? FindScrim(Control target)
    {
        for (StyledElement? node = target; node is not null; node = node.Parent)
        {
            if (node is Grid grid && grid.Classes.Contains("DialogScrim"))
                return grid;
        }

        return null;
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
