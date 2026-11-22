using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Marks a <see cref="TextBox" /> whose text binds with
///     <c>UpdateSourceTrigger=LostFocus</c> so that Enter commits it: the key
///     moves keyboard focus to the owning window, the box loses focus and the
///     binding pushes the text. Boxes with a commit COMMAND bind
///     <c>Enter</c> through <c>TextBox.KeyBindings</c> instead.
/// </summary>
public static class CommitOnEnter
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(CommitOnEnter));

    static CommitOnEnter() => IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnChanged);

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        box.KeyDown -= OnKeyDown;
        if (e.NewValue is true) box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox box) return;

        e.Handled = true;
        TopLevel.GetTopLevel(box)?.FocusManager?.Focus(null);
    }
}
