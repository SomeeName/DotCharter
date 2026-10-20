namespace DotCharter.Services.Infrastructure.Input;

/// <summary>
///     A key as a screen names it, free of any UI framework's key type, so the
///     rule "what does this key do on this screen" can live beside the screen's
///     view model instead of in view code. The UI maps its own key type onto
///     this (<c>DotCharter.UI/Platform/AvaloniaKeyGestureSource.Map</c>); keys a
///     screen never binds map to <see cref="None" />.
/// </summary>
public enum ScreenKey
{
    None,
    Enter,
    Escape,
    Space,
    Tab,
    Backspace,
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9
}

/// <summary>One key press offered to a screen.</summary>
public sealed class ScreenKeyEventArgs(ScreenKey key, bool textInputFocused) : EventArgs
{
    public ScreenKey Key { get; } = key;

    /// <summary>A text input owns the keyboard; a screen shortcut must stand down.</summary>
    public bool TextInputFocused { get; } = textInputFocused;

    /// <summary>Set by the screen that acted on the key, so it goes no further.</summary>
    public bool Handled { get; set; }
}

/// <summary>
///     What the Get Ready and gameplay screens read from the keyboard: key
///     presses in <see cref="ScreenKey" /> terms, raised on the UI thread.
///     Implemented by <c>DotCharter.UI/Platform/AvaloniaKeyGestureSource</c>.
/// </summary>
public interface IKeyGestureSource
{
    event EventHandler<ScreenKeyEventArgs>? KeyPressed;
}
