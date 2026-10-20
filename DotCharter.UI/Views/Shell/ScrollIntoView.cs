using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     <c>ScrollIntoView.WhenTrue="{Binding IsHighlighted}"</c>: the control is
///     brought into view each time the bound flag turns true — the keyboard's
///     row in a results list scrolls with the highlight while focus stays in
///     the search box. A view-side concern the view model must not know about.
/// </summary>
public static class ScrollIntoView
{
    public static readonly AttachedProperty<bool> WhenTrueProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("WhenTrue", typeof(ScrollIntoView));

    static ScrollIntoView()
    {
        WhenTrueProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true) control.BringIntoView();
        });
    }

    public static bool GetWhenTrue(Control control) => control.GetValue(WhenTrueProperty);

    public static void SetWhenTrue(Control control, bool value) => control.SetValue(WhenTrueProperty, value);
}
