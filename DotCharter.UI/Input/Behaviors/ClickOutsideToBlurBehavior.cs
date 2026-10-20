using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DotCharter.UI.Input.Behaviors;

/// <summary>
///     Attached behavior that adds the "click outside to release focus"
///     behaviour Avalonia doesn't provide out of the box.
///
///     Recommended pattern (from Avalonia maintainer @maxkatz6,
///     <see href="https://github.com/AvaloniaUI/Avalonia/discussions/12205" />):
///     on every pointer-press, call <see cref="InputElement.Focus"/> on the
///     hosting <see cref="TopLevel" />. For this to actually move focus, the
///     TopLevel must satisfy two conditions, both of which this behavior
///     enforces automatically when enabled:
///       1. <see cref="InputElement.Focusable" /> = true
///       2. A non-null <see cref="TemplatedControl.Background" /> (a Transparent
///          brush is fine — the window already has one in our case, but we
///          still set Transparent as a safety net so the behavior works on
///          any window without extra setup).
///
///     With those satisfied, focusing the window inside PointerPressed blurs
///     whichever input had focus, which fires LostFocus and stops keystrokes
///     from going to it. Avalonia's normal pointer focus shift then runs and
///     hands focus to whatever focusable control was actually clicked (Button,
///     TabItem, another TextBox, etc.) — so this is harmless when the click
///     does land on something focusable.
///
///     Enable on a window via:
///     <code>
///         &lt;Window xmlns:b="using:DotCharter.UI.Input.Behaviors"
///                 b:ClickOutsideToBlurBehavior.Enabled="True" /&gt;
///     </code>
/// </summary>
public static class ClickOutsideToBlurBehavior
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TopLevel, bool>(
            "Enabled", typeof(ClickOutsideToBlurBehavior));

    public static void SetEnabled(TopLevel obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static bool GetEnabled(TopLevel obj) => obj.GetValue(EnabledProperty);

    static ClickOutsideToBlurBehavior() => EnabledProperty.Changed.AddClassHandler<TopLevel>(OnEnabledChanged);

    private static void OnEnabledChanged(TopLevel target, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // Preconditions enforced by maintainer guidance: window must be
            // focusable and have a non-null background, otherwise PointerPressed
            // → Focus() silently no-ops.
            target.Focusable = true;
            target.Background ??= Brushes.Transparent;

            target.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        else
        {
            target.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Per Avalonia's documented pointer-focus behaviour, focusing the
        // TopLevel here releases focus from whatever input currently holds it.
        // Avalonia's standard pointer handling subsequently moves focus to the
        // actual click target if it is itself focusable — so we don't have to
        // care whether the click landed on a Button, a TabItem, blank space,
        // a Border, etc.
        if (sender is InputElement host) host.Focus();
    }
}
