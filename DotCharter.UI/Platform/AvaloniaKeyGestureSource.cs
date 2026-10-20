using Avalonia.Input;
using DotCharter.Services.Infrastructure.Input;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IKeyGestureSource" /> over Avalonia key events: a view hands a
///     key press to <see cref="Publish" />, and <see cref="Map" /> is the one
///     translation from Avalonia's <see cref="Key" /> to <see cref="ScreenKey" />.
/// </summary>
public sealed class AvaloniaKeyGestureSource : IKeyGestureSource
{
    public event EventHandler<ScreenKeyEventArgs>? KeyPressed;

    /// <summary>Offers a key press to the subscribed screens; true when one
    /// handled it. A key no screen can bind is not offered.</summary>
    public bool Publish(Key key, bool textInputFocused)
    {
        var mapped = Map(key);
        if (mapped == ScreenKey.None) return false;
        var args = new ScreenKeyEventArgs(mapped, textInputFocused);
        KeyPressed?.Invoke(this, args);
        return args.Handled;
    }

    public static ScreenKey Map(Key key) => key switch
    {
        Key.Enter => ScreenKey.Enter,
        Key.Escape => ScreenKey.Escape,
        Key.Space => ScreenKey.Space,
        Key.Tab => ScreenKey.Tab,
        Key.Back => ScreenKey.Backspace,
        Key.Left => ScreenKey.Left,
        Key.Right => ScreenKey.Right,
        Key.Up => ScreenKey.Up,
        Key.Down => ScreenKey.Down,
        Key.PageUp => ScreenKey.PageUp,
        Key.PageDown => ScreenKey.PageDown,
        Key.Home => ScreenKey.Home,
        Key.End => ScreenKey.End,
        >= Key.A and <= Key.Z => ScreenKey.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => ScreenKey.D0 + (key - Key.D0),
        _ => ScreenKey.None
    };
}
